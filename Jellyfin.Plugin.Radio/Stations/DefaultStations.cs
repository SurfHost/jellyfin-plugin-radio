using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.Radio.Configuration;

namespace Jellyfin.Plugin.Radio.Stations;

/// <summary>
/// The built-in Dutch station list (Stations/DefaultStations.json). Every stream was
/// fetched on 2026-10-13 and its format read from the MP3 frame headers; only MP3
/// streams are included. NPO Soul &amp; Jazz is missing on purpose: it stopped on
/// 2026-09-01 (NPO Blend replaced it).
/// </summary>
public static class DefaultStations
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly Lazy<IReadOnlyList<StationConfig>> Cached = new(Read);

    /// <summary>Returns fresh copies of the built-in stations, in display order.</summary>
    /// <returns>The stations.</returns>
    public static List<StationConfig> Load() => Cached.Value.Select(s => s.Clone()).ToList();

    private static IReadOnlyList<StationConfig> Read()
    {
        var assembly = typeof(DefaultStations).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith(".Stations.DefaultStations.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("DefaultStations.json is not embedded.");
        using var reader = new StreamReader(stream);
        return JsonSerializer.Deserialize<List<StationConfig>>(reader.ReadToEnd(), JsonOptions)
            ?? new List<StationConfig>();
    }
}
