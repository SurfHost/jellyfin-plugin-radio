using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Radio.Configuration;
using Jellyfin.Plugin.Radio.Stations;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;

namespace Jellyfin.Plugin.Radio.Playback;

/// <summary>Turns a station into the channel item Jellyfin stores as an Audio item.</summary>
public static class ChannelItemFactory
{
    /// <summary>Creates the channel item for a playable station.</summary>
    /// <param name="station">A playable (validated, normalized) station.</param>
    /// <param name="modifiedUtc">When the station's published content last changed.</param>
    /// <param name="relayThroughServer">Whether clients must get the stream through the server.</param>
    /// <returns>The channel item.</returns>
    public static ChannelItemInfo Create(StationConfig station, DateTime modifiedUtc, bool relayThroughServer)
    {
        ArgumentNullException.ThrowIfNull(station);
        return new ChannelItemInfo
        {
            // The slug is the item's identity: Jellyfin hashes it with the channel name
            // into the item id, which favourites and playlists refer to.
            Id = station.Slug,
            Name = station.Name,
            Type = ChannelItemType.Media,
            MediaType = ChannelMediaType.Audio,

            // Song, never Podcast: Podcast makes Jellyfin create an AudioBook instead,
            // with a different item id.
            ContentType = ChannelMediaContentType.Song,
            IsLiveStream = true,
            Overview = StationCatalog.Overview(station),
            ImageUrl = string.IsNullOrEmpty(station.LogoUrl) ? null : station.LogoUrl,

            // Jellyfin re-saves an existing item (stream URL, media source) only when this
            // date moves forward.
            DateModified = modifiedUtc,
            MediaSources = new List<MediaSourceInfo> { MediaSourceFactory.Create(station, relayThroughServer) }
        };
    }
}
