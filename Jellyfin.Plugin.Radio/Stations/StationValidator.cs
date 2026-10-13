using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using Jellyfin.Plugin.Radio.Configuration;

namespace Jellyfin.Plugin.Radio.Stations;

/// <summary>
/// Checks a station before it becomes a Jellyfin item. A station that fails is left out
/// of the channel (and reported on the plugin page) rather than published half-broken.
/// </summary>
public static partial class StationValidator
{
    /// <summary>The longest slug accepted.</summary>
    public const int MaxSlugLength = 64;

    private static readonly string[] PlaylistExtensions = { ".m3u", ".m3u8", ".pls", ".asx", ".xspf", ".mpd" };

    private static readonly int[] SampleRates = { 8000, 11025, 12000, 16000, 22050, 24000, 32000, 44100, 48000 };

    /// <summary>Returns the problems with one station; empty when it can be published.</summary>
    /// <param name="station">The station.</param>
    /// <returns>Human-readable problems.</returns>
    public static IReadOnlyList<string> Validate(StationConfig station)
    {
        ArgumentNullException.ThrowIfNull(station);
        var errors = new List<string>();

        if (!SlugPattern().IsMatch(station.Slug ?? string.Empty))
        {
            errors.Add("The id must be 1-64 lowercase letters, digits or dashes, starting with a letter or digit.");
        }

        if (string.IsNullOrWhiteSpace(station.Name))
        {
            errors.Add("The name is empty.");
        }
        else if (station.Name.Length > 100)
        {
            errors.Add("The name is longer than 100 characters.");
        }

        var url = StreamUrlNormalizer.Normalize(station.StreamUrl);
        if (!IsHttpUrl(url, out var uri))
        {
            errors.Add("The stream URL must be an absolute http or https URL.");
        }
        else if (PlaylistExtensions.Any(e => uri!.AbsolutePath.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add("The stream URL points to a playlist, not to the stream itself. Open the playlist and use the URL inside it.");
        }
        else if (uri!.AbsolutePath.EndsWith(".aac", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("Only MP3 streams are supported (live AAC cannot be cast or converted by Jellyfin). Most stations offer an MP3 stream too.");
        }

        if (!string.IsNullOrWhiteSpace(station.LogoUrl) && !IsHttpUrl(station.LogoUrl.Trim(), out _))
        {
            errors.Add("The logo URL must be an absolute http or https URL.");
        }

        if (station.BitrateKbps is < 8 or > 640 || !SampleRates.Contains(station.SampleRate) || station.Channels is < 1 or > 2)
        {
            errors.Add("The stream format (bitrate, sample rate, channels) is missing. Press Test to measure it.");
        }

        // Jellyfin stores the settings as XML and silently resets them all when the file
        // cannot be read back, which a pasted control character (a Word line break) causes.
        if (new[] { station.Name, station.Group, station.StreamUrl, station.LogoUrl }.Any(HasInvalidCharacters))
        {
            errors.Add("A field contains an invisible control character (often from copy and paste). Retype it.");
        }

        return errors;
    }

    /// <summary>Returns true when text contains a control character or one that XML cannot hold.</summary>
    /// <param name="text">The text.</param>
    /// <returns>True when the text cannot be stored safely.</returns>
    public static bool HasInvalidCharacters(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
                continue;
            }

            if (char.IsControl(c) || !XmlConvert.IsXmlChar(c))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Validates a whole list. A slug used twice is an error on every later use: the
    /// same slug would be the same Jellyfin item.
    /// </summary>
    /// <param name="stations">The stations in order.</param>
    /// <returns>For each station (same order), its problems.</returns>
    public static IReadOnlyList<IReadOnlyList<string>> ValidateAll(IReadOnlyList<StationConfig> stations)
    {
        ArgumentNullException.ThrowIfNull(stations);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<IReadOnlyList<string>>(stations.Count);
        foreach (var station in stations)
        {
            var errors = Validate(station).ToList();
            if (!string.IsNullOrEmpty(station.Slug) && !seen.Add(station.Slug))
            {
                errors.Add("Another station already uses the id '" + station.Slug + "'.");
            }

            result.Add(errors);
        }

        return result;
    }

    private static bool IsHttpUrl(string value, out Uri? uri) =>
        Uri.TryCreate(value, UriKind.Absolute, out uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && !string.IsNullOrEmpty(uri.Host);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex SlugPattern();
}
