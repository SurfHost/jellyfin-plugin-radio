using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Radio.Stations;

/// <summary>What a test fetch of a stream URL found.</summary>
public sealed class ProbeResult
{
    /// <summary>Gets or sets a value indicating whether the URL is a playable MP3 stream.</summary>
    public bool Ok { get; set; }

    /// <summary>Gets or sets the URL to store (normalized).</summary>
    public string StreamUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets where the redirects ended.</summary>
    public string FinalUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the redirect chain, first hop first.</summary>
    public List<string> Redirects { get; set; } = new();

    /// <summary>Gets or sets the final HTTP status.</summary>
    public int HttpStatus { get; set; }

    /// <summary>Gets or sets the response content type.</summary>
    public string ContentType { get; set; } = string.Empty;

    /// <summary>Gets or sets the station name the server announces (icy-name), if any.</summary>
    public string IcyName { get; set; } = string.Empty;

    /// <summary>Gets or sets the detected codec: mp3, aac or unknown.</summary>
    public string Codec { get; set; } = "unknown";

    /// <summary>Gets or sets the bitrate in kbit/s.</summary>
    public int BitrateKbps { get; set; }

    /// <summary>Gets or sets the sample rate in Hz.</summary>
    public int SampleRate { get; set; }

    /// <summary>Gets or sets the channel count.</summary>
    public int Channels { get; set; }

    /// <summary>Gets or sets problems that do not stop playback everywhere.</summary>
    public List<string> Warnings { get; set; } = new();

    /// <summary>Gets or sets the reason the URL is not usable, when <see cref="Ok"/> is false.</summary>
    public string Error { get; set; } = string.Empty;
}

/// <summary>
/// Fetches the first bytes of a stream to check it and measure its format. Follows
/// redirects itself to report the chain (StreamTheWorld and DPG answer with a redirect
/// on every request) and to notice an https-to-http downgrade, which some apps refuse.
/// Never asks for ICY metadata, so the bytes read are pure audio.
/// </summary>
public sealed class StreamProbe
{
    /// <summary>The named HttpClient (prefixed: named clients are shared server-wide).</summary>
    public const string HttpClientName = "Radio.Probe";

    /// <summary>A browser-like User-Agent; some stream hosts refuse unknown agents.</summary>
    public const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0 Safari/537.36";

    private const int MaxRedirects = 5;
    private const int MaxBytes = 64 * 1024;
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<StreamProbe> _logger;

    /// <summary>Initializes a new instance of the <see cref="StreamProbe"/> class.</summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public StreamProbe(IHttpClientFactory httpClientFactory, ILogger<StreamProbe> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>Checks a stream URL and measures its format.</summary>
    /// <param name="url">The URL as entered.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The result; never throws for network problems.</returns>
    public async Task<ProbeResult> ProbeAsync(string url, CancellationToken cancellationToken)
    {
        var result = new ProbeResult { StreamUrl = StreamUrlNormalizer.Normalize(url) };
        if (!Uri.TryCreate(result.StreamUrl, UriKind.Absolute, out var current)
            || (current.Scheme != Uri.UriSchemeHttp && current.Scheme != Uri.UriSchemeHttps))
        {
            result.Error = "Not an http or https URL.";
            return result;
        }

        if (!string.Equals(result.StreamUrl, (url ?? string.Empty).Trim(), StringComparison.Ordinal))
        {
            result.Warnings.Add("This was a StreamTheWorld node URL, which changes over time. The stable redirect URL is used instead.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Budget);
        var client = _httpClientFactory.CreateClient(HttpClientName);

        try
        {
            for (var hop = 0; ; hop++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
                request.Headers.TryAddWithoutValidation("Accept", "*/*");
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                result.HttpStatus = (int)response.StatusCode;

                if (IsRedirect(response.StatusCode) && response.Headers.Location is not null)
                {
                    if (hop >= MaxRedirects)
                    {
                        result.Error = "Too many redirects.";
                        return result;
                    }

                    var location = response.Headers.Location;
                    if (!Uri.TryCreate(current, location, out var next)
                        || (next.Scheme != Uri.UriSchemeHttp && next.Scheme != Uri.UriSchemeHttps))
                    {
                        result.Error = "The stream redirects to an address Jellyfin cannot play: " + location + ".";
                        return result;
                    }

                    if (current.Scheme == Uri.UriSchemeHttps && next.Scheme == Uri.UriSchemeHttp)
                    {
                        result.Warnings.Add("The stream redirects from https to http. Android Auto and some players refuse that; turn on 'Relay through server' if they do.");
                    }

                    result.Redirects.Add(next.ToString());
                    current = next;
                    continue;
                }

                result.FinalUrl = current.ToString();
                result.ContentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
                if (response.Headers.TryGetValues("icy-name", out var names))
                {
                    result.IcyName = string.Join(' ', names).Trim();
                }

                if (!response.IsSuccessStatusCode)
                {
                    result.Error = "The server answered HTTP " + result.HttpStatus + ".";
                    return result;
                }

                if (IsPlaylistOrPage(result.ContentType))
                {
                    result.Error = "This URL returns a playlist or web page (" + result.ContentType + "), not an audio stream. Open it and use the stream URL inside.";
                    return result;
                }

                var data = await ReadHeadAsync(response, timeout.Token).ConfigureAwait(false);
                var format = Mp3FrameHeader.Detect(data);
                if (format is null)
                {
                    result.Error = "No MP3 or AAC audio found in the first " + data.Length + " bytes.";
                    return result;
                }

                result.Codec = format.Codec;
                result.BitrateKbps = format.BitrateKbps;
                result.SampleRate = format.SampleRate;
                result.Channels = format.Channels;
                if (format.Codec != "mp3")
                {
                    result.Error = "This is an AAC stream. Only MP3 streams are supported (live AAC cannot be cast or converted by Jellyfin); look for the station's MP3 stream.";
                    return result;
                }

                if (!string.Equals(result.ContentType, "audio/mpeg", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(result.ContentType, "audio/mp3", StringComparison.OrdinalIgnoreCase))
                {
                    result.Warnings.Add("The server labels the stream '" + result.ContentType + "' instead of audio/mpeg; some players may refuse it.");
                }

                result.Ok = true;
                return result;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            result.Error = "No answer within " + (int)Budget.TotalSeconds + " seconds.";
            return result;
        }
        catch (HttpRequestException ex) when (ex.HttpRequestError == HttpRequestError.InvalidResponse)
        {
            // "ICY 200 OK" instead of "HTTP/1.x 200 OK": an old SHOUTcast v1 server.
            result.Error = "The server speaks the old SHOUTcast (ICY) protocol, which browsers and Jellyfin cannot play. Look for another stream URL.";
            return result;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or NotSupportedException or UriFormatException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "Radio: probing {Url} failed.", current);
            result.Error = ex.Message;
            return result;
        }
    }

    private static bool IsRedirect(HttpStatusCode code) => (int)code is 301 or 302 or 303 or 307 or 308;

    private static bool IsPlaylistOrPage(string contentType) =>
        contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
        || contentType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase)
        || contentType.Contains("scpls", StringComparison.OrdinalIgnoreCase)
        || contentType.Contains("dash+xml", StringComparison.OrdinalIgnoreCase)
        || contentType.Contains("xspf", StringComparison.OrdinalIgnoreCase);

    private static async Task<byte[]> ReadHeadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxBytes];
        var total = 0;
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            try
            {
                while (total < buffer.Length)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    total += read;
                }
            }
            catch (OperationCanceledException) when (total > 0)
            {
                // A slow stream: use what arrived.
            }
        }

        return buffer.AsSpan(0, total).ToArray();
    }
}
