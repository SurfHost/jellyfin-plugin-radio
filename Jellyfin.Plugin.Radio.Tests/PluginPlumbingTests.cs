using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.Radio.Library;
using Jellyfin.Plugin.Radio.Playback;
using Jellyfin.Plugin.Radio.Stations;
using MediaBrowser.Controller.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Jellyfin.Plugin.Radio.Tests;

/// <summary>
/// Release hygiene: the files that name the plugin and its version must agree, the
/// embedded files must be there, and the channel must be registered the way Jellyfin
/// 12.2 finds channels.
/// </summary>
public class PluginPlumbingTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    [Fact]
    public void GuidIsTheSameEverywhere()
    {
        using var meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot, "Jellyfin.Plugin.Radio", "meta.json")));
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot, "manifest.json")));

        Assert.Equal(Plugin.PluginId, meta.RootElement.GetProperty("guid").GetString());
        Assert.Equal(Plugin.PluginId, manifest.RootElement[0].GetProperty("guid").GetString());
        Assert.Contains(Plugin.PluginId, ReadResource(".Web.configPage.html"), StringComparison.Ordinal);
    }

    [Fact]
    public void VersionIsTheSameInMetaAndCsproj()
    {
        using var meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot, "Jellyfin.Plugin.Radio", "meta.json")));
        var csproj = File.ReadAllText(Path.Combine(RepoRoot, "Jellyfin.Plugin.Radio", "Jellyfin.Plugin.Radio.csproj"));
        var version = meta.RootElement.GetProperty("version").GetString();

        foreach (var property in new[] { "Version", "AssemblyVersion", "FileVersion" })
        {
            Assert.Contains("<" + property + ">" + version + "</" + property + ">", csproj, StringComparison.Ordinal);
        }

        Assert.Contains("## [" + version + "]", File.ReadAllText(Path.Combine(RepoRoot, "CHANGELOG.md")), StringComparison.Ordinal);
    }

    [Fact]
    public void TargetsJellyfin12()
    {
        using var meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot, "Jellyfin.Plugin.Radio", "meta.json")));
        Assert.Equal("net10.0", meta.RootElement.GetProperty("framework").GetString());
        Assert.StartsWith("12.", meta.RootElement.GetProperty("targetAbi").GetString(), StringComparison.Ordinal);
        Assert.Equal("logo.png", meta.RootElement.GetProperty("imagePath").GetString());
        Assert.True(File.Exists(Path.Combine(RepoRoot, "logo.png")));
    }

    [Fact]
    public void ConfigPageSurvivesJellyfinWebsTranslation()
    {
        // jellyfin-web runs plugin pages through translateHtml, which replaces every
        // "${...}" token; a JS template literal in the inline script would be mangled.
        var page = ReadResource(".Web.configPage.html");
        Assert.DoesNotContain("${", page, StringComparison.Ordinal);
        Assert.DoesNotContain("`", page, StringComparison.Ordinal);

        // Only the data-role="page" element is kept, so styles must live inside it.
        var pageStart = page.IndexOf("data-role=\"page\"", StringComparison.Ordinal);
        Assert.True(pageStart > 0);
        Assert.True(page.IndexOf("<style>", StringComparison.Ordinal) > pageStart);
    }

    [Fact]
    public void ChannelImageIsEmbedded()
    {
        var assembly = typeof(Plugin).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith(".Images.channel.png", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        var header = new byte[8];
        Assert.Equal(8, stream.Read(header, 0, 8));
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, header);
    }

    [Fact]
    public void RegistersExactlyOneChannelAndTheSyncService()
    {
        var services = new ServiceCollection();
        new PluginServiceRegistrator().RegisterServices(services, null!);

        var channels = services.Where(d => d.ServiceType == typeof(IChannel)).ToList();
        var channel = Assert.Single(channels);
        Assert.Equal(typeof(RadioChannel), channel.ImplementationType);
        Assert.Equal(ServiceLifetime.Singleton, channel.Lifetime);

        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService));
        Assert.Contains(services, d => d.ServiceType == typeof(RadioSync) && d.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(services, d => d.ServiceType == typeof(StationStateStore) && d.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(services, d => d.ServiceType == typeof(StreamProbe) && d.Lifetime == ServiceLifetime.Singleton);
    }

    private static string ReadResource(string suffix)
    {
        var assembly = typeof(Plugin).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith(suffix, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "manifest.json")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
