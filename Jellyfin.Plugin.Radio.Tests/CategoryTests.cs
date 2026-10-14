using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Radio.Configuration;
using Jellyfin.Plugin.Radio.Library;
using Jellyfin.Plugin.Radio.Playback;
using Jellyfin.Plugin.Radio.Stations;
using Xunit;

namespace Jellyfin.Plugin.Radio.Tests;

/// <summary>0.0.4: national and regional folders, moving stations into them, and the built-in list changes.</summary>
public class CategoryTests
{
    private const string Root = ChannelLayout.Root;
    private const string National = ChannelLayout.NationalFolder;
    private const string Regional = ChannelLayout.RegionalFolder;

    [Fact]
    public void BothCategoriesGiveTwoFolders()
    {
        var playable = new[] { Station("a"), Station("r", StationCategory.Regional), Station("b") };

        var root = ChannelLayout.List(Root, playable, _ => null);
        Assert.Equal(new[] { National, Regional }, root.Folders);
        Assert.Empty(root.Stations);

        Assert.Equal(new[] { "a", "b" }, Slugs(ChannelLayout.List(National, playable, _ => null)));
        Assert.Equal(new[] { "r" }, Slugs(ChannelLayout.List(Regional, playable, _ => null)));
    }

    [Fact]
    public void OneCategoryStaysFlat()
    {
        var playable = new[] { Station("r1", StationCategory.Regional), Station("r2", StationCategory.Regional) };

        var root = ChannelLayout.List(Root, playable, _ => null);
        Assert.Empty(root.Folders);
        Assert.Equal(new[] { "r1", "r2" }, Slugs(root));
        Assert.Empty(ChannelLayout.List(Regional, playable, _ => null).Stations);
    }

    [Fact]
    public void AnUnknownFolderListsNothing()
    {
        var playable = new[] { Station("a"), Station("r", StationCategory.Regional) };
        var listing = ChannelLayout.List("anything", playable, _ => "anything");
        Assert.Empty(listing.Folders);
        Assert.Empty(listing.Stations);
    }

    [Fact]
    public void AStationIsListedWhereItIsUntilItHasMoved()
    {
        var playable = new[] { Station("a"), Station("r", StationCategory.Regional) };
        string? Location(string slug) => Root;

        // Upgrading from the flat list: the root keeps both stations next to the folders.
        var root = ChannelLayout.List(Root, playable, Location);
        Assert.Equal(new[] { National, Regional }, root.Folders);
        Assert.Equal(new[] { "a", "r" }, Slugs(root));
    }

    [Fact]
    public void TheRootKeepsAFolderWhileStationsAreInside()
    {
        // Every station became national, but one still sits in the regional folder.
        var playable = new[] { Station("a"), Station("b") };
        string? Location(string slug) => slug == "b" ? Regional : Root;

        var root = ChannelLayout.List(Root, playable, Location);
        Assert.Equal(new[] { Regional }, root.Folders);
        Assert.Equal(new[] { "a", "b" }, Slugs(root));
        Assert.Equal(new[] { "b" }, Slugs(ChannelLayout.List(Regional, playable, Location)));
    }

    public static TheoryData<string, string[], string[]> Moves() => new()
    {
        // Upgrade from 0.0.3: every station at the root, both categories now.
        { "flat to folders", new[] { "a:N:", "b:N:", "r:R:" }, new[] { "a:N", "b:N", "r:R" } },

        // The admin moves a station to the other category.
        { "national to regional", new[] { "a:N:" + National, "b:N:" + National, "r:R:" + Regional }, new[] { "a:N", "b:R", "r:R" } },

        // Two stations swap categories at once: no listing order suits both.
        { "swap", new[] { "a:N:" + National, "b:N:" + National, "r:R:" + Regional, "s:R:" + Regional }, new[] { "a:N", "b:R", "r:R", "s:N" } },

        // The last regional station becomes national: back to a flat list.
        { "folders to flat", new[] { "a:N:" + National, "r:R:" + Regional }, new[] { "a:N", "r:N" } },

        // A new station next to existing ones.
        { "new station", new[] { "a:N:" + National, "r:R:" + Regional }, new[] { "a:N", "r:R", "n:R" } },
    };

    [Theory]
    [MemberData(nameof(Moves))]
    public void MovingNeverDeletesAStationWhateverTheListingOrder(string scenario, string[] before, string[] after)
    {
        var playable = after.Select(Parse).Select(p => p.Station).ToList();
        var targets = playable.ToDictionary(s => s.Slug, s => ChannelLayout.TargetOf(s, ChannelLayout.UsesFolders(playable)));

        // Apps may list any container that exists, in any order, before the sync pass runs.
        foreach (var prefix in ListingOrders())
        {
            var jellyfin = new FakeJellyfin(before.Select(Parse).Where(p => p.Location is not null).ToDictionary(p => p.Station.Slug, p => p.Location!));
            foreach (var container in prefix)
            {
                jellyfin.List(container, playable);
            }

            jellyfin.SyncPass(playable);

            Assert.True(jellyfin.Deleted.Count == 0, scenario + " after " + string.Join(",", prefix) + ": deleted " + string.Join(",", jellyfin.Deleted));
            Assert.Equal(targets.OrderBy(t => t.Key, StringComparer.Ordinal), jellyfin.Parents.OrderBy(p => p.Key, StringComparer.Ordinal));
            var used = ChannelLayout.UsesFolders(playable)
                ? playable.Select(s => ChannelLayout.FolderOf(s.Category)).Distinct().OrderBy(f => f, StringComparer.Ordinal).ToArray()
                : Array.Empty<string>();
            Assert.Equal(used, jellyfin.Folders.OrderBy(f => f, StringComparer.Ordinal).ToArray());
        }
    }

    [Fact]
    public void ADisabledStationIsDeletedWhereItIs()
    {
        var jellyfin = new FakeJellyfin(new Dictionary<string, string> { ["a"] = National, ["r"] = Regional, ["gone"] = Regional });
        var playable = new[] { Station("a"), Station("r", StationCategory.Regional) };

        jellyfin.SyncPass(playable);

        Assert.Equal(new[] { "gone" }, jellyfin.Deleted);
    }

    [Fact]
    public void A003ListGetsTheChangesOfThisVersion()
    {
        var stations = new List<StationConfig>
        {
            Old("npo-radio-1", "NPO Radio 1", "https://icecast.omroep.nl/radio1-bb-mp3", "https://www.nporadio1.nl/apple-touch-icon.png"),
            Old("npo-blend", "NPO Blend", "https://icecast.omroep.nl/npoblend-bb-mp3", string.Empty),
            Old("npo-sterren-nl", "NPO Sterren NL", "https://example.com/my-own-sterren.mp3", string.Empty),
            Old("538-non-stop", "538 Non-Stop", "https://playerservices.streamtheworld.com/api/livestream-redirect/TLPSTR09.mp3", "https://ctfimages.talparad.io/2AIuJh6rcNAFWaDbH3cKEg/2048835da3011e2b76633a94bf56c558/538_512x512.jpg"),
            Old("qmusic", "Qmusic", "https://stream.qmusic.nl/qmusic/mp3", "https://example.com/my-own-logo.png"),
            Old("sky-radio-hits", "Sky Radio Hits", "https://22713.live.streamtheworld.com:443/SRGSTR01.mp3", string.Empty),
            Old("sky-radio-non-stop-at-work", "Sky @ my desk", "https://playerservices.streamtheworld.com/api/livestream-redirect/SRGSTR24.mp3", "https://ctfimages.talparad.io/THnBtZH5I5RABCOp9WPpI/0d5fa4e65d6fd1e3e9d349088fecaed3/Sky_radio_-_512x512.jpg"),
            Old("radio-veronica", "Radio Veronica", "https://playerservices.streamtheworld.com/api/livestream-redirect/VERONICA.mp3", string.Empty),
            Old("nh-radio", "NH Radio", "https://ice.cr6.streamzilla.xlcdn.com:8000/sz=nhnieuws=NHRadio_mp3", "https://www.nhnieuws.nl/nh/apple-touch-icon.png"),
            Old("my-station", "My station", "https://example.com/live.mp3", string.Empty),
        };
        var config = new PluginConfiguration { DefaultsLoaded = true, DefaultsVersion = "0.0.2", Stations = stations };

        var change = RadioSync.ApplyBuiltInStations(config);

        Assert.Equal("removed 2 retired built-in stations, updated the name or logo of 3 built-in stations, put 1 built-in stations in their category", change);
        Assert.Equal("0.0.4", config.DefaultsVersion);
        var bySlug = config.Stations.ToDictionary(s => s.Slug);

        // Retired: removed, unless the admin repointed it (Sterren NL); a node URL of the same mount counts as unchanged.
        Assert.False(bySlug.ContainsKey("npo-blend"));
        Assert.False(bySlug.ContainsKey("sky-radio-hits"));
        Assert.True(bySlug.ContainsKey("npo-sterren-nl"));

        // New name and logo where the admin kept the old ones; the admin's own edits stay.
        Assert.Equal("Radio 538 Non-Stop", bySlug["538-non-stop"].Name);
        Assert.Equal("https://www.538.nl/icons/icon-512x512.png", bySlug["538-non-stop"].LogoUrl);
        Assert.Equal("https://example.com/my-own-logo.png", bySlug["qmusic"].LogoUrl);
        Assert.Equal("Sky @ my desk", bySlug["sky-radio-non-stop-at-work"].Name);
        Assert.StartsWith("https://images.ctfassets.net/", bySlug["sky-radio-non-stop-at-work"].LogoUrl, StringComparison.Ordinal);
        Assert.StartsWith("https://api.mediahuisradio.nl/", bySlug["radio-veronica"].LogoUrl, StringComparison.Ordinal);

        // Categories: built-in regional stations move; the admin's own station stays national.
        Assert.Equal(StationCategory.Regional, bySlug["nh-radio"].Category);
        Assert.Equal(StationCategory.National, bySlug["my-station"].Category);

        // Applied once.
        Assert.Null(RadioSync.ApplyBuiltInStations(config));
    }

    [Fact]
    public void AnUpToDateListIsLeftAlone()
    {
        var config = new PluginConfiguration { DefaultsLoaded = true, DefaultsVersion = "0.0.3", Stations = DefaultStations.Load() };
        var before = config.Stations.Select(s => (s.Slug, s.Name, s.LogoUrl)).ToList();

        Assert.Equal("built-in station list is up to date", RadioSync.ApplyBuiltInStations(config));
        Assert.Equal(before, config.Stations.Select(s => (s.Slug, s.Name, s.LogoUrl)).ToList());
    }

    [Fact]
    public void FolderNamesFallBackToTheDefaults()
    {
        var config = new PluginConfiguration { NationalFolderName = "  ", RegionalFolderName = " Regio " };
        Assert.Equal("Landelijk", config.FolderName(StationCategory.National));
        Assert.Equal("Regio", config.FolderName(StationCategory.Regional));
        Assert.Equal("Regionaal", new PluginConfiguration().FolderName(StationCategory.Regional));
    }

    [Fact]
    public void FolderNamesAreStoredSafely()
    {
        Assert.Equal("National", Plugin.CleanFolderName("  National "));
        Assert.Equal(string.Empty, Plugin.CleanFolderName("Bad\u000bname"));
        Assert.Equal(60, Plugin.CleanFolderName(new string('x', 80)).Length);
        Assert.Equal(59, Plugin.CleanFolderName(new string('x', 59) + "\U0001F4FB" + "x").Length);
    }

    [Fact]
    public void RenamingAFolderChangesTheListing()
    {
        var config = new PluginConfiguration();
        var hash = StationCatalog.ListingHash(config);
        config.RegionalFolderName = "Regio";
        Assert.NotEqual(hash, StationCatalog.ListingHash(config));
    }

    private static IEnumerable<string[]> ListingOrders()
    {
        var containers = new[] { Root, National, Regional };
        yield return Array.Empty<string>();
        foreach (var a in containers)
        {
            yield return new[] { a };
            foreach (var b in containers)
            {
                yield return new[] { a, b };
                foreach (var c in containers)
                {
                    yield return new[] { a, b, c };
                }
            }
        }
    }

    private static StationConfig Station(string slug, StationCategory category = StationCategory.National) => new()
    {
        Slug = slug,
        Name = slug,
        Category = category,
        StreamUrl = "https://example.com/" + slug + ".mp3",
        BitrateKbps = 128,
        SampleRate = 44100,
        Channels = 2
    };

    private static StationConfig Old(string slug, string name, string url, string logo) => new()
    {
        Slug = slug,
        Name = name,
        StreamUrl = url,
        LogoUrl = logo,
        BitrateKbps = 128,
        SampleRate = 44100,
        Channels = 2
    };

    // "slug:N|R:location" with an empty location for the root and none for "no item yet".
    private static (StationConfig Station, string? Location) Parse(string spec)
    {
        var parts = spec.Split(':');
        var station = Station(parts[0], parts[1] == "R" ? StationCategory.Regional : StationCategory.National);
        return (station, parts.Length > 2 ? parts[2] : null);
    }

    private static string[] Slugs(ChannelLayout.Listing listing) => listing.Stations.Select(s => s.Slug).ToArray();

    /// <summary>
    /// What Jellyfin 12.2's ChannelManager.GetChannelItemsInternal does with a listing:
    /// listed items move under the listed container, the container's other items are
    /// deleted, and deleting a folder deletes what is inside it.
    /// </summary>
    private sealed class FakeJellyfin
    {
        public FakeJellyfin(Dictionary<string, string> parents)
        {
            Parents = parents;
            Folders = parents.Values.Where(v => v != Root).ToHashSet(StringComparer.Ordinal);
        }

        public Dictionary<string, string> Parents { get; }

        public HashSet<string> Folders { get; }

        public List<string> Deleted { get; } = new();

        public void List(string container, IReadOnlyList<StationConfig> playable)
        {
            if (container != Root && !Folders.Contains(container))
            {
                // Jellyfin cannot list a folder it does not have.
                return;
            }

            var listing = ChannelLayout.List(container, playable, slug => Parents.TryGetValue(slug, out var parent) ? parent : null);
            foreach (var folder in listing.Folders)
            {
                Folders.Add(folder);
            }

            var listed = listing.Stations.Select(s => s.Slug).ToHashSet(StringComparer.Ordinal);
            foreach (var slug in listed)
            {
                Parents[slug] = container;
            }

            foreach (var slug in Parents.Where(p => p.Value == container && !listed.Contains(p.Key)).Select(p => p.Key).ToList())
            {
                Delete(slug);
            }

            if (container == Root)
            {
                foreach (var folder in Folders.Where(f => !listing.Folders.Contains(f)).ToList())
                {
                    Folders.Remove(folder);
                    foreach (var slug in Parents.Where(p => p.Value == folder).Select(p => p.Key).ToList())
                    {
                        Delete(slug);
                    }
                }
            }
        }

        // RadioSync's order: the root, every folder Jellyfin has, the root again.
        public void SyncPass(IReadOnlyList<StationConfig> playable)
        {
            List(Root, playable);
            foreach (var folder in ChannelLayout.Folders)
            {
                List(folder, playable);
            }

            List(Root, playable);
        }

        private void Delete(string slug)
        {
            Parents.Remove(slug);
            Deleted.Add(slug);
        }
    }
}
