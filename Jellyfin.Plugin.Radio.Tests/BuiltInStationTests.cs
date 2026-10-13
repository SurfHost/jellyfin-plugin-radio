using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Radio.Configuration;
using Jellyfin.Plugin.Radio.Library;
using Jellyfin.Plugin.Radio.Stations;
using Xunit;

namespace Jellyfin.Plugin.Radio.Tests;

/// <summary>New built-in stations reach existing installs after an update; removals stick.</summary>
public class BuiltInStationTests
{
    [Fact]
    public void LatestVersionIsTheNewestAddition()
    {
        Assert.Equal("0.0.2", DefaultStations.LatestVersion);
    }

    [Fact]
    public void AFreshInstallGetsTheWholeList()
    {
        var config = new PluginConfiguration();

        Assert.NotNull(RadioSync.ApplyBuiltInStations(config));

        Assert.True(config.DefaultsLoaded);
        Assert.Equal(54, config.Stations.Count);
        Assert.Equal("0.0.2", config.DefaultsVersion);
        Assert.Null(RadioSync.ApplyBuiltInStations(config));
    }

    [Fact]
    public void An001ListGetsTheNewStationsAfterTheirMainStation()
    {
        var config = Legacy001Config();

        var change = RadioSync.ApplyBuiltInStations(config);

        Assert.Equal("added 10 new built-in stations", change);
        var slugs = config.Stations.Select(s => s.Slug).ToList();
        Assert.Equal(slugs.IndexOf("qmusic") + 1, slugs.IndexOf("qmusic-non-stop"));
        Assert.Equal(slugs.IndexOf("radio-538") + 1, slugs.IndexOf("538-non-stop"));
        Assert.Equal(slugs.IndexOf("sky-radio-hits") + 1, slugs.IndexOf("sky-radio-non-stop-at-work"));
        Assert.Equal(slugs.IndexOf("joe") + 1, slugs.IndexOf("joe-non-stop"));
        Assert.Equal("my-station", slugs[^1]);
        Assert.Equal("0.0.2", config.DefaultsVersion);
    }

    [Fact]
    public void AnOldStationTheAdminRemovedStaysRemoved()
    {
        var config = Legacy001Config();
        RadioSync.ApplyBuiltInStations(config);
        Assert.DoesNotContain(config.Stations, s => s.Slug == "sublime");
    }

    [Fact]
    public void ANewStationTheAdminRemovedStaysRemoved()
    {
        var config = Legacy001Config();
        RadioSync.ApplyBuiltInStations(config);
        config.Stations.RemoveAll(s => s.Slug == "joe-non-stop");

        Assert.Null(RadioSync.ApplyBuiltInStations(config));
        Assert.DoesNotContain(config.Stations, s => s.Slug == "joe-non-stop");
    }

    [Fact]
    public void MergingTwiceAddsNothing()
    {
        var stations = Legacy001Config().Stations;
        Assert.Equal(10, DefaultStations.MergeNewInto(stations, string.Empty));
        Assert.Equal(0, DefaultStations.MergeNewInto(stations, string.Empty));
    }

    [Fact]
    public void AddedStationsKeepTheirBuiltInSwitch()
    {
        var config = Legacy001Config();
        RadioSync.ApplyBuiltInStations(config);
        Assert.True(config.Stations.Single(s => s.Slug == "slam-non-stop").Enabled);
        Assert.False(config.Stations.Single(s => s.Slug == "bnr-business-beats").Enabled);
    }

    [Fact]
    public void ThePagesFirstSaveRecordsTheListVersion()
    {
        var incoming = new PluginConfiguration { DefaultsLoaded = true };
        Plugin.StampDefaultsVersion(new PluginConfiguration(), incoming);
        Assert.Equal("0.0.2", incoming.DefaultsVersion);
    }

    [Fact]
    public void ALegacySaveLeavesTheVersionForTheMerge()
    {
        var incoming = new PluginConfiguration { DefaultsLoaded = true };
        Plugin.StampDefaultsVersion(new PluginConfiguration { DefaultsLoaded = true }, incoming);
        Assert.Equal(string.Empty, incoming.DefaultsVersion);
    }

    // A list saved by 0.0.1: the 0.0.1 built-in stations, one removed by the admin
    // (Sublime) and one of the admin's own at the end; no recorded version.
    private static PluginConfiguration Legacy001Config()
    {
        var stations = DefaultStations.Load().Where(s => s.Group != "Non-stop" && s.Slug != "sublime").ToList();
        stations.Add(new StationConfig { Slug = "my-station", Name = "My station", StreamUrl = "https://example.com/live.mp3", BitrateKbps = 128, SampleRate = 44100, Channels = 2 });
        return new PluginConfiguration { DefaultsLoaded = true, Stations = stations };
    }
}
