using System;
using System.Buffers;
using Jellyfin.Plugin.Radio.Stations;

namespace Jellyfin.Plugin.Radio.Relay;

/// <summary>
/// Takes a station's ICY stream (MP3 audio with a metadata block every <c>icy-metaint</c>
/// bytes) and writes plain MP3 audio without the ad that the stream server plays before
/// the live stream. AdsWizz announces that ad in the metadata
/// (<c>adw_ad='true';durationMilliseconds='15024';insertionType='preroll';</c>) and the
/// first block without that announcement starts the live stream; the filter drops the
/// audio in between and picks the live stream up at the next MP3 frame. Ads the station
/// airs in its own programme are left alone. Not thread-safe: one instance per connection.
/// </summary>
/// <remarks>
/// MP3 frames borrow main data from the frames before them (the bit reservoir). The relay
/// plays silent frames before the live audio, so the first live frames would decode the
/// silent frames' zeros as audio: a burst of noise. Until enough live data has gone out,
/// a frame that reaches back further than that is muted (its side information zeroed),
/// which is what a decoder does itself when it starts cold.
/// </remarks>
public sealed class PrerollFilter
{
    // Give up looking for an MP3 frame after this much audio and pass it on as it is.
    private const int MaxResyncBytes = 64 * 1024;

    // Never drop more than the announced length plus this, in case the end is not marked.
    private static readonly TimeSpan Slack = TimeSpan.FromSeconds(3);

    // An ad without a length counts as this long; a longer announcement is capped here.
    private static readonly TimeSpan DefaultAdLength = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxAdLength = TimeSpan.FromMinutes(2);

    private readonly int _metaInt;
    private readonly int _bitrateKbps;
    private readonly Action<string>? _onTitle;
    private readonly ArrayBufferWriter<byte> _held = new();
    private readonly ArrayBufferWriter<byte> _resync = new();
    private readonly ArrayBufferWriter<byte> _priming = new();
    private readonly byte[] _metadata = new byte[255 * 16];

    private State _state;
    private int _untilMetadata;
    private int _metadataLength;
    private int _metadataFilled;
    private bool _inMetadata;
    private long _dropBudget;
    private int _reservoir;

    /// <summary>Initializes a new instance of the <see cref="PrerollFilter"/> class.</summary>
    /// <param name="metaInt">The stream's <c>icy-metaint</c>; 0 when it sends no metadata (the audio then passes untouched).</param>
    /// <param name="bitrateKbps">The station's bitrate, to turn an ad's length into bytes.</param>
    /// <param name="onTitle">Called with each new song title (never with an ad's).</param>
    public PrerollFilter(int metaInt, int bitrateKbps, Action<string>? onTitle = null)
    {
        _metaInt = Math.Max(0, metaInt);
        _bitrateKbps = bitrateKbps > 0 ? bitrateKbps : 128;
        _onTitle = onTitle;
        _untilMetadata = _metaInt;

        // The first block of metadata follows the first metaInt bytes of audio and says
        // whether they are an ad, so that audio waits for it. Without metadata the audio
        // still starts on a whole frame, because the relay plays silent frames before it.
        _state = _metaInt > 0 ? State.Holding : State.Resyncing;
    }

    private enum State
    {
        Holding,
        Passing,
        Dropping,
        Resyncing,
        Priming
    }

    /// <summary>
    /// Gets a value indicating whether an ad is being dropped, or the next whole frame is
    /// still being looked for (the relay plays silence meanwhile).
    /// </summary>
    public bool IsDropping => _state is State.Dropping or State.Resyncing;

    /// <summary>Gets how many ads were dropped.</summary>
    public int AdsDropped { get; private set; }

    /// <summary>Gets how many bytes of ad audio were dropped.</summary>
    public long DroppedBytes { get; private set; }

    /// <summary>Gets the last song title seen, or an empty string.</summary>
    public string LastTitle { get; private set; } = string.Empty;

    /// <summary>Processes the next bytes from the station and writes the audio to keep.</summary>
    /// <param name="input">Bytes as received; any chunk size.</param>
    /// <param name="output">Receives plain MP3 audio.</param>
    public void Feed(ReadOnlySpan<byte> input, IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (_metaInt == 0)
        {
            Audio(input, output);
            return;
        }

        while (!input.IsEmpty)
        {
            if (_inMetadata)
            {
                var take = Math.Min(_metadataLength - _metadataFilled, input.Length);
                input[..take].CopyTo(_metadata.AsSpan(_metadataFilled));
                _metadataFilled += take;
                input = input[take..];
                if (_metadataFilled == _metadataLength)
                {
                    _inMetadata = false;
                    _untilMetadata = _metaInt;
                    Metadata(IcyMetadata.Parse(_metadata.AsSpan(0, _metadataLength)), output);
                }

                continue;
            }

            if (_untilMetadata == 0)
            {
                // The length byte: the block is that many 16-byte units; 0 means no change.
                _metadataLength = input[0] * 16;
                input = input[1..];
                if (_metadataLength == 0)
                {
                    _untilMetadata = _metaInt;
                    if (_state == State.Holding)
                    {
                        Release(output);
                    }
                }
                else
                {
                    _inMetadata = true;
                    _metadataFilled = 0;
                }

                continue;
            }

            var audio = Math.Min(_untilMetadata, input.Length);
            Audio(input[..audio], output);
            _untilMetadata -= audio;
            input = input[audio..];
        }
    }

    private void Audio(ReadOnlySpan<byte> audio, IBufferWriter<byte> output)
    {
        switch (_state)
        {
            case State.Holding:
                _held.Write(audio);
                break;
            case State.Passing:
                output.Write(audio);
                break;
            case State.Dropping:
                DroppedBytes += audio.Length;
                _dropBudget -= audio.Length;
                if (_dropBudget <= 0)
                {
                    // The end of the ad was not announced in time: carry on with whatever comes.
                    _state = State.Resyncing;
                }

                break;
            case State.Resyncing:
                _resync.Write(audio);
                TryResync(output);
                break;
            case State.Priming:
                _priming.Write(audio);
                Prime(output);
                break;
        }
    }

    private void Metadata(IcyMetadata metadata, IBufferWriter<byte> output)
    {
        if (metadata.IsPreroll)
        {
            var length = metadata.DurationMilliseconds > 0 ? TimeSpan.FromMilliseconds(metadata.DurationMilliseconds) : DefaultAdLength;
            if (length > MaxAdLength)
            {
                length = MaxAdLength;
            }

            var budget = BytesFor(length + Slack);
            if (_state == State.Holding)
            {
                DroppedBytes += _held.WrittenCount;
                _held.Clear();
            }
            else if (_state == State.Resyncing)
            {
                DroppedBytes += _resync.WrittenCount;
                _resync.Clear();
            }
            else if (_state == State.Priming)
            {
                DroppedBytes += _priming.WrittenCount;
                _priming.Clear();
            }

            // A second ad right after the first extends the drop.
            _dropBudget = _state == State.Dropping ? _dropBudget + budget : budget;
            _state = State.Dropping;
            AdsDropped++;
            return;
        }

        if (!metadata.IsAd && metadata.Title.Length > 0 && !string.Equals(metadata.Title, LastTitle, StringComparison.Ordinal))
        {
            LastTitle = metadata.Title;
            _onTitle?.Invoke(metadata.Title);
        }

        if (_state == State.Holding)
        {
            Release(output);
        }
        else if (_state == State.Dropping)
        {
            // The live stream starts somewhere in the next bytes.
            _state = State.Resyncing;
        }
    }

    // No ad: the held audio goes out, from its first whole frame on.
    private void Release(IBufferWriter<byte> output)
    {
        _resync.Write(_held.WrittenSpan);
        _held.Clear();
        _state = State.Resyncing;
        TryResync(output);
    }

    // Finds the first MP3 frame that is directly followed by another one, so the app's
    // decoder starts on a whole frame of the live stream.
    private void TryResync(IBufferWriter<byte> output)
    {
        var data = _resync.WrittenSpan;
        for (var i = 0; i + 4 <= data.Length; i++)
        {
            var first = Mp3FrameHeader.ParseFrame(data, i, out var length);
            if (first is null || length <= 4)
            {
                continue;
            }

            if (i + length + 4 > data.Length)
            {
                // Wait for the next header to arrive.
                break;
            }

            var second = Mp3FrameHeader.ParseFrame(data, i + length, out _);
            if (second is not null && second.SampleRate == first.SampleRate)
            {
                DroppedBytes += i;
                _priming.Write(data[i..]);
                _resync.Clear();
                _reservoir = 0;
                _state = State.Priming;
                Prime(output);
                return;
            }
        }

        if (_resync.WrittenCount > MaxResyncBytes)
        {
            output.Write(_resync.WrittenSpan);
            _resync.Clear();
            _state = State.Passing;
        }
    }

    // Writes whole frames, muting those whose main data starts before the live audio did,
    // until the reservoir holds as much live data as a frame can reach back.
    private void Prime(IBufferWriter<byte> output)
    {
        while (true)
        {
            var data = _priming.WrittenSpan;
            if (data.Length < 6)
            {
                return;
            }

            if (Mp3FrameHeader.ParseFrame(data, 0, out var length) is null || length <= 6)
            {
                // Lost the frame grid: pass the audio on as it is.
                output.Write(data);
                _priming.Clear();
                _state = State.Passing;
                return;
            }

            if (data.Length < length)
            {
                return;
            }

            var layout = FrameLayout.Of(data);
            var frame = output.GetSpan(length)[..length];
            data[..length].CopyTo(frame);
            if (layout.MainDataBegin(data) > _reservoir)
            {
                frame.Slice(layout.SideInfoOffset, layout.SideInfoLength).Clear();
            }

            output.Advance(length);
            _reservoir += length - layout.SideInfoOffset - layout.SideInfoLength;
            var rest = data[length..].ToArray();
            _priming.Clear();
            if (_reservoir >= layout.MaxMainDataBegin)
            {
                output.Write(rest);
                _state = State.Passing;
                return;
            }

            _priming.Write(rest);
        }
    }

    private long BytesFor(TimeSpan length) => (long)(length.TotalMilliseconds * _bitrateKbps / 8);

    // Where a Layer III frame keeps its side information, and how far back its main data
    // may start (ISO 11172-3 and 13818-3).
    private readonly record struct FrameLayout(bool Mpeg1, int SideInfoOffset, int SideInfoLength)
    {
        public int MaxMainDataBegin => Mpeg1 ? 511 : 255;

        public static FrameLayout Of(ReadOnlySpan<byte> header)
        {
            var mpeg1 = ((header[1] >> 3) & 3) == 3;
            var crc = (header[1] & 1) == 0;
            var mono = ((header[3] >> 6) & 3) == 3;
            var sideInfo = mpeg1 ? (mono ? 17 : 32) : (mono ? 9 : 17);
            return new FrameLayout(mpeg1, crc ? 6 : 4, sideInfo);
        }

        public int MainDataBegin(ReadOnlySpan<byte> frame) => Mpeg1
            ? (frame[SideInfoOffset] << 1) | (frame[SideInfoOffset + 1] >> 7)
            : frame[SideInfoOffset];
    }
}
