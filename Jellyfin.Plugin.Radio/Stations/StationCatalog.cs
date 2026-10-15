using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.Radio.Configuration;

namespace Jellyfin.Plugin.Radio.Stations;

/// <summary>The stations the channel publishes, and the hashes that tell Jellyfin they changed.</summary>
public static class StationCatalog
{
    /// <summary>
    /// Returns the configured stations: the admin's list once the defaults were loaded
    /// into it, the built-in list before that.
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    /// <returns>The stations in display order.</returns>
    public static IReadOnlyList<StationConfig> Configured(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.DefaultsLoaded ? (IReadOnlyList<StationConfig>?)config.Stations ?? Array.Empty<StationConfig>() : DefaultStations.Load();
    }

    /// <summary>
    /// Returns copies of the enabled, valid stations with their URLs normalized. Invalid
    /// stations are skipped; the plugin page reports why.
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    /// <returns>The stations to publish, in display order.</returns>
    public static IReadOnlyList<StationConfig> Playable(PluginConfiguration config)
    {
        var all = Configured(config);
        var errors = StationValidator.ValidateAll(all);
        var result = new List<StationConfig>();
        for (var i = 0; i < all.Count; i++)
        {
            if (!all[i].Enabled || errors[i].Count > 0)
            {
                continue;
            }

            var station = all[i].Clone();
            station.Name = station.Name.Trim();
            station.Group = (station.Group ?? string.Empty).Trim();
            station.StreamUrl = StreamUrlNormalizer.Normalize(station.StreamUrl);
            station.LogoUrl = (station.LogoUrl ?? string.Empty).Trim();
            result.Add(station);
        }

        return result;
    }

    /// <summary>
    /// Hash of everything that ends up in a station's Jellyfin item or media source. When
    /// it changes, the station's modified date moves forward so Jellyfin re-saves the item.
    /// </summary>
    /// <param name="station">A playable station.</param>
    /// <param name="relayThroughServer">The relay setting, which changes the media source.</param>
    /// <param name="relayPath">The relay address of a station that skips its pre-roll ad, else null.</param>
    /// <returns>A short hex hash.</returns>
    public static string StationHash(StationConfig station, bool relayThroughServer, string? relayPath = null)
    {
        ArgumentNullException.ThrowIfNull(station);
        return Hash(string.Join(
            '\n',
            "v3",
            station.Slug,
            station.Name,
            station.Group,
            station.Category.ToString(),
            station.StreamUrl,
            station.LogoUrl,
            station.BitrateKbps.ToString(CultureInfo.InvariantCulture),
            station.SampleRate.ToString(CultureInfo.InvariantCulture),
            station.Channels.ToString(CultureInfo.InvariantCulture),
            relayThroughServer ? "relay" : "direct",
            station.SkipPreroll ? "skip-preroll" : "as-is",
            relayPath ?? string.Empty));
    }

    /// <summary>
    /// Hash of the whole published list. Used as the channel's data version and cache key,
    /// so an edit is picked up at once instead of after Jellyfin's 3-hour channel cache.
    /// </summary>
    /// <param name="playable">The playable stations, in order.</param>
    /// <param name="relayThroughServer">The relay setting.</param>
    /// <returns>A short hex hash.</returns>
    public static string CatalogHash(IReadOnlyList<StationConfig> playable, bool relayThroughServer)
    {
        ArgumentNullException.ThrowIfNull(playable);
        return Hash(string.Join('\n', playable.Select(s => StationHash(s, relayThroughServer))));
    }

    /// <summary>
    /// Hash of everything the channel lists: the stations and the folder names. Used as the
    /// channel's data version and cache key.
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    /// <returns>A short hex hash.</returns>
    public static string ListingHash(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return Hash(string.Join(
            '\n',
            CatalogHash(Playable(config), config.RelayThroughServer),
            config.FolderName(StationCategory.National),
            config.FolderName(StationCategory.Regional)));
    }

    /// <summary>The line shown under the station name, e.g. "NPO · Live · MP3 192 kbps".</summary>
    /// <param name="station">The station.</param>
    /// <returns>The overview text.</returns>
    public static string Overview(StationConfig station)
    {
        ArgumentNullException.ThrowIfNull(station);
        var format = "Live · MP3 " + station.BitrateKbps.ToString(CultureInfo.InvariantCulture) + " kbps";
        return string.IsNullOrWhiteSpace(station.Group) ? format : station.Group.Trim() + " · " + format;
    }

    private static string Hash(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
}
