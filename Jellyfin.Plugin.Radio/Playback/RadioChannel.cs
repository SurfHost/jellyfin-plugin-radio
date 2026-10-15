using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Radio.Configuration;
using Jellyfin.Plugin.Radio.Relay;
using Jellyfin.Plugin.Radio.Stations;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Radio.Playback;

/// <summary>
/// The "Radio" channel: live stations, in a folder per category (national, regional) when
/// both are in use. Jellyfin turns each station into an Audio item and plays it in its
/// music player, in every app that shows channels.
/// </summary>
public sealed class RadioChannel : IChannel, IHasCacheKey
{
    /// <summary>
    /// The channel name. Never change it: Jellyfin derives the channel id and every
    /// station's item id from it, so a new name would orphan favourites and playlists.
    /// </summary>
    public const string ChannelName = "Radio";

    /// <summary>The suffix Jellyfin 12.2 appends when hashing a channel item id (ChannelManager.GetIdToHash).</summary>
    public const string ItemIdSuffix = "16";

    private readonly StationStateStore _state;
    private readonly IChannelItemLocator _locator;
    private readonly IRelayAddress _relay;
    private readonly ILogger<RadioChannel> _logger;

    /// <summary>Initializes a new instance of the <see cref="RadioChannel"/> class.</summary>
    /// <param name="state">The publish state store.</param>
    /// <param name="locator">Finds where Jellyfin keeps each station.</param>
    /// <param name="relay">The relay address of stations that skip their pre-roll ad.</param>
    /// <param name="logger">The logger.</param>
    public RadioChannel(StationStateStore state, IChannelItemLocator locator, IRelayAddress relay, ILogger<RadioChannel> logger)
    {
        _state = state;
        _locator = locator;
        _relay = relay;
        _logger = logger;
    }

    /// <summary>Gets or sets where the settings come from (the plugin's; tests replace it).</summary>
    internal Func<PluginConfiguration> ConfigSource { get; set; } = () => Plugin.Config;

    /// <inheritdoc />
    public string Name => ChannelName;

    /// <inheritdoc />
    public string Description => "Live Nederlandse radiozenders.";

    /// <inheritdoc />
    public string DataVersion => CurrentHash();

    /// <inheritdoc />
    public string HomePageUrl => "https://github.com/SurfHost/jellyfin-plugin-radio";

    /// <inheritdoc />
    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

    /// <summary>The key Jellyfin hashes into a channel item's id (ChannelManager.GetIdToHash in 12.2).</summary>
    /// <param name="slug">The station slug, or a folder's external id.</param>
    /// <returns>The key for ILibraryManager.GetNewItemId with typeof(Audio), or typeof(Folder) for a folder.</returns>
    public static string ItemIdKey(string slug) => slug + ChannelName + ItemIdSuffix;

    /// <summary>The key Jellyfin hashes into the channel's own id (ChannelManager.GetInternalChannelId).</summary>
    /// <returns>The key for ILibraryManager.GetNewItemId with typeof(Channel).</returns>
    public static string ChannelIdKey() => "Channel " + ChannelName;

    /// <inheritdoc />
    public InternalChannelFeatures GetChannelFeatures() => new()
    {
        MediaTypes = new List<ChannelMediaType> { ChannelMediaType.Audio },
        ContentTypes = new List<ChannelMediaContentType> { ChannelMediaContentType.Song }
    };

    /// <inheritdoc />
    public bool IsEnabledFor(string userId) => IsEnabledFor(userId, ConfigSource().ExcludedUserIds);

    /// <summary>Returns false for a user the plugin hides the channel from.</summary>
    /// <param name="userId">The user id as Jellyfin passes it (32 hex digits).</param>
    /// <param name="excluded">The excluded users.</param>
    /// <returns>True when the user sees the channel.</returns>
    public static bool IsEnabledFor(string? userId, Guid[]? excluded) =>
        !Guid.TryParse(userId, out var id) || !(excluded ?? Array.Empty<Guid>()).Contains(id);

    /// <inheritdoc />
    public async Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var container = query.FolderId ?? ChannelLayout.Root;
        if (container != ChannelLayout.Root && ChannelLayout.CategoryOf(container) is null)
        {
            // Not one of this channel's folders.
            return new ChannelItemResult { TotalRecordCount = 0 };
        }

        var config = ConfigSource();
        var stations = StationCatalog.Playable(config);
        var relay = config.RelayThroughServer;
        // A station whose relay this server cannot reach plays straight from the station.
        var relayPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var station in stations.Where(s => s.SkipPreroll))
        {
            if (_relay.For(station.Slug) is { } path)
            {
                relayPaths[station.Slug] = path;
            }
        }

        var hashes = stations
            .Select(s => new KeyValuePair<string, string>(s.Slug, StationCatalog.StationHash(s, relay, relayPaths.GetValueOrDefault(s.Slug))))
            .ToList();
        var modified = await _state.TouchAsync(hashes, DateTime.UtcNow, cancellationToken).ConfigureAwait(false);

        var listing = ChannelLayout.List(container, stations, _locator.LocationOf);
        var items = listing.Folders
            .Select(f => ChannelItemFactory.CreateFolder(f, config.FolderName(ChannelLayout.CategoryOf(f)!.Value)))
            .Concat(listing.Stations.Select(s => ChannelItemFactory.Create(s, modified[s.Slug], relay, relayPaths.GetValueOrDefault(s.Slug))))
            .ToList();
        _logger.LogDebug("Radio: listing {Folders} folders and {Stations} stations in '{Container}'.", listing.Folders.Count, listing.Stations.Count, container);
        return new ChannelItemResult { Items = items, TotalRecordCount = items.Count };
    }

    /// <inheritdoc />
    public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken)
    {
        var assembly = typeof(RadioChannel).Assembly;
        var name = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(".Images.channel.png", StringComparison.Ordinal));
        var stream = name is null ? null : assembly.GetManifestResourceStream(name);
        return Task.FromResult(stream is null
            ? new DynamicImageResponse { HasImage = false }
            : new DynamicImageResponse { HasImage = true, Format = ImageFormat.Png, Protocol = MediaProtocol.File, Stream = stream });
    }

    /// <inheritdoc />
    public IEnumerable<ImageType> GetSupportedChannelImages() => new[] { ImageType.Primary, ImageType.Thumb };

    /// <inheritdoc />
    public string? GetCacheKey(string? userId) => CurrentHash();

    private string CurrentHash() => StationCatalog.ListingHash(ConfigSource());
}
