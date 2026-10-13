using System;
using System.Linq;
using Jellyfin.Plugin.Radio.Library;
using Jellyfin.Plugin.Radio.Playback;
using Jellyfin.Plugin.Radio.Stations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Jellyfin.Plugin.Radio.Tests;

/// <summary>Fixes from the 2026-10-13 review: the channel folder guard, order, and unsafe text.</summary>
public class ReviewFixTests
{
    private static readonly Guid Radio = Guid.NewGuid();

    [Fact]
    public void GuardRefusesAForeignFolderUnderTheRadioChannel()
    {
        Assert.True(ChannelFolderGuard.IsBlocked(Radio, Guid.NewGuid(), Radio));
    }

    [Fact]
    public void GuardAllowsTheRootListing()
    {
        Assert.False(ChannelFolderGuard.IsBlocked(Radio, null, Radio));
        Assert.False(ChannelFolderGuard.IsBlocked(Radio, Guid.Empty, Radio));
        Assert.False(ChannelFolderGuard.IsBlocked(Radio, Radio, Radio));
    }

    [Fact]
    public void GuardLeavesOtherChannelsAlone()
    {
        Assert.False(ChannelFolderGuard.IsBlocked(Guid.NewGuid(), Guid.NewGuid(), Radio));
    }

    [Fact]
    public void GuardIsAGlobalMvcFilter()
    {
        var services = new ServiceCollection();
        new PluginServiceRegistrator().RegisterServices(services, null!);
        using var provider = services.BuildServiceProvider();

        var filters = provider.GetRequiredService<IOptions<MvcOptions>>().Value.Filters;
        Assert.Contains(filters, f => f is ServiceFilterAttribute s && s.ServiceType == typeof(ChannelFolderGuard));
        Assert.Contains(services, d => d.ServiceType == typeof(ChannelFolderGuard) && d.Lifetime == ServiceLifetime.Singleton);
    }

    [Fact]
    public void SortNamesFollowThePageOrder()
    {
        var names = new[] { "NPO Radio 1", "Arrow Classic Rock", "100% NL" };
        var sorted = names.Select((n, i) => RadioSync.SortNameFor(i, n)).OrderBy(s => s, StringComparer.Ordinal).ToList();

        Assert.Equal("001 NPO Radio 1", sorted[0]);
        Assert.Equal("002 Arrow Classic Rock", sorted[1]);
        Assert.Equal("003 100% NL", sorted[2]);
    }

    [Theory]
    [InlineData("Radio\u000BVeronica", true)]
    [InlineData("Radio\u0000", true)]
    [InlineData("Radio\u0085", true)]
    [InlineData("Radio￾", true)]
    [InlineData("Radio \"Gouwe Ouwe\"", false)]
    [InlineData("Omrop Fryslân", false)]
    [InlineData("Radio 📻", false)]
    [InlineData("", false)]
    public void DetectsTextThatXmlCannotHold(string text, bool invalid)
    {
        Assert.Equal(invalid, StationValidator.HasInvalidCharacters(text));
    }

    [Fact]
    public void RefusesAStationWithAControlCharacter()
    {
        var station = StationTests.Valid();
        station.Group = "NPO\u000B";
        Assert.Contains(StationValidator.Validate(station), e => e.Contains("control character", StringComparison.Ordinal));
    }
}
