using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Radio.Stations;
using Xunit;

namespace Jellyfin.Plugin.Radio.Tests;

/// <summary>Reading the stream format from synthetic MP3 and ADTS frames.</summary>
public class Mp3FrameHeaderTests
{
    [Theory]
    [InlineData(0xFB, 0xB4, 0x00, 192, 48000, 2)] // MPEG-1 L3, 192 kbps, 48 kHz, stereo (NPO)
    [InlineData(0xFB, 0x90, 0x00, 128, 44100, 2)] // MPEG-1 L3, 128 kbps, 44.1 kHz, stereo (StreamTheWorld)
    [InlineData(0xFB, 0x94, 0xC0, 128, 48000, 1)] // MPEG-1 L3, 128 kbps, 48 kHz, mono
    [InlineData(0xF3, 0x84, 0x00, 64, 24000, 2)]  // MPEG-2 L3, 64 kbps, 24 kHz
    public void ReadsMp3Frames(byte b1, byte b2, byte b3, int kbps, int rate, int channels)
    {
        var data = Frames(b1, b2, b3, count: 3, junkBefore: 37);
        var format = Mp3FrameHeader.Detect(data);

        Assert.NotNull(format);
        Assert.Equal("mp3", format!.Codec);
        Assert.Equal(kbps, format.BitrateKbps);
        Assert.Equal(rate, format.SampleRate);
        Assert.Equal(channels, format.Channels);
    }

    [Fact]
    public void SkipsAnId3Tag()
    {
        // The tag body contains a fake sync word that must not be taken for a frame.
        var tag = new List<byte> { (byte)'I', (byte)'D', (byte)'3', 4, 0, 0, 0, 0, 0, 20 };
        tag.AddRange(new byte[] { 0xFF, 0xFB, 0x10, 0x00 });
        tag.AddRange(new byte[16]);
        var data = tag.Concat(Frames(0xFB, 0xB4, 0x00, count: 2, junkBefore: 0)).ToArray();

        var format = Mp3FrameHeader.Detect(data);
        Assert.Equal(192, format!.BitrateKbps);
    }

    [Fact]
    public void ALoneSyncWordIsNotAFrame()
    {
        var data = new byte[4000];
        data[100] = 0xFF;
        data[101] = 0xFB;
        data[102] = 0xB4;
        Assert.Null(Mp3FrameHeader.Detect(data));
    }

    [Fact]
    public void RecognisesAdtsAac()
    {
        // ADTS, AAC LC, 48 kHz (index 3), 2 channels, frame length 200.
        var frame = new byte[200];
        frame[0] = 0xFF;
        frame[1] = 0xF1;
        frame[2] = (byte)((1 << 6) | (3 << 2));
        frame[3] = (byte)((2 << 6) | (200 >> 11));
        frame[4] = (byte)((200 >> 3) & 0xFF);
        frame[5] = (byte)(((200 & 7) << 5) | 0x1F);
        frame[6] = 0xFC;
        var data = frame.Concat(frame).ToArray();

        var format = Mp3FrameHeader.Detect(data);
        Assert.Equal("aac", format!.Codec);
        Assert.Equal(48000, format.SampleRate);
        Assert.Equal(2, format.Channels);
    }

    [Fact]
    public void FrameLengthMatchesTheStandard()
    {
        var header = new byte[] { 0xFF, 0xFB, 0x90, 0x00 };
        Assert.NotNull(Mp3FrameHeader.ParseFrame(header, 0, out var length));
        Assert.Equal(417, length); // 144 * 128000 / 44100
    }

    private static byte[] Frames(byte b1, byte b2, byte b3, int count, int junkBefore)
    {
        var header = new byte[] { 0xFF, b1, b2, b3 };
        Assert.NotNull(Mp3FrameHeader.ParseFrame(header, 0, out var length));
        var data = new List<byte>(new byte[junkBefore]);
        for (var i = 0; i < count; i++)
        {
            var frame = new byte[length];
            header.CopyTo(frame, 0);
            data.AddRange(frame);
        }

        return data.ToArray();
    }
}
