using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Radio.Stations;

/// <summary>What the plugin last published for one station.</summary>
public sealed class StationState
{
    /// <summary>Gets or sets the <see cref="StationCatalog.StationHash"/> last published.</summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>Gets or sets when that hash was first published (UTC).</summary>
    public DateTime ModifiedUtc { get; set; }

    /// <summary>Gets or sets the logo URL last applied to the Jellyfin item, or null if unknown.</summary>
    public string? AppliedLogoUrl { get; set; }
}

/// <summary>
/// Per-station publish state in <c>&lt;data&gt;/radio/state.json</c>. Jellyfin only re-saves a
/// channel item (its stream URL, media source and so on) when the item's modified date
/// moves forward, so each station keeps the date its published content last changed.
/// One lock guards every read-modify-write; the file is replaced atomically.
/// </summary>
public sealed class StationStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly string _directory;
    private readonly string _path;
    private readonly ILogger<StationStateStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, StationState>? _states;

    /// <summary>Initializes a new instance of the <see cref="StationStateStore"/> class.</summary>
    /// <param name="applicationPaths">Jellyfin's paths.</param>
    /// <param name="logger">The logger.</param>
    public StationStateStore(IApplicationPaths applicationPaths, ILogger<StationStateStore> logger)
        : this(Path.Combine(applicationPaths.DataPath, "radio"), logger)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="StationStateStore"/> class at an explicit folder (tests).</summary>
    /// <param name="directory">The folder for state.json.</param>
    /// <param name="logger">The logger.</param>
    public StationStateStore(string directory, ILogger<StationStateStore> logger)
    {
        _directory = directory;
        _path = Path.Combine(directory, "state.json");
        _logger = logger;
    }

    /// <summary>
    /// Records the current hash of each station and returns each station's modified date.
    /// A station seen for the first time, or whose hash changed, gets <paramref name="now"/>.
    /// The file is written only when something changed.
    /// </summary>
    /// <param name="hashes">Slug and current hash of every published station.</param>
    /// <param name="now">The current time (UTC).</param>
    /// <param name="cancellationToken">Cancels the wait for the lock.</param>
    /// <returns>The modified date per slug.</returns>
    public async Task<IReadOnlyDictionary<string, DateTime>> TouchAsync(
        IReadOnlyCollection<KeyValuePair<string, string>> hashes,
        DateTime now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hashes);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var states = Load();
            var changed = false;
            var result = new Dictionary<string, DateTime>(StringComparer.Ordinal);
            foreach (var (slug, hash) in hashes)
            {
                if (!states.TryGetValue(slug, out var state))
                {
                    state = new StationState { Hash = hash, ModifiedUtc = now };
                    states[slug] = state;
                    changed = true;
                }
                else if (!string.Equals(state.Hash, hash, StringComparison.Ordinal))
                {
                    // Strictly later than before, even if the clock went back: Jellyfin
                    // only re-saves the item when the date increases.
                    state.ModifiedUtc = now > state.ModifiedUtc ? now : state.ModifiedUtc.AddSeconds(1);
                    state.Hash = hash;
                    changed = true;
                }

                result[slug] = DateTime.SpecifyKind(state.ModifiedUtc, DateTimeKind.Utc);
            }

            if (changed)
            {
                await SaveAsync(states).ConfigureAwait(false);
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Returns the logo URL last applied to a station's item, or null if unknown.</summary>
    /// <param name="slug">The station slug.</param>
    /// <param name="cancellationToken">Cancels the wait for the lock.</param>
    /// <returns>The URL, or null.</returns>
    public async Task<string?> GetAppliedLogoAsync(string slug, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return Load().TryGetValue(slug, out var state) ? state.AppliedLogoUrl : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Records the logo URL now on a station's item.</summary>
    /// <param name="slug">The station slug.</param>
    /// <param name="logoUrl">The logo URL; empty for none.</param>
    /// <param name="cancellationToken">Cancels the wait for the lock.</param>
    /// <returns>A task.</returns>
    public async Task SetAppliedLogoAsync(string slug, string logoUrl, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var states = Load();
            if (!states.TryGetValue(slug, out var state))
            {
                state = new StationState();
                states[slug] = state;
            }

            if (!string.Equals(state.AppliedLogoUrl, logoUrl, StringComparison.Ordinal))
            {
                state.AppliedLogoUrl = logoUrl;
                await SaveAsync(states).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Forgets stations that are no longer published, so a re-added slug starts fresh.</summary>
    /// <param name="keep">The slugs still published.</param>
    /// <param name="cancellationToken">Cancels the wait for the lock.</param>
    /// <returns>How many entries were removed.</returns>
    public async Task<int> PruneAsync(IReadOnlyCollection<string> keep, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keep);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var states = Load();
            var gone = states.Keys.Where(k => !keep.Contains(k)).ToList();
            foreach (var slug in gone)
            {
                states.Remove(slug);
            }

            if (gone.Count > 0)
            {
                await SaveAsync(states).ConfigureAwait(false);
            }

            return gone.Count;
        }
        finally
        {
            _gate.Release();
        }
    }

    private Dictionary<string, StationState> Load()
    {
        if (_states is not null)
        {
            return _states;
        }

        try
        {
            if (File.Exists(_path))
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, StationState>>(File.ReadAllText(_path), JsonOptions);
                _states = loaded is null
                    ? new Dictionary<string, StationState>(StringComparer.Ordinal)
                    : new Dictionary<string, StationState>(loaded, StringComparer.Ordinal);
                return _states;
            }
        }
        catch (Exception ex)
        {
            // A corrupt file must not take the channel down. Keep it for inspection and
            // start empty: every station is then simply republished once.
            _logger.LogError(ex, "Radio: could not read {Path}; starting with an empty state.", _path);
            TryBackupCorrupt();
        }

        _states = new Dictionary<string, StationState>(StringComparer.Ordinal);
        return _states;
    }

    // Deliberately not cancellable: the caller's token has already bounded the wait for
    // the lock, and abandoning a write half-way would only leave memory and disk apart.
    private async Task SaveAsync(Dictionary<string, StationState> states)
    {
        Directory.CreateDirectory(_directory);
        var temp = _path + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(states, JsonOptions), CancellationToken.None).ConfigureAwait(false);
        File.Move(temp, _path, overwrite: true);
    }

    private void TryBackupCorrupt()
    {
        try
        {
            File.Move(_path, _path + ".corrupt", overwrite: true);
        }
        catch (Exception)
        {
            // Best effort only.
        }
    }
}
