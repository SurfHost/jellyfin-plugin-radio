using System;
using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Radio.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Radio.Relay;

/// <summary>
/// Streams a station to one listener through the server, without the ad its stream server
/// plays before the live stream (<see cref="PrerollFilter"/>). Until the live stream
/// arrives the listener gets silence, paced to the clock, so playback starts at once and
/// no app times out (Android Auto gives up after 8 seconds without data).
/// </summary>
public sealed class StationRelay
{
    /// <summary>The named HttpClient for station streams. Named clients are shared by every plugin, hence the prefix.</summary>
    public const string HttpClientName = "Radio.Relay";

    // The same identity for every listener: the stream server plays its ad once per
    // listener (address plus user agent) in a while, so after one dropped ad the next
    // listeners usually get the live stream straight away.
    private static readonly string UserAgent = "Jellyfin-Radio/" + (typeof(StationRelay).Assembly.GetName().Version?.ToString(3) ?? "0") + " (+https://github.com/SurfHost/jellyfin-plugin-radio)";

    // Silence sent ahead of the clock, so the app has something to start on.
    private static readonly TimeSpan HeadStart = TimeSpan.FromSeconds(1);

    // How often silence is topped up while the station is slow or an ad is being dropped.
    private static readonly TimeSpan SilenceTick = TimeSpan.FromMilliseconds(250);

    // Jellyfin answers an app's HEAD request (the web player sends one before every play)
    // by opening a full GET here, without a Range header, and dropping it after the headers.
    // Its HttpClient first drains the response for up to 2 s, so a GET without an open-ended
    // Range waits this long before the station is called: a probe then never reaches the
    // station and never starts an ad nobody hears. Players send "Range: bytes=0-" and start
    // at once; the others hear a little more silence.
    internal static readonly TimeSpan DefaultProbeDelay = TimeSpan.FromSeconds(2.5);

    // The station must answer within this time, and never fall silent for longer.
    internal static readonly TimeSpan DefaultStationTimeout = TimeSpan.FromSeconds(15);

    // Give up when no live audio has come this long after the start (or after an ad began).
    internal static readonly TimeSpan DefaultMaxSilence = TimeSpan.FromSeconds(75);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly NowPlayingStore _nowPlaying;
    private readonly ILogger<StationRelay> _logger;

    /// <summary>Initializes a new instance of the <see cref="StationRelay"/> class.</summary>
    /// <param name="httpClientFactory">The HttpClient factory.</param>
    /// <param name="nowPlaying">Where song titles go.</param>
    /// <param name="logger">The logger.</param>
    public StationRelay(IHttpClientFactory httpClientFactory, NowPlayingStore nowPlaying, ILogger<StationRelay> logger)
    {
        _httpClientFactory = httpClientFactory;
        _nowPlaying = nowPlaying;
        _logger = logger;
    }

    /// <summary>Gets the wait before a request without a player's Range calls the station (tests shorten it).</summary>
    internal TimeSpan ProbeDelay { get; init; } = DefaultProbeDelay;

    /// <summary>Gets how long the station may take to answer or stay quiet (tests shorten it).</summary>
    internal TimeSpan StationTimeout { get; init; } = DefaultStationTimeout;

    /// <summary>Gets how long a listener may hear silence without live audio coming (tests shorten it).</summary>
    internal TimeSpan MaxSilence { get; init; } = DefaultMaxSilence;

    /// <summary>Returns true for a Range header that a player sends when it starts playing ("bytes=0-").</summary>
    /// <param name="range">The request's Range header, if any.</param>
    /// <returns>True when the request is a player's, not a probe.</returns>
    public static bool IsPlayerRange(string? range) =>
        range is not null && range.Replace(" ", string.Empty, StringComparison.Ordinal).Equals("bytes=0-", StringComparison.OrdinalIgnoreCase);

    /// <summary>Relays the station to <paramref name="output"/> until the listener or the station hangs up.</summary>
    /// <param name="station">A playable station (its format sets the silence frames).</param>
    /// <param name="output">The listener's response body; the response has started.</param>
    /// <param name="playerRequest">True when the request came from a player (<see cref="IsPlayerRange"/>), so the station is called at once.</param>
    /// <param name="cancellationToken">Ends the relay (the listener left).</param>
    /// <returns>True when live audio reached the listener; false when the station failed first.</returns>
    public async Task<bool> RelayAsync(StationConfig station, Stream output, bool playerRequest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(station);
        ArgumentNullException.ThrowIfNull(output);
        using var session = new Session(output, SilentFrame.Create(station.SampleRate, station.BitrateKbps, station.Channels), StationTimeout, MaxSilence);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pump = session.PumpSilenceAsync(stop.Token);
        PrerollFilter? filter = null;
        _nowPlaying.Begin(station.Slug);
        try
        {
            if (!playerRequest)
            {
                await Task.Delay(ProbeDelay, stop.Token).ConfigureAwait(false);
            }

            using var upstream = await OpenAsync(station, stop.Token).ConfigureAwait(false);
            if (upstream is not null)
            {
                filter = new PrerollFilter(upstream.MetaInt, station.BitrateKbps, title => _nowPlaying.Set(station.Slug, title, DateTime.UtcNow));
                await session.CopyAsync(upstream.Body, filter, stop.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or TimeoutException)
        {
            // The listener or the station hung up, or the station stalled.
            if (ex is TimeoutException && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Radio: {Station} stopped sending audio ({Reason}).", station.Slug, ex.Message);
            }
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await pump.ConfigureAwait(false);
            _nowPlaying.End(station.Slug);
        }

        if (filter is { AdsDropped: > 0 } && session.LiveSent)
        {
            _logger.LogInformation(
                "Radio: skipped {Count} ad(s) at the start of {Station}; the listener heard {Seconds:F1} s of silence instead.",
                filter.AdsDropped,
                station.Slug,
                session.SilenceSent.TotalSeconds);
        }

        return session.LiveSent;
    }

    /// <summary>Opens the station and returns the stream to relay, or null when the station did not answer with audio.</summary>
    /// <param name="station">A playable station.</param>
    /// <param name="cancellationToken">Ends the connection.</param>
    /// <returns>The open upstream, which the caller disposes.</returns>
    public async Task<Upstream?> OpenAsync(StationConfig station, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(station);
        var request = new HttpRequestMessage(HttpMethod.Get, station.StreamUrl);
        request.Headers.TryAddWithoutValidation("Icy-MetaData", "1");
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        HttpResponseMessage? response = null;
        using var headers = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        headers.CancelAfter(StationTimeout);
        try
        {
            response = await _httpClientFactory.CreateClient(HttpClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headers.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Radio: {Station} answered HTTP {Status}.", station.Slug, (int)response.StatusCode);
                response.Dispose();
                request.Dispose();
                return null;
            }

            var metaInt = response.Headers.TryGetValues("icy-metaint", out var values)
                && int.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
                ? parsed
                : 0;
            var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return new Upstream(request, response, body, metaInt);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogWarning(ex, "Radio: could not open {Station}.", station.Slug);
            response?.Dispose();
            request.Dispose();
            return null;
        }
    }

    /// <summary>An open station stream.</summary>
    public sealed class Upstream : IDisposable
    {
        private readonly HttpRequestMessage _request;
        private readonly HttpResponseMessage _response;

        internal Upstream(HttpRequestMessage request, HttpResponseMessage response, Stream body, int metaInt)
        {
            _request = request;
            _response = response;
            Body = body;
            MetaInt = metaInt;
        }

        /// <summary>Gets the response body.</summary>
        public Stream Body { get; }

        /// <summary>Gets the <c>icy-metaint</c>; 0 when the station sends no metadata.</summary>
        public int MetaInt { get; }

        /// <inheritdoc />
        public void Dispose()
        {
            Body.Dispose();
            _response.Dispose();
            _request.Dispose();
        }
    }

    // One listener's output. The station copy and the silence pump both write to it, one
    // at a time; silence plays from the start, during an ad, and whenever the live stream
    // has not begun, as much as the clock says plus a head start.
    private sealed class Session : IDisposable
    {
        private readonly Stream _output;
        private readonly SilentFrame _frame;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly ArrayBufferWriter<byte> _silence = new(16 * 1024);
        private readonly ArrayBufferWriter<byte> _audio = new(64 * 1024);
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly TimeSpan _stationTimeout;
        private readonly TimeSpan _maxSilence;
        private TimeSpan _sent;
        private bool _silent = true;
        private TimeSpan _silentSince;

        public Session(Stream output, SilentFrame frame, TimeSpan stationTimeout, TimeSpan maxSilence)
        {
            _output = output;
            _frame = frame;
            _stationTimeout = stationTimeout;
            _maxSilence = maxSilence;
        }

        public TimeSpan SilenceSent { get; private set; }

        public bool LiveSent { get; private set; }

        public async Task PumpSilenceAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        if (_silent)
                        {
                            Fill();
                            await FlushAsync(_silence, cancellationToken).ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        _gate.Release();
                    }

                    await Task.Delay(SilenceTick, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException)
            {
                // The relay ended.
            }
        }

        public async Task CopyAsync(Stream upstream, PrerollFilter filter, CancellationToken cancellationToken)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
            try
            {
                while (true)
                {
                    int read;
                    using (var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                    {
                        idle.CancelAfter(_stationTimeout);
                        try
                        {
                            read = await upstream.ReadAsync(buffer.AsMemory(), idle.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                        {
                            throw new TimeoutException("nothing for " + _stationTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " s");
                        }
                    }

                    if (read == 0)
                    {
                        return;
                    }

                    await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        filter.Feed(buffer.AsSpan(0, read), _audio);
                        if (_audio.WrittenCount > 0)
                        {
                            // Live audio. Any silence already queued plays first.
                            _silent = false;
                            LiveSent = true;
                            await FlushAsync(_audio, cancellationToken).ConfigureAwait(false);
                        }

                        if (filter.IsDropping && !_silent)
                        {
                            // Another ad: silence again, on a fresh clock.
                            _silent = true;
                            _silentSince = _clock.Elapsed;
                        }

                        if (_silent)
                        {
                            if (_clock.Elapsed - _silentSince > _maxSilence)
                            {
                                throw new TimeoutException("no live audio after " + _maxSilence.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " s");
                            }

                            Fill();
                            await FlushAsync(_silence, cancellationToken).ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        _gate.Release();
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        public void Dispose() => _gate.Dispose();

        private void Fill()
        {
            var due = _clock.Elapsed + HeadStart;

            // A slow listener never gets more than the head start queued: silence that is
            // already overdue is skipped, not piled up.
            if (due - _sent > HeadStart + SilenceTick)
            {
                _sent = due - HeadStart - SilenceTick;
            }

            while (_sent < due)
            {
                _silence.Write(_frame.Bytes);
                _sent += _frame.Duration;
                SilenceSent += _frame.Duration;
            }
        }

        private async Task FlushAsync(ArrayBufferWriter<byte> pending, CancellationToken cancellationToken)
        {
            if (pending.WrittenCount == 0)
            {
                return;
            }

            await _output.WriteAsync(pending.WrittenMemory, cancellationToken).ConfigureAwait(false);
            await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
            pending.Clear();
        }
    }
}
