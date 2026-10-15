using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Dto;

namespace Jellyfin.Plugin.Radio.Relay;

/// <summary>Gives the relay address of a station.</summary>
public interface IRelayAddress
{
    /// <summary>Gets why the relay cannot be used on this server, or an empty string when it can.</summary>
    string Problem { get; }

    /// <summary>Returns the relay address of a station, or null when this server cannot reach its own relay (see <see cref="Problem"/>).</summary>
    /// <param name="slug">The station slug.</param>
    /// <returns>The address, or null.</returns>
    string? For(string slug);
}

/// <summary>
/// The relay's address as a relayed station's media source path:
/// <c>http://127.0.0.1:&lt;http port&gt;/&lt;base url&gt;/Radio/Stream/&lt;slug&gt;?k=&lt;key&gt;</c>.
/// Only the server fetches it. The key goes in the query string because Jellyfin passes a
/// source's required headers upstream only for User-Agent. The key stops the relay from
/// serving anyone who merely knows its address; it is no secret from signed-in users
/// (PlaybackInfo shows the path), and like every channel item the station is reachable
/// through Jellyfin's own stream endpoint anyway.
/// </summary>
public sealed class RelayAddress : IRelayAddress
{
    /// <summary>The route under the server's base URL.</summary>
    public const string Route = "/Radio/Stream/";

    /// <summary>The query parameter with the key.</summary>
    public const string KeyParameter = "k";

    private readonly IServerApplicationHost _appHost;
    private readonly IServerConfigurationManager _config;
    private readonly INetworkManager _network;
    private readonly RelayKeys _keys;

    /// <summary>Initializes a new instance of the <see cref="RelayAddress"/> class.</summary>
    /// <param name="appHost">The server host.</param>
    /// <param name="config">The server configuration.</param>
    /// <param name="network">The network manager.</param>
    /// <param name="keys">The relay keys.</param>
    public RelayAddress(IServerApplicationHost appHost, IServerConfigurationManager config, INetworkManager network, RelayKeys keys)
    {
        _appHost = appHost;
        _config = config;
        _network = network;
        _keys = keys;
    }

    /// <inheritdoc />
    public string Problem
    {
        get
        {
            LocalBase(out var problem);
            return problem;
        }
    }

    /// <summary>Returns true for a media source that points at the relay (a station with "skip the ad at the start").</summary>
    /// <param name="source">A media source.</param>
    /// <returns>True when the source is relayed.</returns>
    public static bool IsRelayed(MediaSourceInfo source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.Id is { } id
            && id.StartsWith(Playback.MediaSourceFactory.IdPrefix, StringComparison.Ordinal)
            && source.Path is { } path
            && path.Contains(Route, StringComparison.Ordinal);
    }

    /// <summary>Builds the relay address from a base URL.</summary>
    /// <param name="baseUrl">The server's local API URL, without a trailing slash.</param>
    /// <param name="slug">The station slug.</param>
    /// <param name="key">The station's key.</param>
    /// <returns>The address.</returns>
    public static string Build(string baseUrl, string slug, string key)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return baseUrl.TrimEnd('/') + Route + Uri.EscapeDataString(slug) + "?" + KeyParameter + "=" + Uri.EscapeDataString(key);
    }

    /// <summary>
    /// Picks the host the server reaches itself on: 127.0.0.1 when Jellyfin listens on all
    /// addresses or on loopback, else the first address it is bound to.
    /// </summary>
    /// <param name="bound">The addresses Jellyfin listens on.</param>
    /// <returns>The host for a URL, or null when there is none.</returns>
    public static string? HostFor(IPAddress[] bound)
    {
        ArgumentNullException.ThrowIfNull(bound);
        // Jellyfin binds [::] only with IPv4 on as well (a dual-mode socket), so 127.0.0.1
        // answers for 0.0.0.0, [::] and loopback alike.
        if (bound.Any(a => a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any) || a.Equals(IPAddress.Loopback)))
        {
            return "127.0.0.1";
        }

        if (bound.Any(a => a.Equals(IPAddress.IPv6Loopback)))
        {
            return "[::1]";
        }

        // Bound to specific addresses only: use one of them (never a link-local IPv6
        // address, which would need a scope id in the URL).
        var address = bound.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
            ?? bound.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetworkV6 && !a.IsIPv6LinkLocal);
        return address is null ? null : address.AddressFamily == AddressFamily.InterNetworkV6 ? "[" + address + "]" : address.ToString();
    }

    /// <inheritdoc />
    public string? For(string slug) => LocalBase(out _) is { } baseUrl ? Build(baseUrl, slug, _keys.KeyFor(slug)) : null;

    // Plain http on the HTTP port, base URL included. RequireHttps redirects every http
    // request, loopback too, to https on an address the certificate does not cover.
    private string? LocalBase(out string problem)
    {
        if (_config.GetNetworkConfiguration().RequireHttps && _appHost.ListenWithHttps)
        {
            problem = "Jellyfin requires HTTPS (Dashboard > Networking), so the server cannot fetch its own relay over HTTP. Skipping the ad needs that setting off.";
            return null;
        }

        var host = HostFor(_network.GetAllBindInterfaces(false).Select(i => i.Address).ToArray());
        if (host is null)
        {
            problem = "Jellyfin is not listening on any address the relay can use.";
            return null;
        }

        problem = string.Empty;
        return _appHost.GetLocalApiUrl(host, Uri.UriSchemeHttp, _appHost.HttpPort);
    }
}
