using System;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Radio.Stations;

/// <summary>
/// Turns a pasted stream URL into the one to store. StreamTheWorld (Talpa, Mediahuis,
/// BNR) hands every listener a different node, e.g. <c>22713.live.streamtheworld.com:443/RADIO538.mp3</c>;
/// such a node URL stops working after a while. The stable entry point is the
/// <c>livestream-redirect</c> URL, which redirects to a live node on every request.
/// </summary>
public static partial class StreamUrlNormalizer
{
    /// <summary>The stable StreamTheWorld entry point.</summary>
    public const string StreamTheWorldRedirect = "https://playerservices.streamtheworld.com/api/livestream-redirect/";

    /// <summary>Returns the URL to store: trimmed, and StreamTheWorld node URLs rewritten.</summary>
    /// <param name="url">The URL as entered.</param>
    /// <returns>The normalized URL.</returns>
    public static string Normalize(string? url)
    {
        var trimmed = (url ?? string.Empty).Trim();
        var match = NodeUrl().Match(trimmed);
        if (!match.Success)
        {
            return trimmed;
        }

        var mount = match.Groups["mount"].Value;
        var extension = match.Groups["ext"].Value;
        if (mount.EndsWith("_SC", StringComparison.OrdinalIgnoreCase))
        {
            // Node mounts such as SLAM_MP3_SC belong to the redirect name SLAM_MP3.
            mount = mount[..^3];
        }

        if (extension.Length == 0)
        {
            extension = mount.Contains("AAC", StringComparison.OrdinalIgnoreCase) ? ".aac" : ".mp3";
        }

        return StreamTheWorldRedirect + mount + extension.ToLowerInvariant();
    }

    /// <summary>Returns true for a rotating StreamTheWorld node URL.</summary>
    /// <param name="url">The URL.</param>
    /// <returns>True when the URL points at a single node.</returns>
    public static bool IsStreamTheWorldNode(string? url) => NodeUrl().IsMatch((url ?? string.Empty).Trim());

    [GeneratedRegex(@"^https?://\d+\.live\.streamtheworld\.com(?::\d+)?/(?<mount>[A-Za-z0-9_]+?)(?<ext>\.mp3|\.aac)?(?:[?#].*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NodeUrl();
}
