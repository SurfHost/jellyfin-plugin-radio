using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Radio.Stations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Radio.Tests;

/// <summary>The stream probe against canned HTTP answers (no network).</summary>
public class StreamProbeTests
{
    [Fact]
    public async Task MeasuresAnMp3Stream()
    {
        var probe = Probe(new Dictionary<string, Func<HttpResponseMessage>>
        {
            ["https://radio.example/live.mp3"] = () => Audio("audio/mpeg", Mp3Frames())
        });

        var result = await probe.ProbeAsync("https://radio.example/live.mp3", CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.Equal("mp3", result.Codec);
        Assert.Equal(192, result.BitrateKbps);
        Assert.Equal(48000, result.SampleRate);
        Assert.Equal(2, result.Channels);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task FollowsRedirectsAndWarnsAboutADowngrade()
    {
        var probe = Probe(new Dictionary<string, Func<HttpResponseMessage>>
        {
            ["https://redirect.example/station.mp3"] = () => Redirect("http://node.example/station.mp3"),
            ["http://node.example/station.mp3"] = () => Audio("audio/mpeg", Mp3Frames())
        });

        var result = await probe.ProbeAsync("https://redirect.example/station.mp3", CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.Equal("http://node.example/station.mp3", result.FinalUrl);
        Assert.Single(result.Redirects);
        Assert.Contains(result.Warnings, w => w.Contains("https to http", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARedirectToAnotherSchemeIsAnErrorNotACrash()
    {
        var probe = Probe(new Dictionary<string, Func<HttpResponseMessage>>
        {
            ["https://radio.example/live"] = () => Redirect("rtsp://radio.example/live")
        });

        var result = await probe.ProbeAsync("https://radio.example/live", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("cannot play", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefusesAPlaylist()
    {
        var probe = Probe(new Dictionary<string, Func<HttpResponseMessage>>
        {
            ["https://radio.example/listen"] = () => Audio("audio/x-mpegurl", System.Text.Encoding.UTF8.GetBytes("#EXTM3U\nhttps://x/y.mp3\n"))
        });

        var result = await probe.ProbeAsync("https://radio.example/listen", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("playlist", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefusesAac()
    {
        var probe = Probe(new Dictionary<string, Func<HttpResponseMessage>>
        {
            ["https://radio.example/aac"] = () => Audio("audio/aacp", AdtsFrames())
        });

        var result = await probe.ProbeAsync("https://radio.example/aac", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("aac", result.Codec);
        Assert.Contains("AAC", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportsAnHttpError()
    {
        var probe = Probe(new Dictionary<string, Func<HttpResponseMessage>>
        {
            ["https://radio.example/gone"] = () => new HttpResponseMessage(HttpStatusCode.NotFound)
        });

        var result = await probe.ProbeAsync("https://radio.example/gone", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(404, result.HttpStatus);
    }

    [Fact]
    public async Task RewritesAStreamTheWorldNodeBeforeFetching()
    {
        var probe = Probe(new Dictionary<string, Func<HttpResponseMessage>>
        {
            ["https://playerservices.streamtheworld.com/api/livestream-redirect/RADIO538.mp3"] = () => Audio("audio/mpeg", Mp3Frames())
        });

        var result = await probe.ProbeAsync("https://22713.live.streamtheworld.com:443/RADIO538.mp3", CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.Equal("https://playerservices.streamtheworld.com/api/livestream-redirect/RADIO538.mp3", result.StreamUrl);
        Assert.Contains(result.Warnings, w => w.Contains("StreamTheWorld", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NeverAsksForIcyMetadata()
    {
        var handler = new CannedHandler(new Dictionary<string, Func<HttpResponseMessage>>
        {
            ["https://radio.example/live.mp3"] = () => Audio("audio/mpeg", Mp3Frames())
        });
        await new StreamProbe(new Factory(handler), NullLogger<StreamProbe>.Instance).ProbeAsync("https://radio.example/live.mp3", CancellationToken.None);

        Assert.False(handler.Requests.Single().Headers.Contains("Icy-MetaData"));
    }

    private static StreamProbe Probe(Dictionary<string, Func<HttpResponseMessage>> answers) =>
        new(new Factory(new CannedHandler(answers)), NullLogger<StreamProbe>.Instance);

    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location);
        return response;
    }

    private static HttpResponseMessage Audio(string contentType, byte[] body)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static byte[] Mp3Frames()
    {
        // MPEG-1 Layer III, 192 kbps, 48 kHz, stereo: 576-byte frames.
        var frame = new byte[576];
        frame[0] = 0xFF;
        frame[1] = 0xFB;
        frame[2] = 0xB4;
        return frame.Concat(frame).Concat(frame).ToArray();
    }

    private static byte[] AdtsFrames()
    {
        var frame = new byte[200];
        frame[0] = 0xFF;
        frame[1] = 0xF1;
        frame[2] = (1 << 6) | (3 << 2);
        frame[3] = 2 << 6;
        frame[4] = 200 >> 3;
        frame[5] = 0x1F;
        frame[6] = 0xFC;
        return frame.Concat(frame).ToArray();
    }

    private sealed class Factory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public Factory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private sealed class CannedHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpResponseMessage>> _answers;

        public CannedHandler(Dictionary<string, Func<HttpResponseMessage>> answers) => _answers = answers;

        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (request.RequestUri!.Scheme is not ("http" or "https"))
            {
                throw new NotSupportedException("The '" + request.RequestUri.Scheme + "' scheme is not supported.");
            }

            return Task.FromResult(_answers.TryGetValue(request.RequestUri.ToString(), out var answer)
                ? answer()
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
