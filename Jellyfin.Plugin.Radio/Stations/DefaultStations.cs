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
/// 2026-09-01. Each entry records the plugin version that added it, so an update can add
/// new built-in stations to an existing list without bringing back stations the admin
/// removed. Stations/BuiltInChanges.json records later removals, renames and new logos,
/// which an update applies to the stations the admin has not changed.
/// </summary>
public static class DefaultStations
{
    /// <summary>The list version assumed for settings saved before versions were recorded (0.0.1).</summary>
    public const string FirstVersion = "0.0.1";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>The version that gave stations a category (national or regional).</summary>
    public static readonly Version CategoriesAddedIn = new(0, 0, 4);

    private static readonly Lazy<IReadOnlyList<Entry>> Cached = new(Read);

    private static readonly Lazy<IReadOnlyList<Change>> Changes = new(ReadChanges);

    /// <summary>Gets the newest version that changed the built-in list.</summary>
    public static string LatestVersion => Cached.Value.Select(e => e.Version)
        .Concat(Changes.Value.Select(c => c.Version))
        .Append(CategoriesAddedIn)
        .Max()!
        .ToString();

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
        var merged = ParseVersion(mergedVersion);
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

    /// <summary>
    /// Applies the built-in list's changes since <paramref name="mergedVersion"/> to the
    /// admin's stations. A retired station is removed while its stream URL is still the
    /// built-in one. A new name or logo replaces the old built-in value only where the
    /// admin kept that value, so the admin's own edits win. Lists older than
    /// <see cref="CategoriesAddedIn"/> get the built-in category of each built-in station.
    /// </summary>
    /// <param name="stations">The admin's station list, changed in place.</param>
    /// <param name="mergedVersion">The built-in list version already merged; empty means <see cref="FirstVersion"/>.</param>
    /// <returns>What changed.</returns>
    public static ChangeCount ApplyChangesTo(List<StationConfig> stations, string? mergedVersion)
    {
        ArgumentNullException.ThrowIfNull(stations);
        var merged = ParseVersion(mergedVersion);
        var builtIn = Cached.Value.ToDictionary(e => e.Station.Slug, e => e.Station, StringComparer.Ordinal);
        int removed = 0, updated = 0, categorized = 0;
        foreach (var change in Changes.Value.Where(c => c.Version > merged))
        {
            var index = IndexOf(stations, change.Slug);
            if (index < 0)
            {
                continue;
            }

            var station = stations[index];
            if (change.Remove)
            {
                if (string.Equals(StreamUrlNormalizer.Normalize(station.StreamUrl), StreamUrlNormalizer.Normalize(change.IfStreamUrl), StringComparison.Ordinal))
                {
                    stations.RemoveAt(index);
                    removed++;
                }

                continue;
            }

            if (!builtIn.TryGetValue(change.Slug, out var current))
            {
                continue;
            }

            var touched = false;
            if (change.IfName is not null && string.Equals((station.Name ?? string.Empty).Trim(), change.IfName, StringComparison.Ordinal))
            {
                station.Name = current.Name;
                touched = true;
            }

            if (change.IfLogoUrl is not null && string.Equals((station.LogoUrl ?? string.Empty).Trim(), change.IfLogoUrl, StringComparison.Ordinal))
            {
                station.LogoUrl = current.LogoUrl;
                touched = true;
            }

            if (touched)
            {
                updated++;
            }
        }

        if (merged < CategoriesAddedIn)
        {
            foreach (var station in stations)
            {
                if (builtIn.TryGetValue(station.Slug ?? string.Empty, out var current) && station.Category != current.Category)
                {
                    station.Category = current.Category;
                    categorized++;
                }
            }
        }

        return new ChangeCount(removed, updated, categorized);
    }

    private static Version ParseVersion(string? version) =>
        Version.TryParse(string.IsNullOrWhiteSpace(version) ? FirstVersion : version, out var parsed) ? parsed : new Version(0, 0, 1);

    private static int IndexOf(List<StationConfig> stations, string slug) =>
        stations.FindIndex(s => string.Equals(s.Slug, slug, StringComparison.Ordinal));

    private static string ReadResource(string suffix)
    {
        var assembly = typeof(DefaultStations).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith(suffix, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(suffix + " is not embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static IReadOnlyList<Change> ReadChanges()
    {
        var raw = JsonSerializer.Deserialize<List<RawChange>>(ReadResource(".Stations.BuiltInChanges.json"), JsonOptions) ?? new List<RawChange>();
        return raw.Select(r => new Change(Version.Parse(r.Version), r.Slug, r.Remove, r.IfStreamUrl, r.IfName, r.IfLogoUrl)).ToList();
    }

    private static IReadOnlyList<Entry> Read()
    {
        var raw = JsonSerializer.Deserialize<List<RawStation>>(ReadResource(".Stations.DefaultStations.json"), JsonOptions) ?? new List<RawStation>();
        return raw.Select(r => new Entry(
            new StationConfig
            {
                Slug = r.Slug,
                Name = r.Name,
                Group = r.Group,
                Category = Enum.TryParse<StationCategory>(r.Category, ignoreCase: true, out var category) ? category : StationCategory.National,
                StreamUrl = r.StreamUrl,
                LogoUrl = r.LogoUrl,
                BitrateKbps = r.BitrateKbps,
                SampleRate = r.SampleRate,
                Channels = r.Channels,
                Enabled = r.Enabled
            },
            Version.Parse(string.IsNullOrWhiteSpace(r.AddedIn) ? FirstVersion : r.AddedIn))).ToList();
    }

    /// <summary>What <see cref="ApplyChangesTo"/> did.</summary>
    /// <param name="Removed">Retired built-in stations removed.</param>
    /// <param name="Updated">Stations that got a new built-in name or logo.</param>
    /// <param name="Categorized">Stations that got their built-in category.</param>
    public sealed record ChangeCount(int Removed, int Updated, int Categorized);

    private sealed record Entry(StationConfig Station, Version Version);

    private sealed record Change(Version Version, string Slug, bool Remove, string? IfStreamUrl, string? IfName, string? IfLogoUrl);

    // One entry of BuiltInChanges.json. A null "if" value leaves that field alone.
    private sealed class RawChange
    {
        public string Version { get; set; } = string.Empty;

        public string Slug { get; set; } = string.Empty;

        public bool Remove { get; set; }

        public string? IfStreamUrl { get; set; }

        public string? IfName { get; set; }

        public string? IfLogoUrl { get; set; }
    }

    // The JSON shape; kept apart from StationConfig so "addedIn" never ends up in the settings XML.
    private sealed class RawStation
    {
        public string Slug { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string Group { get; set; } = string.Empty;

        public string Category { get; set; } = string.Empty;

        public string StreamUrl { get; set; } = string.Empty;

        public string LogoUrl { get; set; } = string.Empty;

        public int BitrateKbps { get; set; }

        public int SampleRate { get; set; }

        public int Channels { get; set; }

        public bool Enabled { get; set; } = true;

        public string AddedIn { get; set; } = string.Empty;
    }
}
