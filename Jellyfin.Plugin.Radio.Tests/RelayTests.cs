using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.Radio.Relay;
using Jellyfin.Plugin.Radio.Stations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Radio.Tests;

/// <summary>The relay that drops the ad a stream server plays before the live stream (0.0.5).</summary>
public class RelayTests
{
    // MPEG-1 Layer III, 128 kbps, 48 kHz, stereo: 384 bytes, 24 ms per frame (Qmusic, JOE).
    private const int FrameLength = 384;
    private const byte AdFill = 0xAA;
    private const byte LiveFill = 0x55;
    private const int MetaInt = 1024;

    [Fact]
    public void ParsesAdsWizzMetadata()
    {
        var ad = IcyMetadata.Parse("StreamTitle='';StreamUrl='';adw_ad='true';durationMilliseconds='15024';adId='8907';insertionType='preroll';");
        Assert.True(ad.IsAd);
        Assert.True(ad.IsPreroll);
        Assert.Equal(15024, ad.DurationMilliseconds);
        Assert.Equal(string.Empty, ad.Title);

        var song = IcyMetadata.Parse("StreamTitle='Kingdom Of Fear - CAMERON WHITCOMB';StreamUrl='';");
        Assert.False(song.IsAd);
        Assert.Equal("Kingdom Of Fear - CAMERON WHITCOMB", song.Title);

        // The station's own ad break is not a pre-roll.
        Assert.False(IcyMetadata.Parse("StreamTitle='commercial-in';StreamUrl='';").IsPreroll);
    }

    [Fact]
    public void TitlesMayContainApostrophesAndLatin1()
    {
        Assert.Equal("Deep Blue Something - Breakfast At Tiffany's", IcyMetadata.Parse("StreamTitle='Deep Blue Something - Breakfast At Tiffany's';").Title);
        Assert.Equal("Café del Mar", IcyMetadata.Parse(Encoding.Latin1.GetBytes("StreamTitle='Café del Mar';\0\0\0")).Title);
        Assert.Equal("Café del Mar", IcyMetadata.Parse(Encoding.UTF8.GetBytes("StreamTitle='Café del Mar';\0")).Title);
    }

    [Fact]
    public void DropsThePrerollAndStartsOnALiveFrame()
    {
        var stream = Icy(
            (Frames(AdFill, 625), Meta("StreamTitle='';adw_ad='true';durationMilliseconds='15024';insertionType='preroll';")),
            (Frames(LiveFill, 100), Meta("StreamTitle='Kingdom Of Fear - CAMERON WHITCOMB';")));
        var titles = new List<string>();
        var filter = new PrerollFilter(MetaInt, 128, titles.Add);

        var output = Run(filter, stream);

        Assert.DoesNotContain(AdFill, output);
        AssertWholeLiveFrames(output);
        // The end is announced one metadata block into the live audio: at most that block and a frame are lost.
        Assert.True(output.Length >= (100 * FrameLength) - MetaInt - FrameLength, "the live audio arrives");
        Assert.Equal(1, filter.AdsDropped);
        Assert.False(filter.IsDropping);
        Assert.Equal(new[] { "Kingdom Of Fear - CAMERON WHITCOMB" }, titles);
    }

    [Fact]
    public void AStreamWithoutAnAdPassesUntouched()
    {
        var audio = Frames(LiveFill, 200);
        var stream = Icy((audio, Meta("StreamTitle='Dilemma - NELLY & KELLY ROWLAND';")));
        var filter = new PrerollFilter(MetaInt, 128);

        Assert.Equal(audio, Run(filter, stream));
        Assert.Equal(0, filter.AdsDropped);
    }

    [Fact]
    public void WithoutMetadataEverythingPasses()
    {
        var audio = Frames(AdFill, 50);
        Assert.Equal(audio, Run(new PrerollFilter(0, 128), audio));
    }

    [Fact]
    public void ChunkSizeDoesNotMatter()
    {
        var stream = Icy(
            (Frames(AdFill, 300), Meta("StreamTitle='';adw_ad='true';durationMilliseconds='7200';insertionType='preroll';")),
            (Frames(LiveFill, 80), Meta("StreamTitle='Song';")));

        var whole = Run(new PrerollFilter(MetaInt, 128), stream);
        var bytewise = Run(new PrerollFilter(MetaInt, 128), stream, chunk: 1);
        var odd = Run(new PrerollFilter(MetaInt, 128), stream, chunk: 777);

        Assert.Equal(whole, bytewise);
        Assert.Equal(whole, odd);
        Assert.DoesNotContain(AdFill, whole);
    }

    [Fact]
    public void TwoAdsInARowAreBothDropped()
    {
        var stream = Icy(
            (Frames(AdFill, 400), Meta("StreamTitle='';adw_ad='true';durationMilliseconds='9600';insertionType='preroll';")),
            (Frames(AdFill, 400), Meta("StreamTitle='';adw_ad='true';durationMilliseconds='9600';insertionType='preroll';")),
            (Frames(LiveFill, 60), Meta("StreamTitle='Song';")));
        var filter = new PrerollFilter(MetaInt, 128);

        var output = Run(filter, stream);

        Assert.DoesNotContain(AdFill, output);
        AssertWholeLiveFrames(output);
        Assert.Equal(2, filter.AdsDropped);
    }

    [Fact]
    public void AnUnmarkedEndStopsTheDropAfterTheAnnouncedLength()
    {
        // The stream never says the ad ended: after 1 s plus 3 s slack the audio comes back.
        var stream = Icy(
            (Frames(AdFill, 100), Meta("StreamTitle='';adw_ad='true';durationMilliseconds='1000';insertionType='preroll';")),
            (Frames(LiveFill, 400), Array.Empty<byte>()));
        var filter = new PrerollFilter(MetaInt, 128);

        var output = Run(filter, stream);

        Assert.False(filter.IsDropping);
        Assert.InRange(filter.DroppedBytes, 4000 * 16, (4000 * 16) + (2 * MetaInt) + FrameLength);
        Assert.True(output.Length > 200 * FrameLength);
    }

    [Fact]
    public void TheStationsOwnAdBreakIsNotDropped()
    {
        var stream = Icy(
            (Frames(LiveFill, 100), Meta("StreamTitle='commercial-in';StreamUrl='';")),
            (Frames(LiveFill, 100), Meta("StreamTitle='Ad break';StreamUrl='';")));
        var filter = new PrerollFilter(MetaInt, 128);

        Assert.Equal(200 * FrameLength, Run(filter, stream).Length);
        Assert.Equal(0, filter.AdsDropped);
    }

    [Theory]
    [InlineData(48000, 128, 2, 384)]
    [InlineData(44100, 128, 2, 417)]
    [InlineData(44100, 192, 2, 626)]
    [InlineData(48000, 256, 2, 768)]
    [InlineData(22050, 64, 1, 208)]
    public void SilentFramesAreValidFramesOfTheStationsFormat(int rate, int kbps, int channels, int length)
    {
        var frame = SilentFrame.Create(rate, kbps, channels);

        var format = Mp3FrameHeader.ParseFrame(frame.Bytes, 0, out var parsedLength);
        Assert.NotNull(format);
        Assert.Equal(rate, format!.SampleRate);
        Assert.Equal(kbps, format.BitrateKbps);
        Assert.Equal(channels, format.Channels);
        Assert.Equal(length, parsedLength);
        Assert.Equal(length, frame.Bytes.Length);
        Assert.All(frame.Bytes.Skip(4), b => Assert.Equal(0, b));

        // Back to back they read as a stream.
        var two = frame.Bytes.Concat(frame.Bytes).ToArray();
        Assert.NotNull(Mp3FrameHeader.Detect(two));
    }

    [Fact]
    public void RelayKeysArePerStationAndSurviveARestart()
    {
        var path = Path.Combine(Path.GetTempPath(), "radio-relay-" + Guid.NewGuid().ToString("N"), "relay.key");
        try
        {
            var keys = new RelayKeys(path, NullLogger<RelayKeys>.Instance);
            var qmusic = keys.KeyFor("qmusic");

            Assert.Equal(32, qmusic.Length);
            Assert.NotEqual(qmusic, keys.KeyFor("joe"));
            Assert.True(keys.IsValid("qmusic", qmusic));
            Assert.False(keys.IsValid("joe", qmusic));
            Assert.False(keys.IsValid("qmusic", null));
            Assert.False(keys.IsValid("qmusic", qmusic[..31]));
            Assert.Equal(qmusic, new RelayKeys(path, NullLogger<RelayKeys>.Instance).KeyFor("qmusic"));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [Fact]
    public void LiveFramesThatReachBackIntoTheSilenceAreMuted()
    {
        // Real streams use the bit reservoir: each frame's main data starts up to 511 bytes
        // back. After silence, those bytes are zeros, which decode as noise.
        var stream = Icy(
            (Frames(AdFill, 300, mainDataBegin: 300), Meta("StreamTitle='';adw_ad='true';durationMilliseconds='7200';insertionType='preroll';")),
            (Frames(LiveFill, 40, mainDataBegin: 300), Meta("StreamTitle='Song';")));

        var output = Run(new PrerollFilter(MetaInt, 128), stream);
        var frames = output.Chunk(FrameLength).ToList();

        // The first live frame reaches 300 bytes back with no live data before it: muted.
        Assert.All(frames[0][4..MainData], b => Assert.Equal(0, b));
        Assert.Equal(LiveFill, frames[0][MainData]);

        // Each frame carries 348 bytes of main data, so the second one is covered, and from
        // 511 bytes on (two frames) nothing is touched any more.
        Assert.Equal(300 >> 1, frames[1][4]);
        Assert.All(frames.Skip(1), f => Assert.Equal(300 >> 1, f[4]));
        Assert.DoesNotContain(AdFill, output);
    }

    [Fact]
    public void TheRelayAddressCarriesTheKeyInTheQuery()
    {
        Assert.Equal("http://127.0.0.1:8096/Radio/Stream/qmusic?k=abc", RelayAddress.Build("http://127.0.0.1:8096/", "qmusic", "abc"));
        Assert.Equal("http://127.0.0.1:8096/jellyfin/Radio/Stream/qmusic?k=abc", RelayAddress.Build("http://127.0.0.1:8096/jellyfin", "qmusic", "abc"));
    }

    [Fact]
    public void TheServerReachesItselfOnLoopbackWhenItCan()
    {
        Assert.Equal("127.0.0.1", RelayAddress.HostFor(new[] { System.Net.IPAddress.Any }));
        Assert.Equal("127.0.0.1", RelayAddress.HostFor(new[] { System.Net.IPAddress.Parse("192.168.1.10"), System.Net.IPAddress.Loopback }));
        Assert.Equal("127.0.0.1", RelayAddress.HostFor(new[] { System.Net.IPAddress.IPv6Any }));
        Assert.Equal("[::1]", RelayAddress.HostFor(new[] { System.Net.IPAddress.IPv6Loopback, System.Net.IPAddress.Parse("fd00::5") }));
        Assert.Null(RelayAddress.HostFor(new[] { System.Net.IPAddress.Parse("fe80::1%2") }));

        // Bound to one LAN address only: loopback is closed, so use that address.
        Assert.Equal("192.168.1.10", RelayAddress.HostFor(new[] { System.Net.IPAddress.Parse("fd00::5"), System.Net.IPAddress.Parse("192.168.1.10") }));
        Assert.Equal("[fd00::5]", RelayAddress.HostFor(new[] { System.Net.IPAddress.Parse("fd00::5") }));
        Assert.Null(RelayAddress.HostFor(Array.Empty<System.Net.IPAddress>()));
    }

    [Fact]
    public void AStreamThatStartsMidFrameIsCutToTheFirstWholeFrame()
    {
        // The station's first bytes are the tail of a frame; silence frames come before
        // them in the relay, so the live audio must start on a frame.
        var audio = Frames(LiveFill, 50);
        var stream = Icy((audio[100..], Meta("StreamTitle='Song';")));
        var output = Run(new PrerollFilter(MetaInt, 128), stream);

        AssertWholeLiveFrames(output);
        Assert.Equal(audio.Length - FrameLength, output.Length);
    }

    [Fact]
    public void ThePlaybackInfoFilterIsGlobalAndTheRelayIsTheOnlyAnonymousRoute()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        new PluginServiceRegistrator().RegisterServices(services, null!);
        using var provider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
        var filters = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Mvc.MvcOptions>>(provider).Value.Filters;
        Assert.Contains(filters, f => f is Microsoft.AspNetCore.Mvc.ServiceFilterAttribute s && s.ServiceType == typeof(RelayPlaybackFilter));

        // Jellyfin fetches a media source without credentials, so the relay must be
        // anonymous; the admin API must not be.
        Assert.NotNull(typeof(Api.RelayController).GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute), true).SingleOrDefault());
        Assert.Null(typeof(Api.RadioController).GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute), true).SingleOrDefault());
        Assert.All(
            typeof(Api.RadioController).GetMethods().Where(m => m.DeclaringType == typeof(Api.RadioController)),
            m => Assert.Empty(m.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute), true)));
    }

    [Fact]
    public async System.Threading.Tasks.Task TheRelayPlaysSilenceThenTheLiveStreamWithoutTheAd()
    {
        var upstream = Icy(
            (Frames(AdFill, 400), Meta("StreamTitle='';adw_ad='true';durationMilliseconds='9600';insertionType='preroll';")),
            (Frames(LiveFill, 200), Meta("StreamTitle='Kingdom Of Fear - CAMERON WHITCOMB';")));
        var nowPlaying = new NowPlayingStore();
        string? titleWhileListening = null;
        var handler = new FakeStation(upstream, atEnd: () => titleWhileListening = nowPlaying.All().SingleOrDefault()?.Title);
        var relay = new StationRelay(new FakeFactory(handler), nowPlaying, NullLogger<StationRelay>.Instance);
        using var output = new MemoryStream();

        Assert.True(await relay.RelayAsync(Qmusic(), output, playerRequest: true, System.Threading.CancellationToken.None));

        var bytes = output.ToArray();
        Assert.Equal(1, handler.Requests);
        Assert.Equal("1", handler.IcyMetaData);
        Assert.StartsWith("Jellyfin-Radio/", handler.UserAgent, StringComparison.Ordinal);
        Assert.DoesNotContain(AdFill, bytes);

        // Silent frames first (at least the 1 s head start), then live frames, all whole.
        var silent = SilentFrame.Create(48000, 128, 2).Bytes;
        var frames = bytes.Chunk(FrameLength).ToList();
        Assert.All(frames, f => Assert.Equal(FrameLength, f.Length));
        var silentCount = frames.TakeWhile(f => f.SequenceEqual(silent)).Count();
        Assert.True(silentCount >= 42, "1 s of silence is 42 frames of 24 ms");
        Assert.All(frames.Skip(silentCount), f => Assert.Equal(LiveFill, f[MainData]));
        Assert.True(frames.Count - silentCount >= 197);

        // The title was seen while the listener listened, and went when they left.
        Assert.Equal("Kingdom Of Fear - CAMERON WHITCOMB", titleWhileListening);
        Assert.Empty(nowPlaying.All());
    }

    [Fact]
    public async System.Threading.Tasks.Task AProbeNeverReachesTheStation()
    {
        // Jellyfin turns an app's HEAD into a GET here (no Range header) and drops it after
        // draining for up to 2 s; the station is only called after 2.5 s.
        var handler = new FakeStation(Frames(LiveFill, 10));
        var relay = new StationRelay(new FakeFactory(handler), new NowPlayingStore(), NullLogger<StationRelay>.Instance);
        using var output = new MemoryStream();
        using var hangUp = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(2.1));

        Assert.False(await relay.RelayAsync(Qmusic(), output, playerRequest: false, hangUp.Token));

        Assert.Equal(0, handler.Requests);
        Assert.True(output.Length > 0, "silence went out at once");
    }

    [Theory]
    [InlineData("bytes=0-", true)]
    [InlineData("bytes = 0-", true)]
    [InlineData("bytes=0-1", false)]
    [InlineData("bytes=1000-", false)]
    [InlineData(null, false)]
    public void OnlyAPlayersRangeSkipsTheProbeDelay(string? range, bool player)
    {
        Assert.Equal(player, StationRelay.IsPlayerRange(range));
    }

    [Fact]
    public async System.Threading.Tasks.Task AStationErrorIsReportedAsAFailure()
    {
        var handler = new FakeStation(Array.Empty<byte>(), status: System.Net.HttpStatusCode.ServiceUnavailable);
        var relay = new StationRelay(new FakeFactory(handler), new NowPlayingStore(), NullLogger<StationRelay>.Instance);
        using var output = new MemoryStream();

        Assert.False(await relay.RelayAsync(Qmusic(), output, playerRequest: true, System.Threading.CancellationToken.None));
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async System.Threading.Tasks.Task AStationThatStallsEndsTheRelay()
    {
        // Headers, then nothing: silence must not run forever.
        var handler = new FakeStation(Array.Empty<byte>(), stallBody: true);
        var relay = new StationRelay(new FakeFactory(handler), new NowPlayingStore(), NullLogger<StationRelay>.Instance) { StationTimeout = TimeSpan.FromSeconds(1) };
        using var output = new MemoryStream();
        var clock = System.Diagnostics.Stopwatch.StartNew();

        Assert.False(await relay.RelayAsync(Qmusic(), output, playerRequest: true, System.Threading.CancellationToken.None));
        Assert.InRange(clock.Elapsed.TotalSeconds, 0.9, 5);
    }

    [Fact]
    public async System.Threading.Tasks.Task AnAdThatNeverEndsEndsTheRelay()
    {
        // A stream that keeps announcing ads would otherwise give endless silence.
        var stream = Icy(Enumerable.Range(0, 40)
            .Select(_ => (Frames(AdFill, 40), Meta("StreamTitle='';adw_ad='true';durationMilliseconds='60000';insertionType='preroll';")))
            .ToArray());
        var handler = new FakeStation(stream, slowBody: true);
        var relay = new StationRelay(new FakeFactory(handler), new NowPlayingStore(), NullLogger<StationRelay>.Instance) { MaxSilence = TimeSpan.FromSeconds(1) };
        using var output = new MemoryStream();

        Assert.False(await relay.RelayAsync(Qmusic(), output, playerRequest: true, System.Threading.CancellationToken.None));
        Assert.DoesNotContain(AdFill, output.ToArray());
    }

    [Fact]
    public async System.Threading.Tasks.Task ASlowListenerNeverGetsAPileOfSilence()
    {
        var handler = new FakeStation(Frames(LiveFill, 10), answerAfter: TimeSpan.FromSeconds(30));
        var relay = new StationRelay(new FakeFactory(handler), new NowPlayingStore(), NullLogger<StationRelay>.Instance);
        using var slow = new SlowStream(TimeSpan.FromSeconds(1.5));
        using var hangUp = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(6));

        await relay.RelayAsync(Qmusic(), slow, playerRequest: true, hangUp.Token);

        // At most the head start plus one tick of silence per write (1.25 s = 16 KB here).
        Assert.True(slow.Writes >= 2);
        Assert.InRange(slow.LargestWrite, 1, 1.5 * 16000);
    }

    private sealed class SlowStream(TimeSpan perWrite) : Stream
    {
        public int Writes { get; private set; }

        public int LargestWrite { get; private set; }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => 0;

        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override async System.Threading.Tasks.ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, System.Threading.CancellationToken cancellationToken = default)
        {
            Writes++;
            LargestWrite = Math.Max(LargestWrite, buffer.Length);
            await System.Threading.Tasks.Task.Delay(perWrite, cancellationToken);
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async System.Threading.Tasks.Task SilenceKeepsFlowingWhileTheStationIsSlowToAnswer()
    {
        // Android Auto's player gives up after 8 s without data, so a slow station must
        // not stall the listener.
        var handler = new FakeStation(Frames(LiveFill, 10), answerAfter: TimeSpan.FromSeconds(3));
        var relay = new StationRelay(new FakeFactory(handler), new NowPlayingStore(), NullLogger<StationRelay>.Instance);
        using var output = new MemoryStream();
        using var hangUp = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(2.5));

        await relay.RelayAsync(Qmusic(), output, playerRequest: true, hangUp.Token);

        // 2.5 s plus the 1 s head start, at 24 ms per frame; allow for timer slack.
        var seconds = output.Length / (double)FrameLength * 0.024;
        Assert.InRange(seconds, 2.5, 4.0);
        Assert.Equal(1, handler.Requests);
    }

    private static Configuration.StationConfig Qmusic() => new()
    {
        Slug = "qmusic",
        Name = "Qmusic",
        StreamUrl = "https://stream.qmusic.nl/qmusic/mp3",
        BitrateKbps = 128,
        SampleRate = 48000,
        Channels = 2,
        SkipPreroll = true
    };

    private sealed class FakeStation(
        byte[] body,
        TimeSpan answerAfter = default,
        System.Net.HttpStatusCode status = System.Net.HttpStatusCode.OK,
        bool stallBody = false,
        bool slowBody = false,
        Action? atEnd = null) : System.Net.Http.HttpMessageHandler
    {
        public int Requests { get; private set; }

        public string IcyMetaData { get; private set; } = string.Empty;

        public string UserAgent { get; private set; } = string.Empty;

        protected override async System.Threading.Tasks.Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
        {
            Requests++;
            if (answerAfter > TimeSpan.Zero)
            {
                await System.Threading.Tasks.Task.Delay(answerAfter, cancellationToken);
            }

            IcyMetaData = request.Headers.TryGetValues("Icy-MetaData", out var icy) ? icy.Single() : string.Empty;
            UserAgent = string.Join(' ', request.Headers.UserAgent.Select(u => u.ToString()));
            System.Net.Http.HttpContent content = stallBody
                ? new System.Net.Http.StreamContent(new StallingStream())
                : slowBody ? new System.Net.Http.StreamContent(new TricklingStream(body))
                : atEnd is not null ? new System.Net.Http.StreamContent(new EndObservingStream(body, atEnd)) : new System.Net.Http.ByteArrayContent(body);
            var response = new System.Net.Http.HttpResponseMessage(status) { Content = content };
            response.Headers.TryAddWithoutValidation("icy-metaint", MetaInt.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return response;
        }
    }

    // A body that never sends a byte.
    private sealed class StallingStream : MemoryStream
    {
        public override async System.Threading.Tasks.ValueTask<int> ReadAsync(Memory<byte> buffer, System.Threading.CancellationToken cancellationToken = default)
        {
            await System.Threading.Tasks.Task.Delay(System.Threading.Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    // A body that reports when the relay has read all of it (the listener is still there).
    private sealed class EndObservingStream(byte[] data, Action atEnd) : MemoryStream(data)
    {
        public override async System.Threading.Tasks.ValueTask<int> ReadAsync(Memory<byte> buffer, System.Threading.CancellationToken cancellationToken = default)
        {
            var read = await base.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                atEnd();
            }

            return read;
        }
    }

    // A body that sends 4 KB every 50 ms.
    private sealed class TricklingStream(byte[] data) : MemoryStream(data)
    {
        public override async System.Threading.Tasks.ValueTask<int> ReadAsync(Memory<byte> buffer, System.Threading.CancellationToken cancellationToken = default)
        {
            await System.Threading.Tasks.Task.Delay(50, cancellationToken);
            return await base.ReadAsync(buffer[..Math.Min(buffer.Length, 4096)], cancellationToken);
        }
    }

    private sealed class FakeFactory(System.Net.Http.HttpMessageHandler handler) : System.Net.Http.IHttpClientFactory
    {
        public System.Net.Http.HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    [Fact]
    public void NowPlayingKeepsTheTimeATitleStarted()
    {
        var store = new NowPlayingStore();
        var start = new DateTime(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc);
        store.Begin("qmusic");
        store.Begin("joe");
        store.Set("qmusic", "Song A", start);
        store.Set("qmusic", "Song A", start.AddMinutes(1));
        store.Set("joe", "Song B", start.AddMinutes(2));

        var all = store.All();
        Assert.Equal(new[] { "joe", "qmusic" }, all.Select(e => e.Slug));
        Assert.Equal(start, all[1].SinceUtc);
    }

    [Fact]
    public void NowPlayingForgetsAStationWhenItsLastListenerLeaves()
    {
        var store = new NowPlayingStore();
        var now = DateTime.UtcNow;
        store.Begin("qmusic");
        store.Begin("qmusic");
        store.Set("qmusic", "Song", now);

        store.End("qmusic");
        Assert.Single(store.All());
        store.End("qmusic");
        Assert.Empty(store.All());

        // A late title from a relay that already ended is ignored.
        store.Set("qmusic", "Late", now);
        Assert.Empty(store.All());
    }

    // Side information of an MPEG-1 stereo frame: 32 bytes after the header.
    private const int MainData = 4 + 32;

    // Frames whose main data is filled with one byte value, so the output shows which audio
    // got through. mainDataBegin is how far back into earlier frames the main data starts.
    private static byte[] Frames(byte fill, int count, int mainDataBegin = 0)
    {
        var frame = new byte[FrameLength];
        Array.Fill(frame, fill, MainData, FrameLength - MainData);
        frame[0] = 0xFF;
        frame[1] = 0xFB;
        frame[2] = 0x94;
        frame[3] = 0x00;
        frame[4] = (byte)(mainDataBegin >> 1);
        frame[5] = (byte)((mainDataBegin & 1) << 7);
        return Enumerable.Repeat(frame, count).SelectMany(f => f).ToArray();
    }

    private static byte[] Meta(string text) => Encoding.UTF8.GetBytes(text);

    // Builds an ICY stream: each part's audio, with the part's metadata in the first block
    // boundary of that part (later boundaries carry no change).
    private static byte[] Icy(params (byte[] Audio, byte[] Metadata)[] parts)
    {
        var audio = new List<byte>();
        var changes = new Dictionary<int, byte[]>();
        foreach (var (partAudio, metadata) in parts)
        {
            // The announcement comes at the first boundary of the part; AdsWizz sends it
            // right after the first block of the ad (or of the live stream).
            var boundary = ((audio.Count / MetaInt) + 1) * MetaInt;
            if (metadata.Length > 0)
            {
                changes[boundary] = metadata;
            }

            audio.AddRange(partAudio);
        }

        var result = new List<byte>();
        for (var i = 0; i < audio.Count; i += MetaInt)
        {
            var block = audio.Skip(i).Take(MetaInt).ToArray();
            result.AddRange(block);
            if (block.Length < MetaInt)
            {
                break;
            }

            if (changes.TryGetValue(i + MetaInt, out var metadata))
            {
                var units = (metadata.Length + 15) / 16;
                result.Add((byte)units);
                result.AddRange(metadata);
                result.AddRange(new byte[(units * 16) - metadata.Length]);
            }
            else
            {
                result.Add(0);
            }
        }

        return result.ToArray();
    }

    private static byte[] Run(PrerollFilter filter, byte[] stream, int chunk = 4096)
    {
        var output = new ArrayBufferWriter<byte>();
        for (var i = 0; i < stream.Length; i += chunk)
        {
            filter.Feed(stream.AsSpan(i, Math.Min(chunk, stream.Length - i)), output);
        }

        return output.WrittenSpan.ToArray();
    }

    private static void AssertWholeLiveFrames(byte[] output)
    {
        Assert.True(output.Length > 0);
        Assert.Equal(0xFF, output[0]);
        Assert.Equal(0xFB, output[1]);
        Assert.Equal(LiveFill, output[MainData]);
    }
}
