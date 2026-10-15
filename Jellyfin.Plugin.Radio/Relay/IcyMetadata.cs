using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Radio.Relay;

/// <summary>
/// One ICY metadata block, e.g. <c>StreamTitle='Artist - Title';StreamUrl='';</c>. Stream
/// servers that insert ads (AdsWizz, used by Qmusic and JOE) add fields to it:
/// <c>adw_ad='true';durationMilliseconds='15024';insertionType='preroll';</c>.
/// </summary>
public sealed partial class IcyMetadata
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    private readonly Dictionary<string, string> _fields;

    private IcyMetadata(Dictionary<string, string> fields)
    {
        _fields = fields;
    }

    /// <summary>Gets the StreamTitle, or an empty string.</summary>
    public string Title => Get("StreamTitle");

    /// <summary>Gets a value indicating whether this block announces an inserted ad.</summary>
    public bool IsAd => string.Equals(Get("adw_ad"), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>Gets a value indicating whether this block announces an ad played before the live stream.</summary>
    public bool IsPreroll => IsAd && Get("insertionType").Contains("preroll", StringComparison.OrdinalIgnoreCase);

    /// <summary>Gets the announced length of the ad in milliseconds, or 0 when not given.</summary>
    public int DurationMilliseconds =>
        int.TryParse(Get("durationMilliseconds"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms) && ms > 0 ? ms : 0;

    /// <summary>Parses a metadata block as sent on the wire (zero padding allowed).</summary>
    /// <param name="block">The block's bytes.</param>
    /// <returns>The fields.</returns>
    public static IcyMetadata Parse(ReadOnlySpan<byte> block)
    {
        var end = block.IndexOf((byte)0);
        if (end >= 0)
        {
            block = block[..end];
        }

        string text;
        try
        {
            text = StrictUtf8.GetString(block);
        }
        catch (DecoderFallbackException)
        {
            // Older servers send Latin-1.
            text = Encoding.Latin1.GetString(block);
        }

        return Parse(text);
    }

    /// <summary>Parses a metadata block's text.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The fields.</returns>
    public static IcyMetadata Parse(string text)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in FieldPattern().Matches(text ?? string.Empty))
        {
            fields[match.Groups["key"].Value] = match.Groups["value"].Value;
        }

        return new IcyMetadata(fields);
    }

    /// <summary>Returns a field's value, or an empty string.</summary>
    /// <param name="key">The field name.</param>
    /// <returns>The value.</returns>
    public string Get(string key) => _fields.TryGetValue(key, out var value) ? value : string.Empty;

    // A value ends at "';" followed by the next key or the end, so a title may contain an
    // apostrophe ("Breakfast At Tiffany's").
    [GeneratedRegex(@"(?<key>[A-Za-z0-9_]+)='(?<value>.*?)';(?=[A-Za-z0-9_]+='|\s*$)", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex FieldPattern();
}
