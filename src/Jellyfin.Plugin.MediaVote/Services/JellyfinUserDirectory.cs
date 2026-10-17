using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.MediaVote.Core;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.MediaVote.Services;

/// <summary><see cref="IUserDirectory"/> backed by Jellyfin's user manager.</summary>
public sealed class JellyfinUserDirectory : IUserDirectory
{
    private readonly IUserManager _userManager;

    public JellyfinUserDirectory(IUserManager userManager)
    {
        _userManager = userManager;
    }

    public bool IsActive(Guid userId)
    {
        var user = userId == Guid.Empty ? null : _userManager.GetUserById(userId);
        return user is not null && !user.HasPermission(PermissionKind.IsDisabled);
    }

    public bool IsAdministrator(Guid userId)
    {
        var user = userId == Guid.Empty ? null : _userManager.GetUserById(userId);
        return user is not null && user.HasPermission(PermissionKind.IsAdministrator);
    }

    public string GetUserName(Guid userId)
        => (userId == Guid.Empty ? null : _userManager.GetUserById(userId))?.Username ?? "unknown";

    public IReadOnlyList<Guid> GetActiveUserIds()
        => _userManager.Users.Where(u => !u.HasPermission(PermissionKind.IsDisabled)).Select(u => u.Id).ToList();

    /// <summary>All users for the admin picker.</summary>
    public IReadOnlyList<(Guid Id, string Name, bool Disabled, bool Admin)> ListUsers()
        => _userManager.Users
            .Select(u => (u.Id, u.Username, u.HasPermission(PermissionKind.IsDisabled), u.HasPermission(PermissionKind.IsAdministrator)))
            .OrderBy(u => u.Username, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
