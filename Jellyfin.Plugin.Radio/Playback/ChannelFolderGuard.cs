using System;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Radio.Playback;

/// <summary>
/// Refuses <c>GET /Channels/{radio}/Items?folderId=X</c> for any X other than the channel
/// itself. The Radio channel is flat, so a folder id is never legitimate here, and
/// Jellyfin 12.2 does not check that X belongs to the channel before acting on it.
/// </summary>
public sealed class ChannelFolderGuard : IAsyncActionFilter
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<ChannelFolderGuard> _logger;
    private Guid _channelId;

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
    /// <returns>True for a folder request against the Radio channel.</returns>
    public static bool IsBlocked(Guid channelId, Guid? folderId, Guid radioChannelId) =>
        channelId == radioChannelId
        && folderId is { } folder
        && folder != Guid.Empty
        && folder != radioChannelId;

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
            if (_channelId == Guid.Empty)
            {
                _channelId = _libraryManager.GetNewItemId(RadioChannel.ChannelIdKey(), typeof(MediaBrowser.Controller.Channels.Channel));
            }

            if (IsBlocked(channelId, folder as Guid?, _channelId))
            {
                _logger.LogWarning("Radio: refused a channel listing under folder {FolderId}; the Radio channel has no folders.", folder);
                context.Result = new BadRequestObjectResult("The Radio channel has no folders.");
                return;
            }
        }

        await next().ConfigureAwait(false);
    }
}
