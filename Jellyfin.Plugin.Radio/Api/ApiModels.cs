using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Radio.Library;
using Jellyfin.Plugin.Radio.Relay;

namespace Jellyfin.Plugin.Radio.Api;

/// <summary>Request to test a stream URL.</summary>
public sealed class ProbeRequest
{
    /// <summary>Gets or sets the URL to test.</summary>
    public string Url { get; set; } = string.Empty;
}

/// <summary>Playback check of one station, as Jellyfin would serve it to a user.</summary>
public sealed class StationCheckDto
{
    /// <summary>Gets or sets the station slug.</summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>Gets or sets the station name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the station is enabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets the validation problems; a station with problems is not published.</summary>
    public IReadOnlyList<string> Errors { get; set; } = Array.Empty<string>();

    /// <summary>Gets or sets the Jellyfin item id the station maps to.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets a value indicating whether Jellyfin has the item.</summary>
    public bool ItemFound { get; set; }

    /// <summary>Gets or sets how many media sources Jellyfin offers for the item.</summary>
    public int Sources { get; set; }

    /// <summary>Gets or sets the first media source id.</summary>
    public string SourceId { get; set; } = string.Empty;

    /// <summary>Gets or sets the first media source container.</summary>
    public string Container { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the first source allows direct play.</summary>
    public bool DirectPlay { get; set; }

    /// <summary>Gets or sets a value indicating whether the first source describes an audio stream.</summary>
    public bool HasAudioStream { get; set; }

    /// <summary>Gets or sets the first media source path (the station URL).</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets what went wrong while asking Jellyfin for the sources.</summary>
    public string PlaybackError { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the station skips its pre-roll ad (plays through the relay).</summary>
    public bool SkipPreroll { get; set; }

    /// <summary>Gets or sets the result of the server calling its own relay for this station (HEAD; the station is not contacted). Empty when not relayed.</summary>
    public string RelayCheck { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether that call succeeded.</summary>
    public bool RelayOk { get; set; }
}

/// <summary>Result of the plugin page's Test button.</summary>
public sealed class TestReportDto
{
    /// <summary>Gets or sets the running plugin version.</summary>
    public string PluginVersion { get; set; } = string.Empty;

    /// <summary>Gets or sets the Radio channel's Jellyfin id.</summary>
    public Guid ChannelId { get; set; }

    /// <summary>Gets or sets a value indicating whether Jellyfin knows the Radio channel.</summary>
    public bool ChannelRegistered { get; set; }

    /// <summary>Gets or sets the number of configured stations.</summary>
    public int StationsConfigured { get; set; }

    /// <summary>Gets or sets the number of stations published.</summary>
    public int StationsPublished { get; set; }

    /// <summary>Gets or sets the user whose view was tested.</summary>
    public string TestUser { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether streams go through the server for every app.</summary>
    public bool RelayThroughServer { get; set; }

    /// <summary>Gets or sets the last sync pass, if any.</summary>
    public SyncReport? LastSync { get; set; }

    /// <summary>Gets or sets the per-station checks.</summary>
    public List<StationCheckDto> Stations { get; set; } = new();

    /// <summary>Gets or sets the per-user settings.</summary>
    public IReadOnlyList<UserAccessDto> Users { get; set; } = Array.Empty<UserAccessDto>();

    /// <summary>Gets or sets why this server cannot relay stations, or an empty string.</summary>
    public string RelayProblem { get; set; } = string.Empty;

    /// <summary>Gets or sets the song titles the relay has seen, newest first.</summary>
    public IReadOnlyList<NowPlayingEntry> NowPlaying { get; set; } = Array.Empty<NowPlayingEntry>();
}
