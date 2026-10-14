namespace Jellyfin.Plugin.Radio.Configuration;

/// <summary>
/// Where a station appears in the channel. When stations of both categories are
/// published, each category is a folder; with one category the list stays flat.
/// </summary>
public enum StationCategory
{
    /// <summary>A national station (NPO, the commercial networks and their non-stop channels).</summary>
    National = 0,

    /// <summary>A regional broadcaster (NH, Omroep Brabant, Radio Gelderland, ...).</summary>
    Regional = 1
}
