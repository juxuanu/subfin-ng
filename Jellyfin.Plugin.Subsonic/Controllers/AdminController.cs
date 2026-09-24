using System.Globalization;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Subsonic.Auth;
using Jellyfin.Plugin.Subsonic.Store;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Subsonic.Controllers;

/// <summary>
/// Backs the plugin settings page (administrators only): each Jellyfin user's OpenSubsonic password,
/// which clients that only do token login (md5 of password + salt) need.
/// </summary>
[ApiController]
[Route("opensubsonic/admin")]
[Authorize(Policy = Policies.RequiresElevation)]
public class AdminController : ControllerBase
{
    private readonly IUserManager _userManager;
    private readonly ILogger<AdminController> _logger;

    public AdminController(IUserManager userManager, ILogger<AdminController> logger)
    {
        _userManager = userManager;
        _logger = logger;
    }

    /// <summary>Every Jellyfin user, and when their OpenSubsonic password was generated (null: none).</summary>
    [HttpGet("users")]
    public IActionResult GetUsers()
    {
        var generated = SubsonicStore.GetSubsonicPasswordDates();
        return Ok(_userManager.GetUsers()
            .OrderBy(u => u.Username, StringComparer.OrdinalIgnoreCase)
            .Select(u => new SubsonicUserInfo(
                u.Id.ToString("N"),
                u.Username,
                u.HasPermission(PermissionKind.IsAdministrator),
                u.HasPermission(PermissionKind.IsDisabled),
                generated.TryGetValue(u.Id.ToString("N"), out var at) ? ToIso(at) : null)));
    }

    /// <summary>Generates a new OpenSubsonic password, replacing any earlier one. It's only ever shown here.</summary>
    [HttpPost("users/{userId:guid}/password")]
    public IActionResult GeneratePassword(Guid userId)
    {
        if (_userManager.GetUserById(userId) is not { } user) return NotFound();
        var password = SubsonicAuth.GeneratePassword();
        SubsonicStore.SetSubsonicPassword(user.Id.ToString("N"), password);
        _logger.LogInformation("[Subfin] generated an OpenSubsonic password for {User}", user.Username);
        Response.Headers.CacheControl = "no-store";
        return Ok(new GeneratedPassword(user.Username, password));
    }

    [HttpDelete("users/{userId:guid}/password")]
    public IActionResult RemovePassword(Guid userId)
    {
        if (_userManager.GetUserById(userId) is not { } user) return NotFound();
        SubsonicStore.DeleteSubsonicPassword(user.Id.ToString("N"));
        _logger.LogInformation("[Subfin] removed the OpenSubsonic password of {User}", user.Username);
        return NoContent();
    }

    private static string ToIso(string sqliteUtc) =>
        DateTime.Parse(sqliteUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)
            .ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
}

public record SubsonicUserInfo(string Id, string Name, bool IsAdministrator, bool IsDisabled, string? PasswordCreated);

public record GeneratedPassword(string Username, string Password);
