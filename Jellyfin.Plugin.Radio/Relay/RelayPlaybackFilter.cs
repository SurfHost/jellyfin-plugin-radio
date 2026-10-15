using System;
using System.Threading.Tasks;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.Radio.Relay;

/// <summary>
/// Makes apps fetch a relayed station from Jellyfin, never from the station's media source
/// path. That path is the relay on 127.0.0.1, which only the server can reach, while
/// Jellyfin's PlaybackInfo answer marks an MP3 source as direct-playable. The web app,
/// Jellyfin Desktop and the Cast receiver would then open the path themselves; the Cast
/// receiver even ignores IsRemote and required headers. Changed to "direct stream", every
/// app asks for <c>/Audio/{id}/stream.mp3?static=true</c> on the address it already uses,
/// and the server fetches the relay itself.
/// </summary>
public sealed class RelayPlaybackFilter : IAsyncResultFilter
{
    /// <summary>Changes the relayed sources in a PlaybackInfo answer to "stream through the server".</summary>
    /// <param name="response">The answer, changed in place.</param>
    /// <returns>How many sources were changed.</returns>
    public static int Apply(PlaybackInfoResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var changed = 0;
        foreach (var source in response.MediaSources ?? Array.Empty<MediaSourceInfo>())
        {
            if (!RelayAddress.IsRelayed(source))
            {
                continue;
            }

            source.SupportsDirectPlay = false;
            source.SupportsDirectStream = true;
            source.SupportsTranscoding = false;
            source.TranscodingUrl = null;
            source.TranscodingContainer = null;
            changed++;
        }

        return changed;
    }

    /// <inheritdoc />
    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        if (context.ActionDescriptor is ControllerActionDescriptor action
            && string.Equals(action.ControllerName, "MediaInfo", StringComparison.Ordinal)
            && action.ActionName is "GetPlaybackInfo" or "GetPostedPlaybackInfo"
            && context.Result is ObjectResult { Value: PlaybackInfoResponse response })
        {
            Apply(response);
        }

        await next().ConfigureAwait(false);
    }
}
