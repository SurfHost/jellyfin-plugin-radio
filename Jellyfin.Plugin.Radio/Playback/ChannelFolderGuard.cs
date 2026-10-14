using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Radio.Playback;

/// <summary>
/// Refuses <c>GET /Channels/{id}/Items?folderId=X</c> when X is not a folder of that
/// channel, as far as the Radio channel is concerned: under the Radio channel only its own
/// category folders are accepted, and its folders are refused under any other channel.
/// Jellyfin 12.2 does not check that X belongs to the channel before acting on it.
/// </summary>
public sealed class ChannelFolderGuard : IAsyncActionFilter
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<ChannelFolderGuard> _logger;
    private RadioIds? _ids;

    /// <summary>Initializes a new instance of the <see cref="ChannelFolderGuard"/> class.</summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="logger">The logger.</param>
    public ChannelFolderGuard(ILibraryManager libraryManager, ILogger<ChannelFolderGuard> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>Returns true when a channel items request must be refused.</summary>
    /// <param name="channelId">The requested channel.</param>
    /// <param name="folderId">The requested folder, if any.</param>
    /// <param name="radioChannelId">The Radio channel's id.</param>
    /// <param name="radioFolderIds">The ids of the Radio channel's folders.</param>
    /// <returns>True for a foreign folder under the Radio channel, or a Radio folder under another channel.</returns>
    public static bool IsBlocked(Guid channelId, Guid? folderId, Guid radioChannelId, IReadOnlyCollection<Guid> radioFolderIds)
    {
        ArgumentNullException.ThrowIfNull(radioFolderIds);
        if (folderId is not { } folder || folder == Guid.Empty || folder == channelId)
        {
            return false;
        }

        return channelId == radioChannelId ? !radioFolderIds.Contains(folder) : radioFolderIds.Contains(folder);
    }

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        if (context.ActionDescriptor is ControllerActionDescriptor action
            && string.Equals(action.ControllerName, "Channels", StringComparison.Ordinal)
            && string.Equals(action.ActionName, "GetChannelItems", StringComparison.Ordinal)
            && context.ActionArguments.TryGetValue("channelId", out var channel) && channel is Guid channelId
            && context.ActionArguments.TryGetValue("folderId", out var folder))
        {
            // One reference, so a concurrent request sees both ids or neither.
            var ids = _ids ??= new RadioIds(
                ChannelItemLocator.ChannelId(_libraryManager),
                ChannelLayout.Folders.Select(f => ChannelItemLocator.FolderId(_libraryManager, f)).ToArray());

            if (IsBlocked(channelId, folder as Guid?, ids.Channel, ids.Folders))
            {
                _logger.LogWarning("Radio: refused a listing of channel {ChannelId} under folder {FolderId}, which is not a folder of that channel.", channelId, folder);
                context.Result = new BadRequestObjectResult("That folder does not belong to this channel.");
                return;
            }
        }

        await next().ConfigureAwait(false);
    }

    private sealed record RadioIds(Guid Channel, Guid[] Folders);
}
