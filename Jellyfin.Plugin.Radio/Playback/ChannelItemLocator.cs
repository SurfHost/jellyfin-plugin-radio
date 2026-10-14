using System;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.Radio.Playback;

/// <summary>Tells where Jellyfin keeps a station's item, so the channel can move it safely.</summary>
public interface IChannelItemLocator
{
    /// <summary>Returns where Jellyfin keeps a station's item now.</summary>
    /// <param name="slug">The station slug.</param>
    /// <returns><see cref="ChannelLayout.Root"/>, a folder id from <see cref="ChannelLayout.Folders"/>, or null when there is no item (or it is anywhere else).</returns>
    string? LocationOf(string slug);
}

/// <summary>
/// Looks the Radio channel's items up in Jellyfin's library. The ids follow Jellyfin 12.2's
/// channel code: the channel from "Channel Radio", every item from its external id plus
/// the channel name (see <see cref="RadioChannel.ItemIdKey"/>).
/// </summary>
public sealed class ChannelItemLocator : IChannelItemLocator
{
    private readonly ILibraryManager _libraryManager;

    /// <summary>Initializes a new instance of the <see cref="ChannelItemLocator"/> class.</summary>
    /// <param name="libraryManager">The library manager.</param>
    public ChannelItemLocator(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    /// <summary>Returns the Radio channel's item id.</summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <returns>The id.</returns>
    public static Guid ChannelId(ILibraryManager libraryManager)
    {
        ArgumentNullException.ThrowIfNull(libraryManager);
        return libraryManager.GetNewItemId(RadioChannel.ChannelIdKey(), typeof(MediaBrowser.Controller.Channels.Channel));
    }

    /// <summary>Returns the item id of one of the channel's folders.</summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="folder">The folder's external id.</param>
    /// <returns>The id.</returns>
    public static Guid FolderId(ILibraryManager libraryManager, string folder)
    {
        ArgumentNullException.ThrowIfNull(libraryManager);
        return libraryManager.GetNewItemId(RadioChannel.ItemIdKey(folder), typeof(Folder));
    }

    /// <summary>Returns a station's item id.</summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="slug">The station slug.</param>
    /// <returns>The id.</returns>
    public static Guid StationId(ILibraryManager libraryManager, string slug)
    {
        ArgumentNullException.ThrowIfNull(libraryManager);
        return libraryManager.GetNewItemId(RadioChannel.ItemIdKey(slug), typeof(Audio));
    }

    /// <inheritdoc />
    public string? LocationOf(string slug)
    {
        if (_libraryManager.GetItemById(StationId(_libraryManager, slug)) is not { } item)
        {
            return null;
        }

        if (item.ParentId == ChannelId(_libraryManager))
        {
            return ChannelLayout.Root;
        }

        foreach (var folder in ChannelLayout.Folders)
        {
            if (item.ParentId == FolderId(_libraryManager, folder))
            {
                return folder;
            }
        }

        return null;
    }
}
