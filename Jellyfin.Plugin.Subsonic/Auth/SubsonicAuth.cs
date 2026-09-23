using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Plugin.Subsonic.Controllers;
using Jellyfin.Plugin.Subsonic.Response;
using Jellyfin.Plugin.Subsonic.Store;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Subsonic.Auth;

/// <summary>Who a request comes from.</summary>
/// <param name="UserId">The Jellyfin user; for share links, the user who shared.</param>
/// <param name="ShareId">Set for share links.</param>
/// <param name="ShareAllowedIds">For share links: the only song ids they may reach.</param>
public record AuthResult(Guid UserId, string? ShareId = null, HashSet<string>? ShareAllowedIds = null);

/// <summary>
/// Resolves Subsonic credentials. Users sign in with their Jellyfin username and password, which
/// Jellyfin itself checks, so its login providers, lockout, disabled accounts, remote-access rules
/// and access schedules all apply. Share links sign in as u=share_&lt;uid&gt; with the share's secret.
/// </summary>
public class SubsonicAuth
{
    // Subsonic clients send the password with every request and Jellyfin's check is deliberately
    // slow, so verified logins are remembered briefly (keyed by an HMAC of name and password under a
    // per-process key, and forgotten when the user changes: LoginCacheInvalidator). Failures always
    // go to Jellyfin, so its lockout counts them.
    private static readonly TimeSpan LoginCacheTime = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, (Guid UserId, DateTime Expires)> _logins = new();
    private readonly byte[] _loginKey = RandomNumberGenerator.GetBytes(32);

    private readonly IUserManager _userManager;
    private readonly ILogger<SubsonicAuth> _logger;

    public SubsonicAuth(IUserManager userManager, ILogger<SubsonicAuth> logger)
    {
        _userManager = userManager;
        _logger = logger;
    }

    public record AuthError(int Code, string Message);

    /// <summary>Returns an <see cref="AuthResult"/> on success, an <see cref="AuthError"/> otherwise.</summary>
    public async Task<object> ResolveAsync(IQueryCollection query, IPAddress? remoteAddress)
    {
        var u = query.First("u").Trim();
        var p = query.First("p");
        var t = query.First("t");
        var s = query.First("s");

        if (!string.IsNullOrEmpty(query.First("apiKey")))
            return new AuthError(ErrorCode.AuthMechanismNotSupported, "API keys are not supported: sign in with your Jellyfin username and password.");
        if (!string.IsNullOrEmpty(p) && (!string.IsNullOrEmpty(t) || !string.IsNullOrEmpty(s)))
            return new AuthError(ErrorCode.ConflictingAuthMechanisms, "Multiple conflicting authentication mechanisms provided.");

        if (u.StartsWith("share_", StringComparison.Ordinal))
            return ResolveShare(u[6..].Trim(), p);

        if (string.IsNullOrEmpty(u))
            return new AuthError(ErrorCode.RequiredParameterMissing, "Required parameter 'u' (username) missing.");
        // Token authentication needs the plaintext password; Jellyfin only keeps a hash of it.
        if (string.IsNullOrEmpty(p) && (!string.IsNullOrEmpty(t) || !string.IsNullOrEmpty(s)))
            return new AuthError(ErrorCode.TokenAuthNotSupported, "Token authentication is not supported: use password authentication (\"legacy\" login in many clients).");
        if (string.IsNullOrEmpty(p))
            return new AuthError(ErrorCode.RequiredParameterMissing, "Required parameter 'p' (password) missing.");
        if (DecodePassword(p) is not { } password)
            return new AuthError(ErrorCode.WrongCredentials, "Wrong username or password.");

        var key = LoginKey(u, password);
        if (_logins.TryGetValue(key, out var known) && known.Expires > DateTime.UtcNow)
            return new AuthResult(known.UserId);

        try
        {
            var user = await _userManager.AuthenticateUser(u, password, remoteAddress?.ToString() ?? string.Empty, isUserSession: false)
                .ConfigureAwait(false);
            if (user == null)
                return new AuthError(ErrorCode.WrongCredentials, "Wrong username or password.");
            Remember(key, user.Id);
            return new AuthResult(user.Id);
        }
        catch (AuthenticationException)
        {
            return new AuthError(ErrorCode.WrongCredentials, "Wrong username or password.");
        }
        catch (SecurityException ex)
        {
            // Jellyfin refused a correct login: account disabled, no remote access or outside its schedule
            _logger.LogInformation("[Subfin] login refused for {User}: {Reason}", u, ex.Message);
            return new AuthError(ErrorCode.NotAuthorized, ex.Message);
        }
    }

    internal enum ShareStatus { Valid, Invalid, Expired }

    /// <summary>
    /// Checks a share link's secret. Expired is only reported to someone holding the right secret;
    /// with sharing disabled every share is invalid.
    /// </summary>
    internal static (ShareStatus Status, ShareRecord? Share) CheckShare(string shareUid, string? secret)
    {
        if (string.IsNullOrEmpty(shareUid) || string.IsNullOrEmpty(secret) || SubsonicPlugin.Instance?.Configuration?.SharingEnabled == false
            || SubsonicStore.GetShare(shareUid) is not { } share
            || SubsonicStore.GetShareSecret(shareUid) is not { } stored
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(stored), Encoding.UTF8.GetBytes(secret)))
        {
            return (ShareStatus.Invalid, null);
        }
        if (share.ExpiresAt is { } exp && DateTimeOffset.Parse(exp, CultureInfo.InvariantCulture) < DateTimeOffset.UtcNow)
            return (ShareStatus.Expired, share);
        return (ShareStatus.Valid, share);
    }

    private static object ResolveShare(string shareUid, string p)
    {
        var (status, share) = CheckShare(shareUid, DecodePassword(p));
        if (status != ShareStatus.Valid || !Guid.TryParse(share!.OwnerUserId, out var owner))
            return new AuthError(ErrorCode.WrongCredentials, "Wrong username or password.");
        return new AuthResult(owner, shareUid, new HashSet<string>(share.EntryIdsFlat));
    }

    private string LoginKey(string username, string password) =>
        Convert.ToHexString(HMACSHA256.HashData(_loginKey, Encoding.UTF8.GetBytes(username.ToLowerInvariant() + "\0" + password)));

    private void Remember(string key, Guid userId)
    {
        var now = DateTime.UtcNow;
        if (_logins.Count > 256)
            foreach (var stale in _logins.Where(kv => kv.Value.Expires <= now).Select(kv => kv.Key).ToList())
                _logins.TryRemove(stale, out _);
        _logins[key] = (userId, now + LoginCacheTime);
    }

    /// <summary>Forgets a user's remembered logins (their password or name changed).</summary>
    public void Forget(Guid userId)
    {
        foreach (var key in _logins.Where(kv => kv.Value.UserId == userId).Select(kv => kv.Key).ToList())
            _logins.TryRemove(key, out _);
    }

    internal static string? DecodePassword(string p)
    {
        if (string.IsNullOrEmpty(p)) return null;
        if (p.StartsWith("enc:", StringComparison.Ordinal))
        {
            try { return Encoding.UTF8.GetString(Convert.FromHexString(p[4..])); }
            catch { return null; }
        }
        return p;
    }
}
