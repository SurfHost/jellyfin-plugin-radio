using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Radio.Relay;

/// <summary>What a relayed station is playing.</summary>
/// <param name="Slug">The station.</param>
/// <param name="Title">The song, as the station sends it ("Artist - Title" or "Title - ARTIST").</param>
/// <param name="SinceUtc">When the plugin first saw this title.</param>
public sealed record NowPlayingEntry(string Slug, string Title, DateTime SinceUtc);

/// <summary>
/// The latest song title per station, read from the stream metadata while the relay plays
/// the station. Kept in memory only; a station's title goes when its last listener leaves.
/// </summary>
public sealed class NowPlayingStore
{
    private readonly ConcurrentDictionary<string, NowPlayingEntry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _listeners = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    /// <summary>Counts a listener of a station.</summary>
    /// <param name="slug">The station.</param>
    public void Begin(string slug)
    {
        lock (_lock)
        {
            _listeners[slug] = _listeners.GetValueOrDefault(slug) + 1;
        }
    }

    /// <summary>Counts a listener off; the station's title goes with the last one.</summary>
    /// <param name="slug">The station.</param>
    public void End(string slug)
    {
        lock (_lock)
        {
            var left = _listeners.GetValueOrDefault(slug) - 1;
            if (left > 0)
            {
                _listeners[slug] = left;
                return;
            }

            _listeners.Remove(slug);
            _entries.TryRemove(slug, out _);
        }
    }

    /// <summary>Records a title.</summary>
    /// <param name="slug">The station.</param>
    /// <param name="title">The title.</param>
    /// <param name="nowUtc">The current time.</param>
    public void Set(string slug, string title, DateTime nowUtc)
    {
        lock (_lock)
        {
            // Nobody listens any more: the relay already ended.
            if (_listeners.ContainsKey(slug)
                && !(_entries.TryGetValue(slug, out var existing) && string.Equals(existing.Title, title, StringComparison.Ordinal)))
            {
                _entries[slug] = new NowPlayingEntry(slug, title, nowUtc);
            }
        }
    }

    /// <summary>Returns the titles seen, newest first.</summary>
    /// <returns>The entries.</returns>
    public IReadOnlyList<NowPlayingEntry> All() => _entries.Values.OrderByDescending(e => e.SinceUtc).ToList();
}
