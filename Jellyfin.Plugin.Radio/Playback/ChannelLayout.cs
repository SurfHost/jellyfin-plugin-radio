using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Radio.Configuration;

namespace Jellyfin.Plugin.Radio.Playback;

/// <summary>
/// Where each station goes in the channel. With stations in both categories, the channel
/// holds two folders (national, regional) and each station sits in its category's folder;
/// with one category, the stations sit straight under the channel.
/// </summary>
/// <remarks>
/// Moving a station is the hard part. Jellyfin moves an item when it is listed under a
/// new container, and deletes the items of a container that its listing leaves out, which
/// takes the station's playlist entries with it. So a container keeps listing the stations
/// Jellyfin still has in it until they have been listed at their new place, and the root
/// keeps listing a folder while stations are still inside it. A station therefore moves
/// without ever being deleted, whatever the order in which the containers are listed.
/// </remarks>
public static class ChannelLayout
{
    /// <summary>The channel root, as a container id.</summary>
    public const string Root = "";

    /// <summary>The external id of the national folder. Never change it: Jellyfin derives the folder's item id from it.</summary>
    public const string NationalFolder = "category-national";

    /// <summary>The external id of the regional folder. Never change it.</summary>
    public const string RegionalFolder = "category-regional";

    /// <summary>Gets the folders, in display order.</summary>
    public static IReadOnlyList<string> Folders { get; } = new[] { NationalFolder, RegionalFolder };

    /// <summary>Returns the folder of a category.</summary>
    /// <param name="category">The category.</param>
    /// <returns>The folder's external id.</returns>
    public static string FolderOf(StationCategory category) =>
        category == StationCategory.Regional ? RegionalFolder : NationalFolder;

    /// <summary>Returns the category a folder holds, or null for anything that is not one of the folders.</summary>
    /// <param name="folder">An external folder id.</param>
    /// <returns>The category.</returns>
    public static StationCategory? CategoryOf(string? folder) => folder switch
    {
        NationalFolder => StationCategory.National,
        RegionalFolder => StationCategory.Regional,
        _ => null
    };

    /// <summary>Returns true when the published stations use both categories, so the channel shows folders.</summary>
    /// <param name="playable">The playable stations.</param>
    /// <returns>True for folders, false for a flat list.</returns>
    public static bool UsesFolders(IReadOnlyList<StationConfig> playable)
    {
        ArgumentNullException.ThrowIfNull(playable);
        return playable.Select(s => s.Category).Distinct().Skip(1).Any();
    }

    /// <summary>Returns the container a station belongs in.</summary>
    /// <param name="station">The station.</param>
    /// <param name="usesFolders">Whether the channel shows folders.</param>
    /// <returns><see cref="Root"/> or a folder id.</returns>
    public static string TargetOf(StationConfig station, bool usesFolders)
    {
        ArgumentNullException.ThrowIfNull(station);
        return usesFolders ? FolderOf(station.Category) : Root;
    }

    /// <summary>Works out what one container lists.</summary>
    /// <param name="container"><see cref="Root"/> or a folder id; anything else lists nothing.</param>
    /// <param name="playable">The playable stations, in display order.</param>
    /// <param name="locationOf">
    /// Where Jellyfin has a station's item now: <see cref="Root"/>, a folder id, or null
    /// when it has none (or keeps it anywhere else).
    /// </param>
    /// <returns>The folders and stations to list, in display order.</returns>
    public static Listing List(string? container, IReadOnlyList<StationConfig> playable, Func<string, string?> locationOf)
    {
        ArgumentNullException.ThrowIfNull(playable);
        ArgumentNullException.ThrowIfNull(locationOf);
        container ??= Root;
        if (container != Root && CategoryOf(container) is null)
        {
            return new Listing(Array.Empty<string>(), Array.Empty<StationConfig>());
        }

        var usesFolders = UsesFolders(playable);
        var locations = playable.ToDictionary(s => s.Slug, s => locationOf(s.Slug), StringComparer.Ordinal);
        var stations = playable
            .Where(s => TargetOf(s, usesFolders) == container || locations[s.Slug] == container)
            .ToList();
        var folders = container == Root
            ? Folders.Where(f => playable.Any(s => (usesFolders && FolderOf(s.Category) == f) || locations[s.Slug] == f)).ToList()
            : new List<string>();
        return new Listing(folders, stations);
    }

    /// <summary>What a container lists.</summary>
    /// <param name="Folders">The folders (root only).</param>
    /// <param name="Stations">The stations.</param>
    public sealed record Listing(IReadOnlyList<string> Folders, IReadOnlyList<StationConfig> Stations);
}
