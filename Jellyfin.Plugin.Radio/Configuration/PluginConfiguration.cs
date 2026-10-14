using System;
using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Radio.Configuration;

/// <summary>
/// Plugin settings. Only administrators can read or change them; nothing here is a
/// secret, but the station list is the admin's to curate.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>The default name of the national folder, as the (Dutch) apps show it.</summary>
    public const string DefaultNationalFolderName = "Landelijk";

    /// <summary>The default name of the regional folder.</summary>
    public const string DefaultRegionalFolderName = "Regionaal";

    /// <summary>
    /// Gets or sets a value indicating whether the built-in Dutch station list has been
    /// copied into <see cref="Stations"/>. Until then the built-in list is used as is, so
    /// a fresh install has stations without anyone opening the plugin page. Kept apart
    /// from an empty list, which means the admin removed every station.
    /// </summary>
    public bool DefaultsLoaded { get; set; }

    /// <summary>
    /// Gets or sets the version of the built-in list already merged into <see cref="Stations"/>.
    /// After an update, built-in stations added in a newer version are added automatically;
    /// older ones are never re-added, so removing a station sticks. Empty means 0.0.1.
    /// </summary>
    public string DefaultsVersion { get; set; } = string.Empty;

    /// <summary>Gets or sets the stations, in display order.</summary>
    public List<StationConfig> Stations { get; set; } = new();

    /// <summary>
    /// Gets or sets the name of the folder with the national stations. The apps show the
    /// folders only when both categories have stations. Empty means the default.
    /// </summary>
    public string NationalFolderName { get; set; } = DefaultNationalFolderName;

    /// <summary>Gets or sets the name of the folder with the regional stations. Empty means the default.</summary>
    public string RegionalFolderName { get; set; } = DefaultRegionalFolderName;

    /// <summary>
    /// Gets or sets the users who do not see the Radio channel, for example a living-room
    /// Android TV account (that app cannot play channel audio). This hides the channel
    /// view only; access to the stations themselves follows Jellyfin's channel permission.
    /// </summary>
    public Guid[] ExcludedUserIds { get; set; } = Array.Empty<Guid>();

    /// <summary>
    /// Gets or sets a value indicating whether apps that ask Jellyfin for a redirect to the
    /// station (Android Auto) get the stream through the server instead. The web, Android
    /// and iOS apps always get it through the server; Jellyfin Desktop and Chromecast always
    /// fetch the station's URL themselves, with or without this setting. Off by default,
    /// which saves the server's upload. Turn it on when Android Auto fails, e.g. on a server
    /// reached over http while a station redirects to https.
    /// </summary>
    public bool RelayThroughServer { get; set; }

    /// <summary>Returns the folder name for a category, or its default when the setting is blank.</summary>
    /// <param name="category">The category.</param>
    /// <returns>The trimmed folder name.</returns>
    public string FolderName(StationCategory category)
    {
        var (name, fallback) = category == StationCategory.Regional
            ? (RegionalFolderName, DefaultRegionalFolderName)
            : (NationalFolderName, DefaultNationalFolderName);
        return string.IsNullOrWhiteSpace(name) ? fallback : name.Trim();
    }
}
