using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Jellyfin.Plugin.Radio.Stations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Radio.Tests;

/// <summary>The per-station publish state that moves an item's modified date forward.</summary>
public sealed class StationStateStoreTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "radio-state-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    [Fact]
    public async Task FirstSightIsNow()
    {
        var result = await Store().TouchAsync(Hashes(("a", "h1")), T0);
        Assert.Equal(T0, result["a"]);
    }

    [Fact]
    public async Task SameHashKeepsTheDate()
    {
        var store = Store();
        await store.TouchAsync(Hashes(("a", "h1")), T0);
        var later = await store.TouchAsync(Hashes(("a", "h1")), T0.AddHours(1));
        Assert.Equal(T0, later["a"]);
    }

    [Fact]
    public async Task ChangedHashMovesTheDateForward()
    {
        var store = Store();
        await store.TouchAsync(Hashes(("a", "h1"), ("b", "x")), T0);
        var later = await store.TouchAsync(Hashes(("a", "h2"), ("b", "x")), T0.AddHours(1));
        Assert.Equal(T0.AddHours(1), later["a"]);
        Assert.Equal(T0, later["b"]);
    }

    [Fact]
    public async Task DateMovesForwardEvenWhenTheClockWentBack()
    {
        var store = Store();
        await store.TouchAsync(Hashes(("a", "h1")), T0);
        var result = await store.TouchAsync(Hashes(("a", "h2")), T0.AddHours(-1));
        Assert.True(result["a"] > T0);
    }

    [Fact]
    public async Task SurvivesARestart()
    {
        await Store().TouchAsync(Hashes(("a", "h1")), T0);
        await Store().SetAppliedLogoAsync("a", "https://example.com/a.png");

        var reopened = Store();
        var result = await reopened.TouchAsync(Hashes(("a", "h1")), T0.AddDays(1));
        Assert.Equal(T0, result["a"]);
        Assert.Equal("https://example.com/a.png", await reopened.GetAppliedLogoAsync("a"));
        Assert.False(File.Exists(Path.Combine(_directory, "state.json.tmp")));
    }

    [Fact]
    public async Task CorruptFileStartsEmptyAndIsKept()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Path.Combine(_directory, "state.json"), "{ not json");

        var result = await Store().TouchAsync(Hashes(("a", "h1")), T0);

        Assert.Equal(T0, result["a"]);
        Assert.True(File.Exists(Path.Combine(_directory, "state.json.corrupt")));
    }

    [Fact]
    public async Task PruneForgetsRemovedStations()
    {
        var store = Store();
        await store.TouchAsync(Hashes(("a", "h1"), ("b", "h1")), T0);
        Assert.Equal(1, await store.PruneAsync(new[] { "a" }));

        var result = await store.TouchAsync(Hashes(("b", "h1")), T0.AddHours(2));
        Assert.Equal(T0.AddHours(2), result["b"]);
    }

    [Fact]
    public async Task UnknownLogoIsNull()
    {
        Assert.Null(await Store().GetAppliedLogoAsync("nope"));
    }

    private static List<KeyValuePair<string, string>> Hashes(params (string Slug, string Hash)[] items)
    {
        var list = new List<KeyValuePair<string, string>>();
        foreach (var (slug, hash) in items)
        {
            list.Add(new KeyValuePair<string, string>(slug, hash));
        }

        return list;
    }

    private StationStateStore Store() => new(_directory, NullLogger<StationStateStore>.Instance);
}
