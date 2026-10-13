using System;

namespace Jellyfin.Plugin.Radio.Stations;

/// <summary>The format of an audio stream as read from its first frames.</summary>
/// <param name="Codec"><c>mp3</c> or <c>aac</c>.</param>
/// <param name="BitrateKbps">Bitrate in kbit/s; 0 when the header does not carry it (AAC).</param>
/// <param name="SampleRate">Sample rate in Hz.</param>
/// <param name="Channels">Channel count.</param>
public sealed record AudioFormat(string Codec, int BitrateKbps, int SampleRate, int Channels);

/// <summary>
/// Reads the stream format from the first bytes of an MP3 (MPEG audio Layer III) or ADTS
/// AAC stream. Jellyfin never probes these live streams (that would hang on an endless
/// stream), so the plugin measures the format once and stores it with the station.
/// </summary>
public static class Mp3FrameHeader
{
    private static readonly int[] BitratesV1 = { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0 };
    private static readonly int[] BitratesV2 = { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0 };
    private static readonly int[] RatesV1 = { 44100, 48000, 32000 };
    private static readonly int[] RatesV2 = { 22050, 24000, 16000 };
    private static readonly int[] RatesV25 = { 11025, 12000, 8000 };
    private static readonly int[] AdtsRates = { 96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350 };

    /// <summary>
    /// Finds the first MP3 frame that is followed directly by another frame with the same
    /// sample rate (a lone 0xFFF sync pattern is common in random data), skipping an ID3v2
    /// tag. Falls back to an ADTS AAC header so the caller can say the stream is AAC.
    /// </summary>
    /// <param name="data">The first bytes of the stream.</param>
    /// <returns>The format, or null when no frame was found.</returns>
    public static AudioFormat? Detect(ReadOnlySpan<byte> data)
    {
        return FindMp3(data) ?? FindAdts(data);
    }

    /// <summary>Parses one MPEG audio Layer III frame header.</summary>
    /// <param name="data">The bytes.</param>
    /// <param name="offset">Where the header starts.</param>
    /// <param name="frameLength">The frame length in bytes, including the header.</param>
    /// <returns>The format, or null when this is not a valid Layer III header.</returns>
    public static AudioFormat? ParseFrame(ReadOnlySpan<byte> data, int offset, out int frameLength)
    {
        frameLength = 0;
        if (offset < 0 || offset + 4 > data.Length || data[offset] != 0xFF || (data[offset + 1] & 0xE0) != 0xE0)
        {
            return null;
        }

        var version = (data[offset + 1] >> 3) & 3; // 3 = MPEG-1, 2 = MPEG-2, 0 = MPEG-2.5, 1 = reserved
        var layer = (data[offset + 1] >> 1) & 3;   // 1 = Layer III
        var bitrateIndex = (data[offset + 2] >> 4) & 0xF;
        var rateIndex = (data[offset + 2] >> 2) & 3;
        var padding = (data[offset + 2] >> 1) & 1;
        var mode = (data[offset + 3] >> 6) & 3;     // 3 = mono
        if (version == 1 || layer != 1 || bitrateIndex is 0 or 15 || rateIndex == 3)
        {
            return null;
        }

        var bitrate = (version == 3 ? BitratesV1 : BitratesV2)[bitrateIndex];
        var sampleRate = (version switch { 3 => RatesV1, 2 => RatesV2, _ => RatesV25 })[rateIndex];
        frameLength = ((version == 3 ? 144 : 72) * bitrate * 1000 / sampleRate) + padding;
        return new AudioFormat("mp3", bitrate, sampleRate, mode == 3 ? 1 : 2);
    }

    private static AudioFormat? FindMp3(ReadOnlySpan<byte> data)
    {
        var start = 0;
        if (data.Length > 10 && data[0] == (byte)'I' && data[1] == (byte)'D' && data[2] == (byte)'3')
        {
            // ID3v2 size: four 7-bit bytes, plus the 10-byte header.
            start = 10 + ((data[6] & 0x7F) << 21 | (data[7] & 0x7F) << 14 | (data[8] & 0x7F) << 7 | (data[9] & 0x7F));
        }

        for (var i = start; i + 4 <= data.Length; i++)
        {
            var first = ParseFrame(data, i, out var length);
            if (first is null || length <= 4)
            {
                continue;
            }

            var second = ParseFrame(data, i + length, out _);
            if (second is not null && second.SampleRate == first.SampleRate)
            {
                return first;
            }
        }

        return null;
    }

    private static AudioFormat? FindAdts(ReadOnlySpan<byte> data)
    {
        for (var i = 0; i + 7 <= data.Length; i++)
        {
            if (data[i] != 0xFF || (data[i + 1] & 0xF6) != 0xF0)
            {
                continue;
            }

            var rateIndex = (data[i + 2] >> 2) & 0xF;
            var channels = ((data[i + 2] & 1) << 2) | ((data[i + 3] >> 6) & 3);
            var length = ((data[i + 3] & 3) << 11) | (data[i + 4] << 3) | ((data[i + 5] >> 5) & 7);
            if (rateIndex >= AdtsRates.Length || channels is < 1 or > 2 || length < 7)
            {
                continue;
            }

            // Require the next frame's sync too, as for MP3.
            var next = i + length;
            if (next + 2 <= data.Length && data[next] == 0xFF && (data[next + 1] & 0xF6) == 0xF0)
            {
                return new AudioFormat("aac", 0, AdtsRates[rateIndex], channels);
            }
        }

        return null;
    }
}
