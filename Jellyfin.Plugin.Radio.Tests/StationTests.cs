using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Radio.Configuration;
using Jellyfin.Plugin.Radio.Stations;
using Xunit;

namespace Jellyfin.Plugin.Radio.Tests;

/// <summary>The built-in station list, validation, URL normalization and the catalog.</summary>
public class StationTests
{
    [Fact]
    public void DefaultListIsTheMeasuredDutchList()
    {
        var stations = DefaultStations.Load();

        Assert.Equal(54, stations.Count);
        Assert.Equal(41, stations.Count(s => s.Enabled));
        Assert.Equal(stations.Count, stations.Select(s => s.Slug).Distinct(StringComparer.Ordinal).Count());
        Assert.All(StationValidator.ValidateAll(stations), errors => Assert.Empty(errors));
        Assert.All(stations, s =>
        {
            Assert.StartsWith("https://", s.StreamUrl, StringComparison.Ordinal);
            Assert.False(StreamUrlNormalizer.IsStreamTheWorldNode(s.StreamUrl), s.Slug);
            Assert.True(s.BitrateKbps >= 128, s.Slug);
            Assert.Contains(s.SampleRate, new[] { 44100, 48000 });
            Assert.Equal(2, s.Channels);
        });

        // Stopped on 2026-09-01; its mounts answer 404.
        Assert.DoesNotContain(stations, s => s.StreamUrl.Contains("radio6", StringComparison.Ordinal));
        Assert.Equal("npo-radio-1", stations[0].Slug);
    }

    [Theory]
    [InlineData("538-non-stop", "radio-538")]
    [InlineData("qmusic-non-stop", "qmusic")]
    [InlineData("sky-radio-hits", "sky-radio")]
    [InlineData("radio-10-non-stop", "radio-10")]
    [InlineData("100-nl-non-stop", "100-nl")]
    [InlineData("slam-non-stop", "slam")]
    [InlineData("joe-non-stop", "joe")]
    [InlineData("bnr-business-beats", "bnr-nieuwsradio")]
    public void NonStopChannelsFollowTheirMainStation(string channel, string main)
    {
        var slugs = DefaultStations.Load().Select(s => s.Slug).ToList();
        Assert.Equal(slugs.IndexOf(main) + 1, slugs.IndexOf(channel));
    }

    [Fact]
    public void DefaultListReturnsCopies()
    {
        var first = DefaultStations.Load();
        first[0].Name = "changed";
        Assert.Equal("NPO Radio 1", DefaultStations.Load()[0].Name);
    }

    [Theory]
    [InlineData("https://22713.live.streamtheworld.com:443/RADIO538.mp3", "https://playerservices.streamtheworld.com/api/livestream-redirect/RADIO538.mp3")]
    [InlineData("http://25223.live.streamtheworld.com/RADIO538.mp3?dist=web", "https://playerservices.streamtheworld.com/api/livestream-redirect/RADIO538.mp3")]
    [InlineData("https://20043.live.streamtheworld.com/SLAM_MP3_SC", "https://playerservices.streamtheworld.com/api/livestream-redirect/SLAM_MP3.mp3")]
    [InlineData("https://20043.live.streamtheworld.com/SKYRADIOAAC_SC", "https://playerservices.streamtheworld.com/api/livestream-redirect/SKYRADIOAAC.aac")]
    [InlineData("  https://icecast.omroep.nl/radio2-bb-mp3  ", "https://icecast.omroep.nl/radio2-bb-mp3")]
    [InlineData("https://playerservices.streamtheworld.com/api/livestream-redirect/KINK.mp3", "https://playerservices.streamtheworld.com/api/livestream-redirect/KINK.mp3")]
    public void NormalizesStreamUrls(string input, string expected)
    {
        Assert.Equal(expected, StreamUrlNormalizer.Normalize(input));
    }

    [Theory]
    [InlineData("npo-radio-2", true)]
    [InlineData("100-nl", true)]
    [InlineData("NPO", false)]
    [InlineData("-npo", false)]
    [InlineData("npo radio", false)]
    [InlineData("", false)]
    public void ChecksSlugs(string slug, bool valid)
    {
        var station = Valid();
        station.Slug = slug;
        Assert.Equal(valid, StationValidator.Validate(station).Count == 0);
    }

    [Theory]
    [InlineData("https://example.com/radio.m3u")]
    [InlineData("https://example.com/radio.PLS")]
    [InlineData("https://example.com/live/master.m3u8")]
    [InlineData("https://example.com/stream.aac")]
    [InlineData("ftp://example.com/stream.mp3")]
    [InlineData("example.com/stream.mp3")]
    public void RejectsUnusableStreamUrls(string url)
    {
        var station = Valid();
        station.StreamUrl = url;
        Assert.NotEmpty(StationValidator.Validate(station));
    }

    [Fact]
    public void RequiresTheMeasuredFormat()
    {
        var station = Valid();
        station.SampleRate = 0;
        Assert.Contains(StationValidator.Validate(station), e => e.Contains("Press Test", StringComparison.Ordinal));
    }

    [Fact]
    public void ReportsDuplicateSlugsOnTheLaterStation()
    {
        var result = StationValidator.ValidateAll(new List<StationConfig> { Valid(), Valid() });
        Assert.Empty(result[0]);
        Assert.Single(result[1]);
    }

    [Fact]
    public void UsesTheBuiltInListUntilTheDefaultsAreLoaded()
    {
        var config = new PluginConfiguration();
        Assert.Equal(54, StationCatalog.Configured(config).Count);
        Assert.Equal(41, StationCatalog.Playable(config).Count);

        config.DefaultsLoaded = true;
        Assert.Empty(StationCatalog.Configured(config));
        Assert.Empty(StationCatalog.Playable(config));
    }

    [Fact]
    public void PublishesOnlyEnabledValidStationsWithNormalizedUrls()
    {
        var node = Valid("node");
        node.StreamUrl = "https://22713.live.streamtheworld.com:443/RADIO538.mp3";
        var off = Valid("off");
        off.Enabled = false;
        var broken = Valid("broken");
        broken.StreamUrl = "https://example.com/list.m3u";
        var config = new PluginConfiguration { DefaultsLoaded = true, Stations = new List<StationConfig> { node, off, broken, Valid("ok") } };

        var playable = StationCatalog.Playable(config);

        Assert.Equal(new[] { "node", "ok" }, playable.Select(s => s.Slug));
        Assert.Equal("https://playerservices.streamtheworld.com/api/livestream-redirect/RADIO538.mp3", playable[0].StreamUrl);
        Assert.Equal("https://22713.live.streamtheworld.com:443/RADIO538.mp3", config.Stations[0].StreamUrl);
    }

    [Fact]
    public void StationHashFollowsEverythingPublished()
    {
        var station = Valid();
        var hash = StationCatalog.StationHash(station, false);

        Assert.Equal(hash, StationCatalog.StationHash(Valid(), false));
        Assert.NotEqual(hash, StationCatalog.StationHash(station, true));

        foreach (var change in new Action<StationConfig>[]
        {
            s => s.Name = "Other",
            s => s.Group = "Other",
            s => s.StreamUrl = "https://example.com/other.mp3",
            s => s.LogoUrl = "https://example.com/other.png",
            s => s.BitrateKbps = 128,
            s => s.SampleRate = 44100,
            s => s.Channels = 1
        })
        {
            var changed = Valid();
            change(changed);
            Assert.NotEqual(hash, StationCatalog.StationHash(changed, false));
        }
    }

    [Fact]
    public void CatalogHashFollowsOrder()
    {
        var a = Valid("a");
        var b = Valid("b");
        Assert.NotEqual(StationCatalog.CatalogHash(new[] { a, b }, false), StationCatalog.CatalogHash(new[] { b, a }, false));
    }

    [Fact]
    public void OverviewShowsGroupAndFormat()
    {
        Assert.Equal("NPO · Live · MP3 192 kbps", StationCatalog.Overview(Valid()));
        var noGroup = Valid();
        noGroup.Group = " ";
        Assert.Equal("Live · MP3 192 kbps", StationCatalog.Overview(noGroup));
    }

    internal static StationConfig Valid(string slug = "npo-radio-2") => new()
    {
        Slug = slug,
        Name = "NPO Radio 2",
        Group = "NPO",
        StreamUrl = "https://icecast.omroep.nl/radio2-bb-mp3",
        LogoUrl = "https://www.nporadio2.nl/apple-touch-icon.png",
        BitrateKbps = 192,
        SampleRate = 48000,
        Channels = 2,
        Enabled = true
    };
}
