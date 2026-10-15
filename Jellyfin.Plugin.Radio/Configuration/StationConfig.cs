namespace Jellyfin.Plugin.Radio.Configuration;

/// <summary>
/// One radio station. The slug is the station's identity: Jellyfin derives the item id
/// from it, so favourites and playlists follow the slug, not the name or the URL.
/// </summary>
public class StationConfig
{
    /// <summary>Gets or sets the immutable id, e.g. <c>npo-radio-2</c>. Lowercase letters, digits and dashes.</summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>Gets or sets the display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets a short group label shown with the station, e.g. <c>NPO</c> or <c>Regionaal</c>.</summary>
    public string Group { get; set; } = string.Empty;

    /// <summary>Gets or sets the folder the station is listed in: national or regional.</summary>
    public StationCategory Category { get; set; } = StationCategory.National;

    /// <summary>
    /// Gets or sets the stream URL: a direct MP3 stream over http(s), or a stable redirect
    /// to one (StreamTheWorld <c>livestream-redirect</c>, <c>stream.qmusic.nl</c>). Never a
    /// playlist (.m3u, .pls) and never a rotating StreamTheWorld node.
    /// </summary>
    public string StreamUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the logo URL (http(s), PNG or JPEG). Empty for none.</summary>
    public string LogoUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the measured bitrate in kbit/s.</summary>
    public int BitrateKbps { get; set; }

    /// <summary>Gets or sets the measured sample rate in Hz.</summary>
    public int SampleRate { get; set; }

    /// <summary>Gets or sets the measured channel count (1 or 2).</summary>
    public int Channels { get; set; }

    /// <summary>Gets or sets a value indicating whether the station is listed.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the station plays through the server, which
    /// drops the ad its stream server plays before the live stream (Qmusic, JOE). Every
    /// app then gets the station from this server instead of from the station.
    /// </summary>
    public bool SkipPreroll { get; set; }

    /// <summary>Returns a member-wise copy.</summary>
    /// <returns>The copy.</returns>
    public StationConfig Clone() => (StationConfig)MemberwiseClone();
}
