using System;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Radio.Relay;
using Jellyfin.Plugin.Radio.Stations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Radio.Api;

/// <summary>
/// The relay for stations with "skip the ad at the start" on. The server itself calls it:
/// the station's media source points here (on 127.0.0.1, see <see cref="RelayAddress"/>),
/// and every app gets the station through Jellyfin's own audio endpoints. Anonymous for
/// that reason, because Jellyfin fetches a media source without credentials. Only a call
/// with the station's key is served; a wrong key or slug answers 404 like any unknown
/// address.
/// </summary>
[ApiController]
[Route("Radio/Stream")]
[AllowAnonymous]
public sealed class RelayController : ControllerBase
{
    private readonly StationRelay _relay;
    private readonly RelayKeys _keys;

    /// <summary>Initializes a new instance of the <see cref="RelayController"/> class.</summary>
    /// <param name="relay">The relay.</param>
    /// <param name="keys">The relay keys.</param>
    public RelayController(StationRelay relay, RelayKeys keys)
    {
        _relay = relay;
        _keys = keys;
    }

    /// <summary>Streams a station without its pre-roll ad.</summary>
    /// <param name="slug">The station.</param>
    /// <param name="key">The station's relay key.</param>
    /// <returns>An endless audio/mpeg body; for HEAD only the headers (the station is not contacted).</returns>
    [HttpGet("{slug}")]
    [HttpHead("{slug}")]
    public async Task<IActionResult> Stream([FromRoute] string slug, [FromQuery(Name = RelayAddress.KeyParameter)] string? key)
    {
        var station = StationCatalog.Playable(Plugin.Config)
            .FirstOrDefault(s => s.SkipPreroll && string.Equals(s.Slug, slug, StringComparison.Ordinal));
        if (station is null || !_keys.IsValid(station.Slug, key))
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "no-cache, no-store";
        if (HttpMethods.IsHead(Request.Method))
        {
            Response.ContentType = "audio/mpeg";
            return new EmptyResult();
        }

        // Headers at once: Jellyfin waits for them before it answers the app, and the
        // relay plays silence until the station's live stream arrives.
        var aborted = HttpContext.RequestAborted;
        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = "audio/mpeg";
        Response.Headers.AcceptRanges = "none";
        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        await Response.StartAsync(aborted).ConfigureAwait(false);
        var player = StationRelay.IsPlayerRange(Request.Headers.Range.FirstOrDefault());
        if (!await _relay.RelayAsync(station, Response.Body, player, aborted).ConfigureAwait(false) && !aborted.IsCancellationRequested)
        {
            // The station failed before any live audio: cut the response off, so the app
            // reports an error instead of a short silent track that ended normally.
            HttpContext.Abort();
        }

        return new EmptyResult();
    }
}
