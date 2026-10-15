using System;
using System.Linq;

namespace Jellyfin.Plugin.Radio.Relay;

/// <summary>
/// A digitally silent MPEG audio Layer III frame in a station's own format. The relay plays
/// these while it drops an ad, so the app starts at once and keeps playing instead of
/// waiting for data. A frame whose side information is all zero carries no coded values,
/// so every decoder outputs silence for it.
/// </summary>
public sealed class SilentFrame
{
    private static readonly int[] BitratesV1 = { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320 };
    private static readonly int[] BitratesV2 = { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160 };
    private static readonly int[] RatesV1 = { 44100, 48000, 32000 };
    private static readonly int[] RatesV2 = { 22050, 24000, 16000 };
    private static readonly int[] RatesV25 = { 11025, 12000, 8000 };

    private SilentFrame(byte[] bytes, TimeSpan duration)
    {
        Bytes = bytes;
        Duration = duration;
    }

    /// <summary>Gets the frame, header included.</summary>
    public byte[] Bytes { get; }

    /// <summary>Gets how long the frame plays.</summary>
    public TimeSpan Duration { get; }

    /// <summary>Builds a silent frame. An unsupported bitrate becomes the nearest one the format allows.</summary>
    /// <param name="sampleRate">The sample rate in Hz (8000 to 48000).</param>
    /// <param name="bitrateKbps">The bitrate in kbit/s.</param>
    /// <param name="channels">1 or 2.</param>
    /// <returns>The frame.</returns>
    public static SilentFrame Create(int sampleRate, int bitrateKbps, int channels)
    {
        int version; // 3 = MPEG-1, 2 = MPEG-2, 0 = MPEG-2.5 (header bits)
        int rateIndex;
        if ((rateIndex = Array.IndexOf(RatesV1, sampleRate)) >= 0)
        {
            version = 3;
        }
        else if ((rateIndex = Array.IndexOf(RatesV2, sampleRate)) >= 0)
        {
            version = 2;
        }
        else if ((rateIndex = Array.IndexOf(RatesV25, sampleRate)) >= 0)
        {
            version = 0;
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "Not an MPEG audio sample rate.");
        }

        var table = version == 3 ? BitratesV1 : BitratesV2;
        var bitrateIndex = Enumerable.Range(1, table.Length - 1).MinBy(i => Math.Abs(table[i] - bitrateKbps));
        var bitrate = table[bitrateIndex];
        var mono = channels == 1;

        var length = (version == 3 ? 144 : 72) * bitrate * 1000 / sampleRate;
        var bytes = new byte[length];
        bytes[0] = 0xFF;
        bytes[1] = (byte)(0xE0 | (version << 3) | (1 << 1) | 1); // Layer III, no CRC
        bytes[2] = (byte)((bitrateIndex << 4) | (rateIndex << 2)); // no padding
        bytes[3] = (byte)(mono ? 0xC0 : 0x00);                     // mono or stereo

        // Everything after the header (side information and main data) stays zero.
        var samples = version == 3 ? 1152 : 576;
        return new SilentFrame(bytes, TimeSpan.FromSeconds((double)samples / sampleRate));
    }
}
