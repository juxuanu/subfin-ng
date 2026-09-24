using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
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
/// Resolves Subsonic credentials. Users sign in with their Jellyfin username and either the
/// OpenSubsonic password an administrator generated for them (as a password or a token), or their
/// Jellyfin password, which Jellyfin itself checks (its login providers and lockout apply). Share
/// links sign in as u=share_&lt;uid&gt; with the share's secret. Disabled accounts, remote access
/// and access schedules are checked on every request (<see cref="AccessRules"/>).
/// </summary>
public class SubsonicAuth
{
    // Subsonic clients send the password with every request and Jellyfin's check is deliberately
    // slow, so verified logins are remembered briefly (keyed by an HMAC of name and password under a
    // per-process key, and forgotten when the user changes: UserEventConsumer). Failures always
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
            return new AuthError(ErrorCode.AuthMechanismNotSupported, "API keys are not supported: sign in with your Jellyfin username and your OpenSubsonic or Jellyfin password.");
        if (!string.IsNullOrEmpty(p) && (!string.IsNullOrEmpty(t) || !string.IsNullOrEmpty(s)))
            return new AuthError(ErrorCode.ConflictingAuthMechanisms, "Multiple conflicting authentication mechanisms provided.");

        if (u.StartsWith("share_", StringComparison.Ordinal))
            return ResolveShare(u[6..].Trim(), p);

        if (string.IsNullOrEmpty(u))
            return new AuthError(ErrorCode.RequiredParameterMissing, "Required parameter 'u' (username) missing.");
        if (!string.IsNullOrEmpty(t) || !string.IsNullOrEmpty(s))
            return ResolveToken(u, t, s);
        if (string.IsNullOrEmpty(p))
            return new AuthError(ErrorCode.RequiredParameterMissing, "Required parameter 'p' (password) missing.");
        if (DecodePassword(p) is not { } password)
            return new AuthError(ErrorCode.WrongCredentials, "Wrong username or password.");

        // The generated OpenSubsonic password is checked here, without Jellyfin
        if (_userManager.GetUserByName(u) is { } named && SubsonicPasswordOf(named.Id) is { } generated
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(generated), Encoding.UTF8.GetBytes(password)))
        {
            return new AuthResult(named.Id);
        }

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

    /// <summary>
    /// Token login: t = md5(password + s). Only the generated OpenSubsonic password can be checked
    /// this way, since Jellyfin keeps just a hash of the Jellyfin password.
    /// </summary>
    private object ResolveToken(string u, string t, string s)
    {
        if (string.IsNullOrEmpty(t) || string.IsNullOrEmpty(s))
            return new AuthError(ErrorCode.RequiredParameterMissing, "Token login needs both 't' and 's'.");
        if (_userManager.GetUserByName(u) is not { } user)
            return new AuthError(ErrorCode.WrongCredentials, "Wrong username or password.");
        if (SubsonicPasswordOf(user.Id) is not { } generated)
        {
            return new AuthError(ErrorCode.TokenAuthNotSupported,
                "This account has no OpenSubsonic password for token login: an administrator can generate one in "
                + "Dashboard > Plugins > Subfin, or use password (\"legacy\") login with the Jellyfin password.");
        }
        var expected = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(generated + s)));
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(t.ToLowerInvariant()))
            ? new AuthResult(user.Id)
            : new AuthError(ErrorCode.WrongCredentials, "Wrong username or password.");
    }

    private static string? SubsonicPasswordOf(Guid userId) => SubsonicStore.GetSubsonicPassword(userId.ToString("N"));

    /// <summary>A new OpenSubsonic password: 4 groups of 5 characters without look-alikes (about 116 bits).</summary>
    public static string GeneratePassword()
    {
        const string chars = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        return string.Join('-', Enumerable.Range(0, 4).Select(_ =>
            new string(Enumerable.Range(0, 5).Select(_ => chars[RandomNumberGenerator.GetInt32(chars.Length)]).ToArray())));
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
