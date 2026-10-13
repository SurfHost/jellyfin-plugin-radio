using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.Radio.Api;

/// <summary>Per-user settings that decide whether the Radio channel works for that user.</summary>
public sealed class UserAccessDto
{
    /// <summary>Gets or sets the user id.</summary>
    public Guid UserId { get; set; }

    /// <summary>Gets or sets the user name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the user is an administrator.</summary>
    public bool IsAdmin { get; set; }

    /// <summary>Gets or sets a value indicating whether the account is disabled.</summary>
    public bool IsDisabled { get; set; }

    /// <summary>Gets or sets a value indicating whether the user may see the Radio channel.</summary>
    public bool ChannelAccess { get; set; }

    /// <summary>Gets or sets a value indicating whether the plugin hides the channel from this user.</summary>
    public bool Excluded { get; set; }

    /// <summary>Gets or sets problems that stop or degrade playback for this user.</summary>
    public List<string> Problems { get; set; } = new();
}

/// <summary>Checks Jellyfin's user settings that affect the Radio channel.</summary>
public sealed class UserAudit
{
    private readonly IUserManager _userManager;

    /// <summary>Initializes a new instance of the <see cref="UserAudit"/> class.</summary>
    /// <param name="userManager">The user manager.</param>
    public UserAudit(IUserManager userManager)
    {
        _userManager = userManager;
    }

    /// <summary>
    /// Reads the calling user's id. Jellyfin.Api, which defines the claim name, is not
    /// on NuGet, so the string is spelled out here as the other SurfHost plugins do.
    /// </summary>
    /// <param name="principal">The caller.</param>
    /// <returns>The user id, or Guid.Empty for an API key.</returns>
    public static Guid GetUserId(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var claim = principal.FindFirst("Jellyfin-UserId")?.Value ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(claim, out var id) ? id : Guid.Empty;
    }

    /// <summary>
    /// Mirrors Jellyfin's channel visibility rule (Channel.IsVisible in 12.2): with a
    /// blocked-channels list, every channel not on it is visible; without one, the user
    /// needs "all channels" or this channel enabled.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="channelId">The Radio channel's id.</param>
    /// <returns>True when the user can see the channel.</returns>
    public static bool CanSeeChannel(User user, Guid channelId)
    {
        ArgumentNullException.ThrowIfNull(user);
        var blocked = user.GetPreferenceValues<Guid>(PreferenceKind.BlockedChannels);
        if (blocked.Length != 0)
        {
            return !blocked.Contains(channelId);
        }

        return user.HasPermission(PermissionKind.EnableAllChannels)
            || user.GetPreferenceValues<Guid>(PreferenceKind.EnabledChannels).Contains(channelId);
    }

    /// <summary>Lists every user with the settings that matter for the Radio channel.</summary>
    /// <param name="channelId">The Radio channel's id.</param>
    /// <param name="excluded">The users the plugin hides the channel from.</param>
    /// <returns>One entry per user.</returns>
    public IReadOnlyList<UserAccessDto> Audit(Guid channelId, IReadOnlyCollection<Guid> excluded)
    {
        var result = new List<UserAccessDto>();
        foreach (var user in _userManager.GetUsers().OrderBy(u => u.Username, StringComparer.OrdinalIgnoreCase))
        {
            var dto = new UserAccessDto
            {
                UserId = user.Id,
                Name = user.Username,
                IsAdmin = user.HasPermission(PermissionKind.IsAdministrator),
                IsDisabled = user.HasPermission(PermissionKind.IsDisabled),
                ChannelAccess = CanSeeChannel(user, channelId),
                Excluded = excluded.Contains(user.Id)
            };

            if (!dto.ChannelAccess)
            {
                dto.Problems.Add("No access to the Radio channel. Enable it under Users > " + user.Username + " > Access > Channels.");
            }

            if (user.HasPermission(PermissionKind.ForceRemoteSourceTranscoding))
            {
                dto.Problems.Add("'Force transcoding of remote media sources' is on (Profile > Playback); live radio cannot be transcoded, so playback fails.");
            }

            // Stations carry no age rating, so blocking unrated channel content empties the channel.
            if (user.GetPreferenceValues<UnratedItem>(PreferenceKind.BlockUnratedItems).Contains(UnratedItem.ChannelContent))
            {
                dto.Problems.Add("Unrated channel content is blocked (Parental control), so the Radio channel shows no stations.");
            }

            result.Add(dto);
        }

        return result;
    }

    /// <summary>
    /// Picks the user to test playback as: the requested one, else the first enabled
    /// non-administrator that can see the channel, else any enabled user.
    /// </summary>
    /// <param name="requested">A user id, or Guid.Empty.</param>
    /// <param name="channelId">The Radio channel's id.</param>
    /// <returns>The user, or null when there are none.</returns>
    public User? PickTestUser(Guid requested, Guid channelId)
    {
        if (requested != Guid.Empty)
        {
            return _userManager.GetUserById(requested);
        }

        var users = _userManager.GetUsers().Where(u => !u.HasPermission(PermissionKind.IsDisabled)).ToList();
        return users.FirstOrDefault(u => !u.HasPermission(PermissionKind.IsAdministrator) && CanSeeChannel(u, channelId))
            ?? users.FirstOrDefault(u => !u.HasPermission(PermissionKind.IsAdministrator))
            ?? users.FirstOrDefault();
    }
}
