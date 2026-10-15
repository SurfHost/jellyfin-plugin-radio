using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Radio.Relay;

/// <summary>
/// The keys that unlock the relay. Each station has its own key, derived from a random
/// secret in <c>&lt;data&gt;/radio/relay.key</c>. The key never expires, because Jellyfin
/// stores a station's media source (with the key) until the station changes; deleting the
/// file revokes every key at the next restart. A key keeps the relay from serving anyone
/// who only knows its address; it is not a secret from signed-in users.
/// </summary>
public sealed class RelayKeys
{
    private readonly string _path;
    private readonly ILogger<RelayKeys> _logger;
    private readonly object _lock = new();
    private byte[]? _secret;

    /// <summary>Initializes a new instance of the <see cref="RelayKeys"/> class.</summary>
    /// <param name="applicationPaths">Jellyfin's paths.</param>
    /// <param name="logger">The logger.</param>
    public RelayKeys(IApplicationPaths applicationPaths, ILogger<RelayKeys> logger)
        : this(Path.Combine(applicationPaths.DataPath, "radio", "relay.key"), logger)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="RelayKeys"/> class with an explicit file (tests).</summary>
    /// <param name="path">The secret's file.</param>
    /// <param name="logger">The logger.</param>
    public RelayKeys(string path, ILogger<RelayKeys> logger)
    {
        _path = path;
        _logger = logger;
    }

    /// <summary>Returns a station's key.</summary>
    /// <param name="slug">The station slug.</param>
    /// <returns>32 hex digits.</returns>
    public string KeyFor(string slug)
    {
        ArgumentNullException.ThrowIfNull(slug);
        var hash = HMACSHA256.HashData(Secret(), Encoding.UTF8.GetBytes("relay:" + slug));
        return Convert.ToHexStringLower(hash)[..32];
    }

    /// <summary>Checks a key in constant time.</summary>
    /// <param name="slug">The station slug.</param>
    /// <param name="key">The key the caller sent.</param>
    /// <returns>True when it is the station's key.</returns>
    public bool IsValid(string slug, string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(KeyFor(slug)), Encoding.ASCII.GetBytes(key));
    }

    private byte[] Secret()
    {
        lock (_lock)
        {
            if (_secret is not null)
            {
                return _secret;
            }

            try
            {
                if (File.Exists(_path))
                {
                    var stored = Convert.FromHexString(File.ReadAllText(_path).Trim());
                    if (stored.Length >= 32)
                    {
                        return _secret = stored;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
            {
                _logger.LogWarning(ex, "Radio: could not read the relay key {Path}; making a new one.", _path);
            }

            var secret = RandomNumberGenerator.GetBytes(32);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, Convert.ToHexString(secret));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still works until the next restart; stations then get new keys.
                _logger.LogWarning(ex, "Radio: could not save the relay key {Path}.", _path);
            }

            return _secret = secret;
        }
    }
}
