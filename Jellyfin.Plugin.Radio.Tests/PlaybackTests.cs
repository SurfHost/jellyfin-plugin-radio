using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Radio.Playback;
using Jellyfin.Plugin.Radio.Stations;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Radio.Tests;

/// <summary>
/// The channel items and media sources, pinned to what Jellyfin 12.2's playback code
/// needs (docs/DESIGN.md section 1.1).
/// </summary>
public class PlaybackTests
{
    [Fact]
    public void MediaSourceIsADirectPlayMp3LiveStream()
    {
        var source = MediaSourceFactory.Create(StationTests.Valid(), relayThroughServer: false);

        Assert.Equal("radio-npo-radio-2", source.Id);
        Assert.Equal("https://icecast.omroep.nl/radio2-bb-mp3", source.Path);
        Assert.Equal(MediaProtocol.Http, source.Protocol);
        Assert.Equal(MediaSourceType.Default, source.Type);
        Assert.Equal("mp3", source.Container);
        Assert.True(source.IsRemote);
        Assert.True(source.IsInfiniteStream);
        Assert.Null(source.RunTimeTicks);
        Assert.True(source.SupportsDirectPlay);
        Assert.True(source.SupportsDirectStream);
        Assert.False(source.SupportsTranscoding);
        Assert.False(source.SupportsProbing);
        Assert.False(source.RequiresOpening);
        Assert.False(source.RequiresClosing);
        Assert.Equal(192000, source.Bitrate);
        Assert.Equal(0, source.DefaultAudioStreamIndex);
        Assert.True(source.RequiredHttpHeaders is null || source.RequiredHttpHeaders.Count == 0);
    }

    [Fact]
    public void MediaSourceIdIsNeverAGuid()
    {
        // Jellyfin drops a source whose id parses as a foreign GUID for signed-in users.
        foreach (var station in DefaultStations.Load())
        {
            Assert.False(Guid.TryParse(MediaSourceFactory.Create(station, false).Id, out _), station.Slug);
        }
    }

    [Fact]
    public void MediaSourceDescribesExactlyOneAudioStream()
    {
        var source = MediaSourceFactory.Create(StationTests.Valid(), false);
        var stream = Assert.Single(source.MediaStreams);
        Assert.Equal(MediaStreamType.Audio, stream.Type);
        Assert.Equal(0, stream.Index);
        Assert.Equal("mp3", stream.Codec);
        Assert.Equal(192000, stream.BitRate);
        Assert.Equal(48000, stream.SampleRate);
        Assert.Equal(2, stream.Channels);
        Assert.True(stream.IsDefault);
    }

    [Fact]
    public void RelayMakesTheSourceLocal()
    {
        Assert.False(MediaSourceFactory.Create(StationTests.Valid(), relayThroughServer: true).IsRemote);
    }

    [Fact]
    public void ChannelItemIsALiveSong()
    {
        var modified = new DateTime(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
        var item = ChannelItemFactory.Create(StationTests.Valid(), modified, false);

        Assert.Equal("npo-radio-2", item.Id);
        Assert.Equal("NPO Radio 2", item.Name);
        Assert.Equal(ChannelItemType.Media, item.Type);
        Assert.Equal(ChannelMediaType.Audio, item.MediaType);

        // Podcast would make Jellyfin store an AudioBook, with a different item id.
        Assert.Equal(ChannelMediaContentType.Song, item.ContentType);
        Assert.True(item.IsLiveStream);
        Assert.Equal(modified, item.DateModified);
        Assert.Equal("https://www.nporadio2.nl/apple-touch-icon.png", item.ImageUrl);
        Assert.Equal("NPO · Live · MP3 192 kbps", item.Overview);
        Assert.Single(item.MediaSources);
    }

    [Fact]
    public void ChannelItemWithoutLogoHasNoImageUrl()
    {
        var station = StationTests.Valid();
        station.LogoUrl = string.Empty;
        Assert.Null(ChannelItemFactory.Create(station, DateTime.UtcNow, false).ImageUrl);
    }

    [Fact]
    public void IdKeysMatchJellyfin122()
    {
        // ChannelManager.GetIdToHash: externalId + channel name + "16"; GetInternalChannelId: "Channel " + name.
        Assert.Equal("Radio", RadioChannel.ChannelName);
        Assert.Equal("npo-radio-2Radio16", RadioChannel.ItemIdKey("npo-radio-2"));
        Assert.Equal("Channel Radio", RadioChannel.ChannelIdKey());
    }

    [Fact]
    public void HidesTheChannelFromExcludedUsers()
    {
        var excluded = Guid.NewGuid();
        var other = Guid.NewGuid();
        Assert.False(RadioChannel.IsEnabledFor(excluded.ToString("N"), new[] { excluded }));
        Assert.True(RadioChannel.IsEnabledFor(other.ToString("N"), new[] { excluded }));
        Assert.True(RadioChannel.IsEnabledFor(null, new[] { excluded }));
        Assert.True(RadioChannel.IsEnabledFor(other.ToString("N"), null));
    }

    [Fact]
    public async Task ListsTheBuiltInStationsOnAFreshInstall()
    {
        var directory = Path.Combine(Path.GetTempPath(), "radio-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var channel = new RadioChannel(new StationStateStore(directory, NullLogger<StationStateStore>.Instance), NullLogger<RadioChannel>.Instance);

            var result = await channel.GetChannelItems(new InternalChannelItemQuery(), CancellationToken.None);

            Assert.Equal(41, result.Items.Count);
            Assert.Equal(41, result.TotalRecordCount);
            Assert.Equal("npo-radio-1", result.Items[0].Id);
            Assert.All(result.Items, i => Assert.Equal(DateTimeKind.Utc, i.DateModified.Kind));
            Assert.True(File.Exists(Path.Combine(directory, "state.json")));

            var folder = await channel.GetChannelItems(new InternalChannelItemQuery { FolderId = "anything" }, CancellationToken.None);
            Assert.Empty(folder.Items);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Fact]
    public void ChannelDescribesItself()
    {
        var channel = new RadioChannel(new StationStateStore(Path.GetTempPath(), NullLogger<StationStateStore>.Instance), NullLogger<RadioChannel>.Instance);
        var features = channel.GetChannelFeatures();
        Assert.Equal(new[] { ChannelMediaType.Audio }, features.MediaTypes);
        Assert.Equal(new[] { ChannelMediaContentType.Song }, features.ContentTypes);
        Assert.Equal(channel.DataVersion, channel.GetCacheKey(null));
        Assert.Contains(ImageType.Primary, channel.GetSupportedChannelImages());
    }

    [Fact]
    public async Task ChannelImageIsThePng()
    {
        var channel = new RadioChannel(new StationStateStore(Path.GetTempPath(), NullLogger<StationStateStore>.Instance), NullLogger<RadioChannel>.Instance);
        var image = await channel.GetChannelImage(ImageType.Primary, CancellationToken.None);
        Assert.True(image.HasImage);
        Assert.NotNull(image.Stream);
        await image.Stream.DisposeAsync();
    }

    [Fact]
    public void StationsAreListedOnce()
    {
        var slugs = StationCatalog.Playable(new Configuration.PluginConfiguration()).Select(s => s.Slug).ToList();
        Assert.Equal(slugs.Count, slugs.Distinct(StringComparer.Ordinal).Count());
    }
}
