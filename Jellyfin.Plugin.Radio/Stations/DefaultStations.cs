using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.Radio.Configuration;

namespace Jellyfin.Plugin.Radio.Stations;

/// <summary>
/// The built-in Dutch station list (Stations/DefaultStations.json). Every stream was
/// fetched and its format read from the MP3 frame headers before it was added; only MP3
/// streams are included. NPO Soul &amp; Jazz is missing on purpose: it stopped on
/// 2026-09-01 (NPO Blend replaced it). Each entry records the plugin version that added
/// it, so an update can add new built-in stations to an existing list without bringing
/// back stations the admin removed.
/// </summary>
public static class DefaultStations
{
    /// <summary>The list version assumed for settings saved before versions were recorded (0.0.1).</summary>
    public const string FirstVersion = "0.0.1";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly Lazy<IReadOnlyList<Entry>> Cached = new(Read);

    /// <summary>Gets the newest version that added built-in stations.</summary>
    public static string LatestVersion => Cached.Value.Select(e => e.Version).Max()!.ToString();

    /// <summary>Returns fresh copies of the built-in stations, in display order.</summary>
    /// <returns>The stations.</returns>
    public static List<StationConfig> Load() => Cached.Value.Select(e => e.Station.Clone()).ToList();

    /// <summary>
    /// Adds the built-in stations that are newer than <paramref name="mergedVersion"/> and
    /// not in the list yet. Each goes right after the nearest earlier built-in station that
    /// is in the list (a non-stop channel after its main station), or at the end.
    /// Stations from older versions are never re-added, so an admin's removals stick.
    /// </summary>
    /// <param name="stations">The admin's station list, changed in place.</param>
    /// <param name="mergedVersion">The built-in list version already merged; empty means <see cref="FirstVersion"/>.</param>
    /// <returns>How many stations were added.</returns>
    public static int MergeNewInto(List<StationConfig> stations, string? mergedVersion)
    {
        ArgumentNullException.ThrowIfNull(stations);
        var merged = Version.TryParse(string.IsNullOrWhiteSpace(mergedVersion) ? FirstVersion : mergedVersion, out var parsed)
            ? parsed
            : new Version(0, 0, 1);
        var entries = Cached.Value;
        var added = 0;
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (entry.Version <= merged || IndexOf(stations, entry.Station.Slug) >= 0)
            {
                continue;
            }

            var at = stations.Count;
            for (var k = i - 1; k >= 0; k--)
            {
                var previous = IndexOf(stations, entries[k].Station.Slug);
                if (previous >= 0)
                {
                    at = previous + 1;
                    break;
                }
            }

            stations.Insert(at, entry.Station.Clone());
            added++;
        }

        return added;
    }

    private static int IndexOf(List<StationConfig> stations, string slug) =>
        stations.FindIndex(s => string.Equals(s.Slug, slug, StringComparison.Ordinal));

    private static IReadOnlyList<Entry> Read()
    {
        var assembly = typeof(DefaultStations).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith(".Stations.DefaultStations.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("DefaultStations.json is not embedded.");
        using var reader = new StreamReader(stream);
        var raw = JsonSerializer.Deserialize<List<RawStation>>(reader.ReadToEnd(), JsonOptions) ?? new List<RawStation>();
        return raw.Select(r => new Entry(
            new StationConfig
            {
                Slug = r.Slug,
                Name = r.Name,
                Group = r.Group,
                StreamUrl = r.StreamUrl,
                LogoUrl = r.LogoUrl,
                BitrateKbps = r.BitrateKbps,
                SampleRate = r.SampleRate,
                Channels = r.Channels,
                Enabled = r.Enabled
            },
            Version.Parse(string.IsNullOrWhiteSpace(r.AddedIn) ? FirstVersion : r.AddedIn))).ToList();
    }

    private sealed record Entry(StationConfig Station, Version Version);

    // The JSON shape; kept apart from StationConfig so "addedIn" never ends up in the settings XML.
    private sealed class RawStation
    {
        public string Slug { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string Group { get; set; } = string.Empty;

        public string StreamUrl { get; set; } = string.Empty;

        public string LogoUrl { get; set; } = string.Empty;

        public int BitrateKbps { get; set; }

        public int SampleRate { get; set; }

        public int Channels { get; set; }

        public bool Enabled { get; set; } = true;

        public string AddedIn { get; set; } = string.Empty;
    }
}
