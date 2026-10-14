using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Radio.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Radio;

/// <summary>
/// Radio plugin. Publishes Dutch radio stations as a Jellyfin channel whose items are
/// live audio, so they play in every Jellyfin app that shows channels.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>The plugin id, shared with meta.json and manifest.json.</summary>
    public const string PluginId = "b44dc395-cb0c-4e22-9ba3-5d10a39484ed";

    /// <summary>Initializes a new instance of the <see cref="Plugin"/> class.</summary>
    /// <param name="applicationPaths">Jellyfin's paths.</param>
    /// <param name="xmlSerializer">The XML serializer.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>Gets the current plugin instance.</summary>
    public static Plugin? Instance { get; private set; }

    /// <summary>
    /// Gets the current configuration, or defaults while the plugin is not loaded
    /// (unit tests).
    /// </summary>
    public static PluginConfiguration Config => Instance?.Configuration ?? new PluginConfiguration();

    /// <inheritdoc />
    public override string Name => "Radio";

    /// <inheritdoc />
    public override string Description => "Listen to Dutch radio stations in every Jellyfin app.";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse(PluginId);

    /// <summary>
    /// Reports the version the way meta.json and the plugin catalog write it
    /// (MAJOR.MINOR.PATCH). Jellyfin takes the version it lists for a running plugin
    /// from the assembly, which always has four parts (0.1.0.0), but it looks the
    /// plugin's image up by version against meta.json's 0.1.0. Those two never match,
    /// so the logo would vanish from the plugin pages after every restart.
    /// </summary>
    /// <returns>The plugin info with a three-part version.</returns>
    public override PluginInfo GetPluginInfo()
    {
        var info = base.GetPluginInfo();
        info.Version = ManifestVersion(info.Version);
        return info;
    }

    /// <inheritdoc />
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        if (configuration is PluginConfiguration incoming)
        {
            StampDefaultsVersion(Configuration, incoming);
            incoming.NationalFolderName = CleanFolderName(incoming.NationalFolderName);
            incoming.RegionalFolderName = CleanFolderName(incoming.RegionalFolderName);
        }

        base.UpdateConfiguration(configuration);
    }

    /// <summary>
    /// Trims a folder name to at most 60 characters. A name with a character the settings
    /// XML cannot hold becomes empty (the default name): Jellyfin silently resets all
    /// settings when it cannot read the file back.
    /// </summary>
    /// <param name="name">The name as entered.</param>
    /// <returns>The name to store.</returns>
    public static string CleanFolderName(string? name)
    {
        var text = (name ?? string.Empty).Trim();
        if (Stations.StationValidator.HasInvalidCharacters(text))
        {
            return string.Empty;
        }

        if (text.Length <= 60)
        {
            return text;
        }

        // Never cut an emoji (a surrogate pair) in half.
        return text[..(char.IsHighSurrogate(text[59]) ? 59 : 60)].TrimEnd();
    }

    /// <summary>
    /// The plugin page saves the built-in list it showed when the settings had none yet.
    /// That list is the current one, so record its version; otherwise the next start would
    /// treat it as the 0.0.1 list and re-add newer stations the admin just removed.
    /// </summary>
    /// <param name="current">The settings before the save.</param>
    /// <param name="incoming">The settings being saved, changed in place.</param>
    public static void StampDefaultsVersion(PluginConfiguration current, PluginConfiguration incoming)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(incoming);
        if (incoming.DefaultsLoaded && string.IsNullOrEmpty(incoming.DefaultsVersion) && !current.DefaultsLoaded)
        {
            incoming.DefaultsVersion = Stations.DefaultStations.LatestVersion;
        }
    }

    /// <summary>Drops a zero fourth part from a version: 0.1.0.0 becomes 0.1.0.</summary>
    /// <param name="version">The assembly version.</param>
    /// <returns>The version as meta.json writes it.</returns>
    public static Version ManifestVersion(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return version.Revision == 0 ? new Version(version.Major, version.Minor, version.Build) : version;
    }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages() => new[]
    {
        new PluginPageInfo
        {
            Name = "Radio",
            DisplayName = "Radio",
            EmbeddedResourcePath = GetType().Namespace + ".Web.configPage.html",
            EnableInMainMenu = true,
            MenuIcon = "radio"
        }
    };
}
