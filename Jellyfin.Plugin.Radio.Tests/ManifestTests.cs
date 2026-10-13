using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Extensions.Json;
using MediaBrowser.Model.Updates;
using Xunit;

namespace Jellyfin.Plugin.Radio.Tests;

/// <summary>
/// manifest.json must survive the way Jellyfin 12.2 reads a plugin repository
/// (InstallationManager.GetPackages: Jellyfin's own PackageInfo model and JsonDefaults),
/// or the plugin silently never shows up in the catalog.
/// </summary>
public class ManifestTests
{
    [Fact]
    public void ParsesWithJellyfinsOwnModel()
    {
        var packages = JsonSerializer.Deserialize<PackageInfo[]>(File.ReadAllText(Path.Combine(FindRepoRoot(), "manifest.json")), JsonDefaults.Options);

        var package = Assert.Single(packages!);
        Assert.Equal(Guid.Parse(Plugin.PluginId), package.Id);
        Assert.Equal("Radio", package.Name);
        Assert.Equal("General", package.Category);

        // Jellyfin drops versions whose target ABI is newer than the server, then drops a
        // package without versions. A 12.2.0 server reports its version as 12.2.0.0.
        var server = new Version(12, 2, 0, 0);
        Assert.All(package.Versions, v =>
        {
            Assert.True(Version.TryParse(v.TargetAbi, out var abi) && abi <= server, v.TargetAbi);
            Assert.NotNull(v.VersionNumber);
            Assert.StartsWith("https://github.com/SurfHost/jellyfin-plugin-radio/releases/download/v" + v.Version + "/", v.SourceUrl, StringComparison.Ordinal);
            Assert.Matches("^[0-9a-f]{32}$", v.Checksum);
        });
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
