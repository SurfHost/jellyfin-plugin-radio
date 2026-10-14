using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Radio.Configuration;
using Jellyfin.Plugin.Radio.Playback;
using Jellyfin.Plugin.Radio.Stations;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Radio.Library;

/// <summary>Outcome of one sync pass, shown on the plugin page.</summary>
public sealed class SyncReport
{
    /// <summary>Gets or sets when the pass ran (UTC).</summary>
    public DateTime RanUtc { get; set; }

    /// <summary>Gets or sets the number of stations published.</summary>
    public int Stations { get; set; }

    /// <summary>Gets or sets the number of station items found in Jellyfin.</summary>
    public int Items { get; set; }

    /// <summary>Gets or sets the number of items whose name, text, order or logo was updated.</summary>
    public int Updated { get; set; }

    /// <summary>Gets or sets the slugs whose item was not found.</summary>
    public List<string> Missing { get; set; } = new();

    /// <summary>Gets or sets why the pass stopped early, if it did.</summary>
    public string Error { get; set; } = string.Empty;
}

/// <summary>
/// Keeps the station items in step with the settings. Jellyfin creates, moves, updates and
/// deletes channel items itself when the channel and its folders are listed, but it sets
/// an item's name and text only when the item is new, and its image only when it has none.
/// So after listing the channel this pass applies renames, order and new logos to existing
/// items. Runs 30 seconds after startup, after every settings change, and every 6 hours.
/// </summary>
public sealed class RadioSync : BackgroundService
{
    /// <summary>Delay after startup, so the server finishes loading first.</summary>
    public static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);

    /// <summary>Time between passes when nothing changes.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    // Settings are often saved twice in a row (page + reload); wait for the dust to settle.
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(3);

    private readonly ILibraryManager _libraryManager;
    private readonly IChannelManager _channelManager;
    private readonly IApplicationPaths _applicationPaths;
    private readonly StationStateStore _state;
    private readonly ILogger<RadioSync> _logger;
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly SemaphoreSlim _running = new(1, 1);
    private bool _listening;

    /// <summary>Initializes a new instance of the <see cref="RadioSync"/> class.</summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="channelManager">The channel manager.</param>
    /// <param name="applicationPaths">Jellyfin's paths.</param>
    /// <param name="state">The publish state store.</param>
    /// <param name="logger">The logger.</param>
    public RadioSync(
        ILibraryManager libraryManager,
        IChannelManager channelManager,
        IApplicationPaths applicationPaths,
        StationStateStore state,
        ILogger<RadioSync> logger)
    {
        _libraryManager = libraryManager;
        _channelManager = channelManager;
        _applicationPaths = applicationPaths;
        _state = state;
        _logger = logger;
    }

    /// <summary>Gets the outcome of the last pass, or null before the first.</summary>
    public SyncReport? LastReport { get; private set; }

    /// <summary>
    /// The sort name that makes the apps list stations in the page's order. Jellyfin's
    /// apps sort channel items by name (the web app by IsFolder,SortName; Android Auto by
    /// SortName), so the position goes in front.
    /// </summary>
    /// <param name="position">Zero-based position in the published list.</param>
    /// <param name="name">The station name.</param>
    /// <returns>The forced sort name, e.g. "001 NPO Radio 1".</returns>
    public static string SortNameFor(int position, string name) =>
        (position + 1).ToString("D3", CultureInfo.InvariantCulture) + " " + name;

    /// <summary>Asks for a pass soon (after a settings change).</summary>
    public void Nudge()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // A pass is already pending.
        }
    }

    /// <summary>
    /// Brings the built-in stations into the settings. On first start it copies the whole
    /// built-in list, so the plugin page shows what the channel publishes. After an update
    /// it applies the built-in list's removals, renames, logos and categories to the
    /// stations the admin has not changed (see <see cref="DefaultStations.ApplyChangesTo"/>)
    /// and adds the built-in stations that are new since the list was last merged (see
    /// <see cref="DefaultStations.MergeNewInto"/>).
    /// </summary>
    /// <param name="config">The settings, changed in place.</param>
    /// <returns>What changed, for the log; null when nothing did.</returns>
    public static string? ApplyBuiltInStations(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var latest = DefaultStations.LatestVersion;
        if (!config.DefaultsLoaded)
        {
            config.Stations = DefaultStations.Load();
            config.DefaultsLoaded = true;
            config.DefaultsVersion = latest;
            return "loaded the built-in Dutch station list";
        }

        var merged = Version.TryParse(string.IsNullOrWhiteSpace(config.DefaultsVersion) ? DefaultStations.FirstVersion : config.DefaultsVersion, out var parsed)
            ? parsed
            : new Version(0, 0, 1);
        if (merged >= Version.Parse(latest))
        {
            return null;
        }

        config.Stations ??= new List<StationConfig>();
        var changes = DefaultStations.ApplyChangesTo(config.Stations, config.DefaultsVersion);
        var added = DefaultStations.MergeNewInto(config.Stations, config.DefaultsVersion);
        config.DefaultsVersion = latest;
        var done = new List<string>();
        if (changes.Removed > 0)
        {
            done.Add("removed " + changes.Removed.ToString(CultureInfo.InvariantCulture) + " retired built-in stations");
        }

        if (changes.Updated > 0)
        {
            done.Add("updated the name or logo of " + changes.Updated.ToString(CultureInfo.InvariantCulture) + " built-in stations");
        }

        if (changes.Categorized > 0)
        {
            done.Add("put " + changes.Categorized.ToString(CultureInfo.InvariantCulture) + " built-in stations in their category");
        }

        if (added > 0)
        {
            done.Add("added " + added.ToString(CultureInfo.InvariantCulture) + " new built-in stations");
        }

        return done.Count > 0 ? string.Join(", ", done) : "built-in station list is up to date";
    }

    /// <summary>Runs one pass now.</summary>
    /// <param name="cancellationToken">Cancels waiting for a pass that is already running.</param>
    /// <returns>What the pass did.</returns>
    public async Task<SyncReport> SyncAsync(CancellationToken cancellationToken)
    {
        await _running.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Once started, a pass runs to the end: stopping half-way would leave items
            // stale behind a fresh channel cache.
            var report = await SyncCoreAsync(CancellationToken.None).ConfigureAwait(false);
            LastReport = report;
            return report;
        }
        finally
        {
            _running.Release();
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Jellyfin keeps .NET's default for background services: an exception escaping
        // here stops the whole server. So nothing may escape.
        try
        {
            await Task.Delay(StartupDelay, stoppingToken).ConfigureAwait(false);
            ListenForSettingsChanges();
            try
            {
                var plugin = Plugin.Instance;
                var change = plugin is null ? null : ApplyBuiltInStations(plugin.Configuration);
                if (change is not null)
                {
                    plugin!.SaveConfiguration();
                    _logger.LogInformation("Radio: {Change}.", change);
                }
            }
            catch (Exception ex)
            {
                // The built-in list is still published (StationCatalog.Configured).
                _logger.LogWarning(ex, "Radio: could not save the built-in station list to the plugin settings.");
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var report = await SyncAsync(stoppingToken).ConfigureAwait(false);
                    if (report.Error.Length > 0)
                    {
                        _logger.LogWarning("Radio: sync stopped: {Error}", report.Error);
                    }
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    _logger.LogError(ex, "Radio: sync failed.");
                }

                if (await _signal.WaitAsync(Interval, stoppingToken).ConfigureAwait(false))
                {
                    await Task.Delay(Debounce, stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Radio: the sync service stopped.");
        }
    }

    private void ListenForSettingsChanges()
    {
        var plugin = Plugin.Instance;
        if (plugin is null || _listening)
        {
            return;
        }

        plugin.ConfigurationChanged += (_, _) => Nudge();
        _listening = true;
    }

    private async Task<SyncReport> SyncCoreAsync(CancellationToken cancellationToken)
    {
        var report = new SyncReport { RanUtc = DateTime.UtcNow };
        var stations = StationCatalog.Playable(Plugin.Config);
        report.Stations = stations.Count;

        // Creates the channel's own entity if Jellyfin has not done so yet.
        await _channelManager.GetChannelsInternalAsync(new ChannelQuery()).ConfigureAwait(false);
        var channelId = ChannelItemLocator.ChannelId(_libraryManager);
        if (_libraryManager.GetItemById(channelId) is not MediaBrowser.Controller.Channels.Channel)
        {
            report.Error = "The Radio channel is not registered with Jellyfin.";
            return report;
        }

        // Jellyfin caches a channel listing for 3 hours under a name derived from the
        // channel's data version. Going back to an earlier station list within those
        // hours would hit the old file, and the listing below would change nothing. The
        // folder holds only this listing cache; item data lives elsewhere.
        ClearListingCache(channelId);

        // Listing the channel and its folders makes Jellyfin create new items, move
        // stations to their folder, re-save changed ones and delete the ones no longer
        // listed. A station stays listed where it is until it has moved (ChannelLayout),
        // so moving never deletes it.
        await ListAsync(channelId, Guid.Empty, cancellationToken).ConfigureAwait(false);
        foreach (var folder in ChannelLayout.Folders)
        {
            var folderId = ChannelItemLocator.FolderId(_libraryManager, folder);
            if (_libraryManager.GetItemById(folderId) is Folder)
            {
                await ListAsync(channelId, folderId, cancellationToken).ConfigureAwait(false);
            }
        }

        // Once the stations have moved, list the root again: a folder that has become
        // empty and unused is dropped.
        ClearListingCache(channelId);
        await ListAsync(channelId, Guid.Empty, cancellationToken).ConfigureAwait(false);

        var config = Plugin.Config;
        for (var index = 0; index < ChannelLayout.Folders.Count; index++)
        {
            var folder = ChannelLayout.Folders[index];
            if (_libraryManager.GetItemById(ChannelItemLocator.FolderId(_libraryManager, folder)) is Folder item
                && await PatchFolderAsync(item, index, config.FolderName(ChannelLayout.CategoryOf(folder)!.Value), cancellationToken).ConfigureAwait(false))
            {
                report.Updated++;
            }
        }

        for (var position = 0; position < stations.Count; position++)
        {
            var station = stations[position];
            var id = ChannelItemLocator.StationId(_libraryManager, station.Slug);
            if (_libraryManager.GetItemById(id) is not Audio item)
            {
                report.Missing.Add(station.Slug);
                continue;
            }

            report.Items++;
            if (await PatchAsync(item, station, position, cancellationToken).ConfigureAwait(false))
            {
                report.Updated++;
            }
        }

        await _state.PruneAsync(stations.Select(s => s.Slug).ToList(), cancellationToken).ConfigureAwait(false);
        if (report.Updated > 0 || report.Missing.Count > 0)
        {
            _logger.LogInformation(
                "Radio: {Stations} stations, {Items} items, {Updated} updated, {Missing} missing.",
                report.Stations,
                report.Items,
                report.Updated,
                report.Missing.Count);
        }

        return report;
    }

    private Task ListAsync(Guid channelId, Guid parentId, CancellationToken cancellationToken) =>
        _channelManager.GetChannelItemsInternal(
            new InternalItemsQuery { ChannelIds = new[] { channelId }, ParentId = parentId, EnableTotalRecordCount = false },
            new Progress<double>(),
            cancellationToken);

    private void ClearListingCache(Guid channelId)
    {
        var folder = Path.Combine(_applicationPaths.CachePath, "channels", channelId.ToString("N", CultureInfo.InvariantCulture));
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Radio: could not clear the channel listing cache {Folder}.", folder);
        }
    }

    // Jellyfin names a folder itself; this keeps national before regional whatever the names.
    private async Task<bool> PatchFolderAsync(Folder item, int position, string name, CancellationToken cancellationToken)
    {
        var sortName = SortNameFor(position, name);
        if (string.Equals(item.ForcedSortName, sortName, StringComparison.Ordinal))
        {
            return false;
        }

        item.ForcedSortName = sortName;
        await _libraryManager.UpdateItemAsync(item, item.GetParent(), ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> PatchAsync(Audio item, StationConfig station, int position, CancellationToken cancellationToken)
    {
        var changed = false;
        if (!string.Equals(item.Name, station.Name, StringComparison.Ordinal))
        {
            item.Name = station.Name;
            changed = true;
        }

        var overview = StationCatalog.Overview(station);
        if (!string.Equals(item.Overview, overview, StringComparison.Ordinal))
        {
            item.Overview = overview;
            changed = true;
        }

        var sortName = SortNameFor(position, station.Name);
        if (!string.Equals(item.ForcedSortName, sortName, StringComparison.Ordinal))
        {
            item.ForcedSortName = sortName;
            changed = true;
        }

        // Jellyfin replaces a remote image with a local copy the first time it is shown, so
        // the item's image path says nothing about which logo it is; the state remembers.
        var applied = await _state.GetAppliedLogoAsync(station.Slug, cancellationToken).ConfigureAwait(false);
        if (applied is null)
        {
            // First pass for this item: Jellyfin gave it the logo listed when it was created.
            applied = item.HasImage(ImageType.Primary) ? station.LogoUrl : string.Empty;
            await _state.SetAppliedLogoAsync(station.Slug, applied, cancellationToken).ConfigureAwait(false);
        }

        var logoChanged = !string.Equals(applied, station.LogoUrl, StringComparison.Ordinal);
        if (logoChanged)
        {
            if (station.LogoUrl.Length > 0)
            {
                // A fresh date gives the image a new tag. Apps cache images by tag for a
                // year, and a remote image's tag otherwise only depends on the item.
                item.SetImage(new ItemImageInfo { Path = station.LogoUrl, Type = ImageType.Primary, DateModified = DateTime.UtcNow }, 0);
            }
            else if (item.GetImageInfo(ImageType.Primary, 0) is { } image)
            {
                item.RemoveImage(image);
            }

            changed = true;
        }

        // Keep metadata refreshes from overwriting the station's name and text.
        var locks = item.LockedFields ?? Array.Empty<MetadataField>();
        if (!locks.Contains(MetadataField.Name) || !locks.Contains(MetadataField.Overview))
        {
            item.LockedFields = locks.Union(new[] { MetadataField.Name, MetadataField.Overview }).ToArray();
            changed = true;
        }

        if (!changed)
        {
            return false;
        }

        await _libraryManager.UpdateItemAsync(item, item.GetParent(), ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        if (logoChanged)
        {
            await _state.SetAppliedLogoAsync(station.Slug, station.LogoUrl, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }
}
