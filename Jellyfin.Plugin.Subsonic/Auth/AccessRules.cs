using System.Net;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Common.Net;

namespace Jellyfin.Plugin.Subsonic.Auth;

/// <summary>The account rules Jellyfin applies at login (<c>IUserManager.AuthenticateUser</c>).</summary>
public static class AccessRules
{
    /// <summary>Why Jellyfin would refuse this user right now, or null.</summary>
    public static string? Denied(User user, IPAddress? remoteIp, INetworkManager network)
    {
        if (user.HasPermission(PermissionKind.IsDisabled))
            return $"The {user.Username} account is currently disabled.";
        if (!user.HasPermission(PermissionKind.EnableRemoteAccess) && (remoteIp == null || !network.IsInLocalNetwork(remoteIp)))
            return "Remote access is disabled for this account.";
        if (!user.IsParentalScheduleAllowed())
            return "This account is not allowed access at this time.";
        return null;
    }
}
