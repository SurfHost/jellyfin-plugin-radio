using Jellyfin.Plugin.Radio.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.Radio.Playback;

/// <summary>
/// Builds the one media source of a station. Every field here was checked against
/// Jellyfin 12.2's playback code; see docs/DESIGN.md section 1.1.
/// </summary>
public static class MediaSourceFactory
{
    /// <summary>Prefix of every media source id.</summary>
    public const string IdPrefix = "radio-";

    /// <summary>Creates the media source for a playable station.</summary>
    /// <param name="station">A playable (validated, normalized) station.</param>
    /// <param name="relayThroughServer">Whether clients must get the stream through the server.</param>
    /// <param name="relayPath">
    /// The plugin's relay address for a station with "skip the ad at the start" on
    /// (<see cref="Relay.RelayAddress"/>), or null to point at the station itself.
    /// </param>
    /// <returns>The media source.</returns>
    public static MediaSourceInfo Create(StationConfig station, bool relayThroughServer, string? relayPath = null)
    {
        var bitrate = station.BitrateKbps * 1000;
        return new MediaSourceInfo
        {
            // Never a GUID: for a signed-in user Jellyfin drops sources whose id parses as
            // a GUID that is not the item's own id (MediaSourceManager.GetStaticMediaSources).
            Id = IdPrefix + station.Slug,
            Name = station.Name,

            // The stable URL. For StreamTheWorld and DPG this is a redirect that every
            // player follows itself; the rotating node it lands on is never stored. A
            // relayed station points at the relay on 127.0.0.1 instead, which only the
            // server fetches (RelayPlaybackFilter keeps the apps from opening it).
            Path = relayPath ?? station.StreamUrl,
            Protocol = MediaProtocol.Http,
            Type = MediaSourceType.Default,
            Container = "mp3",

            // Remote: an app that can fetch remote media gets a redirect to the station and
            // fetches it itself. Jellyfin Web cannot, and is proxied by the server anyway.
            // Never for the relay: a redirect to 127.0.0.1 would fail on the app's device.
            IsRemote = !relayThroughServer && relayPath is null,
            IsInfiniteStream = true,
            RunTimeTicks = null,

            // Live audio cannot be transcoded (Jellyfin has no live HLS route for audio),
            // so everything rests on direct play of MP3, which every Jellyfin app supports.
            // SupportsTranscoding is overwritten from the user's permission anyway.
            SupportsDirectPlay = true,
            SupportsDirectStream = true,
            SupportsTranscoding = false,

            // No ffprobe of an endless stream, no live-stream opening (channel sources
            // cannot be opened), no extra headers (Jellyfin 12.2 sends only User-Agent
            // upstream, and the apps ignore them).
            SupportsProbing = false,
            RequiresOpening = false,
            RequiresClosing = false,
            Bitrate = bitrate,
            DefaultAudioStreamIndex = 0,

            // One complete audio stream. Without it Jellyfin forces a remote probe, and
            // DLNA renderers (Sonos) get a transcode instead of the MP3.
            MediaStreams = new[]
            {
                new MediaStream
                {
                    Type = MediaStreamType.Audio,
                    Index = 0,
                    Codec = "mp3",
                    BitRate = bitrate,
                    SampleRate = station.SampleRate,
                    Channels = station.Channels,
                    ChannelLayout = station.Channels == 1 ? "mono" : "stereo",
                    IsDefault = true
                }
            }
        };
    }
}
