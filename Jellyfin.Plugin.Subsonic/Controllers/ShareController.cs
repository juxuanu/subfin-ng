using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Subsonic.Auth;
using Jellyfin.Plugin.Subsonic.Mappers;
using Jellyfin.Plugin.Subsonic.Store;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Subsonic.Controllers;

/// <summary>
/// Public pages for share links (the url of the Subsonic share API): a player, an M3U playlist and a
/// ZIP download. The link's secret signs in as the sharing user, limited to the shared songs.
/// </summary>
[ApiController]
[Route("opensubsonic/share")]
public partial class ShareController : ControllerBase
{
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _library;
    private readonly INetworkManager _network;
    private readonly IApplicationPaths _paths;

    public ShareController(IUserManager userManager, ILibraryManager library, INetworkManager network, IApplicationPaths paths)
    {
        _userManager = userManager;
        _library = library;
        _network = network;
        _paths = paths;
    }

    [HttpGet("{uid}")]
    public IActionResult SharePage(string uid)
    {
        if (!TryOpen(uid, out var share, out var owner, out var secret, out var error)) return error!;
        SubsonicStore.IncrementShareVisitCount(uid);

        var tracks = Songs(share, owner).Select(audio => new
        {
            title = audio.Name ?? "",
            artist = audio.AlbumArtists.FirstOrDefault() ?? audio.Artists.FirstOrDefault() ?? "",
            album = audio.Album ?? "",
            duration = ItemMapper.TicksToSeconds(audio.RunTimeTicks),
            streamUrl = StreamUrl(uid, secret, audio),
        }).ToList();

        var html = GetEmbeddedHtml("share.html")
            .Replace("{{BASE_URL}}", System.Net.WebUtility.HtmlEncode(Request.PathBase.Value ?? ""))
            .Replace("{{SHARE_UID}}", Uri.EscapeDataString(uid))
            .Replace("{{SECRET}}", Uri.EscapeDataString(secret))
            .Replace("{{TRACKS_JSON}}", JsonSerializer.Serialize(tracks))  // escapes <, > and & for the script block
            .Replace("{{DESCRIPTION}}", System.Net.WebUtility.HtmlEncode(share.Description ?? "Shared Music"));
        if (!owner.HasPermission(PermissionKind.EnableContentDownloading))
            html = ZipSection().Replace(html, "");
        return Content(html, "text/html; charset=utf-8");
    }

    [GeneratedRegex("<!--zip-->.*?<!--/zip-->", RegexOptions.Singleline)]
    private static partial Regex ZipSection();

    [HttpGet("{uid}/m3u")]
    public IActionResult ShareM3U(string uid)
    {
        if (!TryOpen(uid, out var share, out var owner, out var secret, out var error)) return error!;
        SubsonicStore.IncrementShareVisitCount(uid);

        var sb = new StringBuilder("#EXTM3U\n");
        foreach (var audio in Songs(share, owner))
        {
            sb.Append($"#EXTINF:{ItemMapper.TicksToSeconds(audio.RunTimeTicks)},{audio.AlbumArtists.FirstOrDefault() ?? ""} - {audio.Name ?? ""}\n");
            sb.Append(StreamUrl(uid, secret, audio)).Append('\n');
        }
        return File(Encoding.UTF8.GetBytes(sb.ToString()), "audio/x-mpegurl", $"share-{uid}.m3u8");
    }

    [HttpGet("{uid}/download")]
    public async Task<IActionResult> ShareZip(string uid)
    {
        if (!TryOpen(uid, out var share, out var owner, out _, out var error)) return error!;
        if (!owner.HasPermission(PermissionKind.EnableContentDownloading))
            return StatusCode(403, "Downloads are not allowed for this share.");

        // Unique names inside the archive
        var used = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var songs = new List<(Audio Audio, string FileName)>();
        foreach (var audio in Songs(share, owner))
        {
            if (string.IsNullOrEmpty(audio.Path) || !System.IO.File.Exists(audio.Path)) continue;
            var name = Path.GetFileName(audio.Path);
            used[name] = used.TryGetValue(name, out var n) ? n + 1 : 0;
            songs.Add((audio, used[name] == 0 ? name : $"{Path.GetFileNameWithoutExtension(name)} ({used[name]}){Path.GetExtension(name)}"));
        }

        // Built in a temporary file: the archive writer needs synchronous writes, which Kestrel refuses
        // on the response, and a whole album shouldn't sit in memory. The file goes when it's sent.
        var zipFile = new FileStream(Path.Combine(_paths.TempDirectory, $"subfin-share-{Guid.NewGuid():N}.zip"), FileMode.CreateNew,
            FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        try
        {
            using (var zip = new ZipArchive(zipFile, ZipArchiveMode.Create, leaveOpen: true))
            {
                var playlistText = new StringBuilder("#EXTM3U\n");
                foreach (var (audio, fileName) in songs)
                {
                    playlistText.Append($"#EXTINF:{ItemMapper.TicksToSeconds(audio.RunTimeTicks)},{audio.AlbumArtists.FirstOrDefault() ?? ""} - {audio.Name ?? ""}\n");
                    playlistText.Append(fileName).Append('\n');

                    using var entryStream = zip.CreateEntry(fileName, CompressionLevel.NoCompression).Open();
                    using var file = System.IO.File.OpenRead(audio.Path);
                    await file.CopyToAsync(entryStream, HttpContext.RequestAborted);
                }

                using var playlist = new StreamWriter(zip.CreateEntry("playlist.m3u8").Open(), new UTF8Encoding(false));
                await playlist.WriteAsync(playlistText.ToString());
            }
            zipFile.Position = 0;
            return File(zipFile, "application/zip", $"share-{uid}.zip");
        }
        catch
        {
            await zipFile.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Opens a share from its link. Unknown shares and wrong secrets look the same (404); an expired
    /// share is 410, and a sharing user Jellyfin wouldn't let in right now is 403.
    /// </summary>
    private bool TryOpen(string uid, out ShareRecord share, out User owner, out string secret, out IActionResult? error)
    {
        share = null!;
        owner = null!;
        secret = Request.Query.First("secret");

        var (status, found) = SubsonicAuth.CheckShare(uid, secret);
        if (status == SubsonicAuth.ShareStatus.Expired)
        {
            error = StatusCode(410, "This share has expired.");
            return false;
        }
        if (status != SubsonicAuth.ShareStatus.Valid || !Guid.TryParse(found!.OwnerUserId, out var ownerId)
            || _userManager.GetUserById(ownerId) is not { } user)
        {
            error = NotFound("Share not found.");
            return false;
        }
        if (AccessRules.Denied(user, HttpContext.Connection.RemoteIpAddress, _network) is not null)
        {
            error = StatusCode(403, "This share is not available right now.");
            return false;
        }
        share = found;
        owner = user;
        error = null;
        return true;
    }

    /// <summary>The shared songs the sharing user can still see.</summary>
    private IEnumerable<Audio> Songs(ShareRecord share, User owner) =>
        share.EntryIdsFlat
            .Select(id => Guid.TryParse(ItemMapper.StripPrefix(id), out var guid) ? _library.GetItemById<Audio>(guid) : null)
            .OfType<Audio>()
            .Where(a => a.IsVisibleStandalone(owner));

    private string StreamUrl(string uid, string secret, Audio audio) =>
        $"{Request.Scheme}://{Request.Host}{Request.PathBase}/opensubsonic/rest/stream.view"
        + $"?id={audio.Id:N}&u=share_{Uri.EscapeDataString(uid)}&p={Uri.EscapeDataString(secret)}&v=1.16.1&c=share";

    private static string GetEmbeddedHtml(string filename)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Jellyfin.Plugin.Subsonic.Web.Views.{filename}")
            ?? throw new FileNotFoundException($"Embedded view {filename} not found");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
