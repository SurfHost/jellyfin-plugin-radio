using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Radio.Configuration;
using Jellyfin.Plugin.Radio.Library;
using Jellyfin.Plugin.Radio.Playback;
using Jellyfin.Plugin.Radio.Stations;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Radio.Api;

/// <summary>
/// The plugin page's API. Administrators only: listening needs no plugin API at all,
/// because the stations are ordinary Jellyfin items. Jellyfin has no fallback
/// authorization policy, so these attributes are what protect the endpoints.
/// </summary>
[ApiController]
[Route("Radio")]
[Authorize(Policy = Policies.RequiresElevation)]
public sealed class RadioController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly StreamProbe _probe;
    private readonly RadioSync _sync;
    private readonly UserAudit _audit;

    /// <summary>Initializes a new instance of the <see cref="RadioController"/> class.</summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="mediaSourceManager">The media source manager.</param>
    /// <param name="probe">The stream probe.</param>
    /// <param name="sync">The sync service.</param>
    /// <param name="audit">The user audit.</param>
    public RadioController(
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        StreamProbe probe,
        RadioSync sync,
        UserAudit audit)
    {
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
        _probe = probe;
        _sync = sync;
        _audit = audit;
    }

    /// <summary>Returns the built-in Dutch station list.</summary>
    /// <returns>The stations.</returns>
    [HttpGet("Admin/Defaults")]
    public ActionResult<List<StationConfig>> Defaults()
    {
        NoStore();
        return DefaultStations.Load();
    }

    /// <summary>Fetches the start of a stream to check it and measure its format.</summary>
    /// <param name="request">The URL.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What was found.</returns>
    [HttpPost("Admin/Probe")]
    public async Task<ActionResult<ProbeResult>> Probe([FromBody] ProbeRequest request, CancellationToken cancellationToken)
    {
        NoStore();
        if (request is null || string.IsNullOrWhiteSpace(request.Url))
        {
            return BadRequest("Url is required.");
        }

        return await _probe.ProbeAsync(request.Url, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Validates a station list before it is saved.</summary>
    /// <param name="stations">The stations, in order.</param>
    /// <returns>For each station, its problems.</returns>
    [HttpPost("Admin/Validate")]
    public ActionResult<IReadOnlyList<IReadOnlyList<string>>> Validate([FromBody] List<StationConfig> stations)
    {
        NoStore();
        return Ok(StationValidator.ValidateAll(stations ?? new List<StationConfig>()));
    }

    /// <summary>Runs a sync pass now: lists the channel and applies names and logos.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What the pass did.</returns>
    [HttpPost("Admin/Resync")]
    public async Task<ActionResult<SyncReport>> Resync(CancellationToken cancellationToken)
    {
        NoStore();
        return await _sync.SyncAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Checks every station the way Jellyfin serves it to a user: does the item exist and
    /// which media source would an app get. Never probes the streams (they never end).
    /// </summary>
    /// <param name="userId">The user to test as; default a non-administrator.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The report.</returns>
    [HttpGet("Admin/Test")]
    public async Task<ActionResult<TestReportDto>> Test([FromQuery] Guid? userId, CancellationToken cancellationToken)
    {
        NoStore();
        var config = Plugin.Config;
        var channelId = _libraryManager.GetNewItemId(RadioChannel.ChannelIdKey(), typeof(MediaBrowser.Controller.Channels.Channel));
        var configured = StationCatalog.Configured(config);
        var errors = StationValidator.ValidateAll(configured);
        var published = StationCatalog.Playable(config).ToDictionary(s => s.Slug, StringComparer.Ordinal);
        var user = _audit.PickTestUser(userId ?? Guid.Empty, channelId);

        var report = new TestReportDto
        {
            PluginVersion = Plugin.ManifestVersion(typeof(Plugin).Assembly.GetName().Version ?? new Version(0, 0, 0)).ToString(),
            ChannelId = channelId,
            ChannelRegistered = _libraryManager.GetItemById(channelId) is MediaBrowser.Controller.Channels.Channel,
            StationsConfigured = configured.Count,
            StationsPublished = published.Count,
            TestUser = user?.Username ?? string.Empty,
            RelayThroughServer = config.RelayThroughServer,
            LastSync = _sync.LastReport,
            Users = _audit.Audit(channelId, config.ExcludedUserIds ?? Array.Empty<Guid>())
        };

        for (var i = 0; i < configured.Count; i++)
        {
            var station = configured[i];
            var check = new StationCheckDto
            {
                Slug = station.Slug,
                Name = station.Name,
                Enabled = station.Enabled,
                Errors = errors[i]
            };
            report.Stations.Add(check);
            if (!published.ContainsKey(station.Slug ?? string.Empty))
            {
                continue;
            }

            check.ItemId = _libraryManager.GetNewItemId(RadioChannel.ItemIdKey(station.Slug!), typeof(Audio));
            if (_libraryManager.GetItemById(check.ItemId) is not Audio item)
            {
                continue;
            }

            check.ItemFound = true;
            try
            {
                // allowMediaProbe must stay false: with true, a source without an audio
                // stream makes Jellyfin ffprobe the endless stream.
                var sources = await _mediaSourceManager.GetPlaybackMediaSources(item, user!, false, false, cancellationToken).ConfigureAwait(false);
                check.Sources = sources.Count;
                if (sources.Count > 0)
                {
                    var first = sources[0];
                    check.SourceId = first.Id ?? string.Empty;
                    check.Container = first.Container ?? string.Empty;
                    check.DirectPlay = first.SupportsDirectPlay;
                    check.HasAudioStream = first.MediaStreams?.Any(s => s.Type == MediaStreamType.Audio) ?? false;
                    check.Path = first.Path ?? string.Empty;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                check.PlaybackError = ex.Message;
            }
        }

        return report;
    }

    private void NoStore() => Response.Headers.CacheControl = "no-store";
}
