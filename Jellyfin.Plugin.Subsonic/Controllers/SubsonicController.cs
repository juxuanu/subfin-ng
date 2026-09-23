using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Subsonic.Auth;
using Jellyfin.Plugin.Subsonic.Mappers;
using Jellyfin.Plugin.Subsonic.Response;
using Jellyfin.Plugin.Subsonic.Store;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Lyrics;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Security;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Subsonic.Controllers;

/// <summary>
/// Handles all /rest/* Subsonic API endpoints.
/// Returns XML by default; JSON when f=json is in the query.
/// </summary>
[ApiController]
[Route("rest")]
public class SubsonicController : ControllerBase
{
    private readonly SubsonicAuth _auth;
    private readonly ILibraryManager _library;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userData;
    private readonly ISessionManager _sessions;
    private readonly IPlaylistManager _playlists;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMusicManager _musicManager;
    private readonly IAuthenticationManager _authManager;
    private readonly ILyricManager _lyricManager;
    private readonly ILogger<SubsonicController> _logger;
    private static readonly ConcurrentDictionary<string, byte> _refreshInProgress = new();

    // Controllers are created per request, so this is the authenticated user of the current call.
    private User? _currentUser;

    public SubsonicController(
        SubsonicAuth auth,
        ILibraryManager library,
        IUserManager userManager,
        IUserDataManager userData,
        ISessionManager sessions,
        IPlaylistManager playlists,
        IHttpClientFactory httpClientFactory,
        IMusicManager musicManager,
        IAuthenticationManager authManager,
        ILyricManager lyricManager,
        ILogger<SubsonicController> logger)
    {
        _auth = auth;
        _library = library;
        _userManager = userManager;
        _userData = userData;
        _sessions = sessions;
        _playlists = playlists;
        _httpClientFactory = httpClientFactory;
        _musicManager = musicManager;
        _authManager = authManager;
        _lyricManager = lyricManager;
        _logger = logger;
    }

    // ── Entry point ──────────────────────────────────────────────────────────

    [HttpGet("{method}")]
    [HttpGet("{method}.view")]
    [HttpPost("{method}")]
    [HttpPost("{method}.view")]
    public async Task<IActionResult> Handle(string method)
    {
        var q = Request.Query;
        var format = q["f"].ToString().ToLowerInvariant() == "json" ? "json" : "xml";

        var config = SubsonicPlugin.Instance?.Configuration;
        if (config?.LogRestRequests == true)
            _logger.LogInformation("[Subfin] {Method} {Format}", method, format);

        var m = method.ToLowerInvariant().TrimEnd();

        // The only endpoint the spec requires to be public; clients use ping to verify credentials.
        if (m is "getopensubsonicextensions")
            return Respond(format, HandleUnauthenticated(m));

        // Auth
        var authObj = _auth.Resolve(q);
        if (authObj is SubsonicAuth.AuthError err)
        {
            // API-key errors point at the page where keys are listed and revoked
            var helpUrl = err.Code is >= ErrorCode.AuthMechanismNotSupported and <= ErrorCode.InvalidApiKey
                ? $"{Request.Scheme}://{Request.Host}{Request.PathBase}/subfin/"
                : null;
            return Respond(format, SubsonicEnvelope.Error(err.Code, err.Message, helpUrl), XmlBuilder.ErrorEnvelope(err.Code, err.Message, helpUrl));
        }

        var auth = (AuthResult)authObj;

        // Share credentials are embedded in public share pages and act as the sharing user,
        // so they may only fetch the shared tracks (stream/download check the allowlist).
        if (auth.ShareId != null && m is not ("ping" or "getlicense" or "stream" or "download"))
            return ErrorResponse(format, ErrorCode.NotAuthorized, "Share links can only play the shared items.");

        var jellyfinUser = _userManager.GetUserById(Guid.Parse(auth.JellyfinUserId));
        if (jellyfinUser == null)
            return Respond(format, SubsonicEnvelope.Error(ErrorCode.WrongCredentials, "User not found."), XmlBuilder.ErrorEnvelope(ErrorCode.WrongCredentials, "User not found."));

        try
        {
            return await HandleAuthenticated(m, auth, jellyfinUser, q, format);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Subfin] Error handling {Method}", method);
            return Respond(format, SubsonicEnvelope.Error(ErrorCode.Generic, "Internal server error."), XmlBuilder.ErrorEnvelope(ErrorCode.Generic, "Internal server error."));
        }
    }

    // ── Unauthenticated ──────────────────────────────────────────────────────

    private static (JsonObject Json, string Xml) HandleUnauthenticated(string method) => method switch
    {
        "ping" => (SubsonicEnvelope.Ok(), XmlBuilder.Ping()),
        "getlicense" => (SubsonicEnvelope.Ok(new() { ["license"] = new Dictionary<string, object> { ["valid"] = true, ["email"] = "", ["licenseExpires"] = "2099-01-01T00:00:00.000Z" } }), XmlBuilder.License()),
        "getopensubsonicextensions" => (SubsonicEnvelope.Ok(new() { ["openSubsonicExtensions"] = SubsonicConstants.Extensions.Select(e => new Dictionary<string, object> { ["name"] = e.Name, ["versions"] = e.Versions }).ToList() }), XmlBuilder.OpenSubsonicExtensions()),
        _ => (SubsonicEnvelope.Error(ErrorCode.NotFound, "Not found"), XmlBuilder.ErrorEnvelope(ErrorCode.NotFound, "Not found"))
    };

    // ── Authenticated dispatch ───────────────────────────────────────────────

    private async Task<IActionResult> HandleAuthenticated(string method, AuthResult auth, User user, Microsoft.AspNetCore.Http.IQueryCollection q, string format)
    {
        var p = new QueryParams(q);
        _currentUser = user;

        return method switch
        {
            "ping" or "getlicense" => Respond(format, HandleUnauthenticated(method)),
            "getmusicfolders" => GetMusicFolders(auth, user, format),
            "getartists" => GetArtists(auth, user, p, format),
            "getindexes" => GetIndexes(auth, user, p, format),
            "getartist" => GetArtist(auth, user, p, format),
            "getalbum" => GetAlbum(auth, user, p, format),
            "getsong" => GetSong(p, format),
            "getmusicdirectory" => GetMusicDirectory(auth, user, p, format),
            "search3" or "search2" => Search3(auth, user, p, format, search2: method == "search2"),
            "getalbumlist" => GetAlbumList(auth, user, p, format, false),
            "getalbumlist2" => GetAlbumList(auth, user, p, format, true),
            "getrandomsongs" => GetRandomSongs(auth, user, p, format),
            "getgenres" => GetGenres(auth, user, p, format),
            "getsongsbygenre" => GetSongsByGenre(auth, user, p, format),
            "getplaylists" => GetPlaylists(user, format),
            "getplaylist" => GetPlaylist(user, p, format),
            "createplaylist" => await CreatePlaylist(user, p, format),
            "updateplaylist" => await UpdatePlaylist(user, p, format),
            "deleteplaylist" => DeletePlaylist(user, p, format),
            "star" => Star(user, p, format, true),
            "unstar" => Star(user, p, format, false),
            "setrating" => SetRating(user, p, format),
            "scrobble" => await Scrobble(auth, user, p, format),
            "getuser" => GetUser(user, p, format),
            "getusers" => GetUsers(user, format),
            "getscanstatus" => GetScanStatus(format),
            "getnowplaying" => GetNowPlaying(format),
            "saveplayqueue" => SavePlayQueue(auth, p, format),
            "getplayqueue" => GetPlayQueue(auth, format),
            "getshares" or "createshare" or "updateshare" or "deleteshare"
                when SubsonicPlugin.Instance?.Configuration?.SharingEnabled == false
                => ErrorResponse(format, ErrorCode.Generic, "Sharing is disabled."),
            "getshares" => GetShares(auth, user, format),
            "createshare" => CreateShare(auth, user, p, format),
            "updateshare" => UpdateShare(auth, p, format),
            "deleteshare" => DeleteShare(auth, p, format),
            "getstarred" => GetStarred(user, format, false),
            "getstarred2" => GetStarred(user, format, true),
            "getartistinfo" or "getartistinfo2" => await GetArtistInfo(auth, user, p, format, method.EndsWith("2")),
            "getalbuminfo" or "getalbuminfo2" => await GetAlbumInfo(auth, user, p, format, method.EndsWith("2")),
            "getsimilarsongs" or "getsimilarsongs2" => GetSimilarSongs(user, p, format, method.EndsWith("2")),
            "gettopsongs" => GetTopSongs(user, p, format),
            "getlyrics" => await GetLyrics(user, p, format),
            "getlyricsbysongid" => await GetLyricsBySongId(user, p, format),
            "stream" => await Stream(auth, user, p, format),
            "download" => Download(auth, user, p, format),
            "getcoverart" => GetCoverArt(p, format),
            "getavatar" => GetAvatar(user, p, format),
            _ => Respond(format, SubsonicEnvelope.Error(ErrorCode.NotFound, $"Unknown method: {method}"), XmlBuilder.ErrorEnvelope(ErrorCode.NotFound, $"Unknown method: {method}"))
        };
    }

    // ── Artist tag-entity resolution ─────────────────────────────────────────

    /// <summary>
    /// Resolves an artist name to the tag/index entity ID (the one AlbumArtistIds queries match).
    /// Never use album.MusicArtist?.Id — that's the folder-hierarchy entity and won't match.
    /// </summary>
    private string? ResolveArtistTagId(string? name) =>
        string.IsNullOrEmpty(name) ? null :
        _library.GetArtist(name) is MusicArtist a ? $"ar-{a.Id:N}" : null;

    // ── Response helper ──────────────────────────────────────────────────────

    private IActionResult Respond(string format, JsonObject json, string? xml = null)
    {
        if (format == "json")
            return new ContentResult { Content = json.ToJsonString(), ContentType = "application/json; charset=utf-8", StatusCode = 200 };
        return new ContentResult { Content = xml ?? XmlBuilder.ErrorEnvelope(0, "XML not implemented"), ContentType = "text/xml; charset=utf-8", StatusCode = 200 };
    }

    private IActionResult Respond(string format, (JsonObject Json, string Xml) tuple) =>
        Respond(format, tuple.Json, tuple.Xml);

    private IActionResult ErrorResponse(string format, int code, string message) =>
        Respond(format, SubsonicEnvelope.Error(code, message), XmlBuilder.ErrorEnvelope(code, message));

    // ── getMusicFolders ──────────────────────────────────────────────────────

    private IActionResult GetMusicFolders(AuthResult auth, User user, string format)
    {
        var saved = SubsonicStore.GetUserLibrarySettings(auth.SubsonicUsername);
        var musicFolders = MusicFoldersFor(user);

        if (saved.Count > 0)
            musicFolders = musicFolders.Where(f => saved.Contains(f.ItemId)).ToList();

        var json = SubsonicEnvelope.Ok(new()
        {
            ["musicFolders"] = new Dictionary<string, object>
            {
                ["musicFolder"] = musicFolders.Select(f => new Dictionary<string, object> { ["id"] = FolderIdToInt(f.ItemId), ["name"] = f.Item2 }).ToList()
            }
        });
        return Respond(format, json, XmlBuilder.MusicFolders(musicFolders.Select(f => (FolderIdToInt(f.ItemId).ToString(CultureInfo.InvariantCulture), f.Name)).ToList()));  // same int ids as JSON
    }

    // ── getArtists / getIndexes ──────────────────────────────────────────────

    private IActionResult GetArtists(AuthResult auth, User user, QueryParams p, string format)
    {
        var index = BuildArtistIndex(auth, user, p.MusicFolderId);
        var json = SubsonicEnvelope.Ok(new() { ["artists"] = BuildArtistsJson(index) });
        return Respond(format, json, XmlBuilder.Artists(index));
    }

    private IActionResult GetIndexes(AuthResult auth, User user, QueryParams p, string format)
    {
        var index = BuildArtistIndex(auth, user, p.MusicFolderId);
        // Required by the spec. ifModifiedSince isn't supported, so the index is always current.
        var lastModified = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var indexes = BuildArtistsJson(index);
        indexes["lastModified"] = lastModified;
        var json = SubsonicEnvelope.Ok(new() { ["indexes"] = indexes });
        return Respond(format, json, XmlBuilder.Indexes(index, lastModified: lastModified));
    }

    private List<(string Letter, List<(string Id, string Name, int AlbumCount)> Artists)> BuildArtistIndex(
        AuthResult auth, User user, string? musicFolderId)
    {
        var folderIds = GetEffectiveFolderIds(auth, musicFolderId);
        var cacheKey = $"artistIndex:{auth.JellyfinUserId}:{(folderIds == null ? "all" : string.Join(",", folderIds.OrderBy(x => x)))}";
        const long TtlMs = 15L * 60 * 1000;

        var entries = GetOrRefreshCache(
            cacheKey, TtlMs,
            build: () => BuildArtistList(user, folderIds).Select(a => new ArtistCacheEntry(a.Id, a.Name, a.AlbumCount)).ToList(),
            deserialize: json => JsonSerializer.Deserialize<List<ArtistCacheEntry>>(json),
            serialize: v => JsonSerializer.Serialize(v));

        return GroupByLetter(entries.Select(a => (a.Id, a.Name, a.AlbumCount)));
    }

    private record ArtistCacheEntry(string Id, string Name, int AlbumCount);
    private record GenreCacheEntry(string Name, int SongCount, int AlbumCount);

    private static readonly Regex _artistKeyRegex = new(@"[\s._/*'""\-]+", RegexOptions.Compiled);
    private static string CanonicalArtistKey(string name)
        => _artistKeyRegex.Replace(name.ToLowerInvariant(), "");

    private List<(string Id, string Name, int AlbumCount)> BuildArtistList(User user, List<string>? folderIds)
        => LibraryQueries.BuildArtistList(_library, user, folderIds);

    private static List<(string Letter, List<(string Id, string Name, int AlbumCount)> Artists)> GroupByLetter(
        IEnumerable<(string Id, string Name, int AlbumCount)> artists)
    {
        var grouped = new SortedDictionary<string, List<(string, string, int)>>();
        foreach (var a in artists)
        {
            var letter = ItemMapper.IndexLetter(a.Name);
            if (!grouped.ContainsKey(letter)) grouped[letter] = [];
            grouped[letter].Add(a);
        }
        return grouped.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    private static Dictionary<string, object> BuildArtistsJson(List<(string Letter, List<(string Id, string Name, int AlbumCount)> Artists)> index) => new()
    {
        ["ignoredArticles"] = "The An A Die Das Ein Eine Les Le La",
        ["index"] = index.Select(g => new Dictionary<string, object>
        {
            ["name"] = g.Letter,
            ["artist"] = g.Artists.Select(a => new Dictionary<string, object>
            {
                ["id"] = a.Id,
                ["name"] = a.Name,
                ["coverArt"] = $"ar-{a.Id}",
                ["albumCount"] = a.AlbumCount,
            }).ToList()
        }).ToList()
    };

    // ── getArtist ────────────────────────────────────────────────────────────

    private IActionResult GetArtist(AuthResult auth, User user, QueryParams p, string format)
    {
        var id = p.Id;
        if (!TryParseItemId(id, format, out var guid, out var err)) return err!;

        var artist = GetVisibleItem<MusicArtist>(guid);
        if (artist == null) return ErrorResponse(format, ErrorCode.NotFound, "Artist not found");

        var folderIds = GetEffectiveFolderIds(auth, null);

        var query = new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.MusicAlbum],
            AlbumArtistIds = [guid],
            Recursive = true,
        };
        ApplyFolderScoping(query, folderIds);

        var albums = _library.GetItemList(query).OfType<MusicAlbum>().ToList();
        _logger.LogInformation("[Subfin] getArtist {Name} (guid={Guid}): {Count} albums", artist.Name, guid, albums.Count);

        // Fallback 1: if no albums found, re-resolve via GetArtist(name) — the guid we received
        // may be a folder/hierarchy entity (whose ID doesn't work with AlbumArtistIds) rather than
        // the tag/index entity. GetArtist(name) returns the tag entity.
        if (albums.Count == 0 && !string.IsNullOrEmpty(artist.Name))
        {
            var tagEntity = _library.GetArtist(artist.Name);
            if (tagEntity != null && tagEntity.Id != guid)
            {
                _logger.LogInformation("[Subfin] getArtist fallback: tag entity {TagId} differs from passed {Guid}, retrying", tagEntity.Id, guid);
                var fallbackQuery = new InternalItemsQuery(user)
                {
                    IncludeItemTypes = [BaseItemKind.MusicAlbum],
                    AlbumArtistIds = [tagEntity.Id],
                    Recursive = true,
                };
                ApplyFolderScoping(fallbackQuery, folderIds);
                albums = _library.GetItemList(fallbackQuery).OfType<MusicAlbum>().ToList();
            }
        }

        // Fallback 2: artists with special characters (e.g. *NSYNC, AC/DC) may still return 0
        // albums because Jellyfin's AlbumArtistIds lookup matches entity name lowercased against
        // CleanValue, which strips special chars (*nsync ≠ nsync). Fall back to fetching all
        // albums and filtering by AlbumArtist name in C#.
        if (albums.Count == 0 && !string.IsNullOrEmpty(artist.Name))
        {
            _logger.LogInformation("[Subfin] getArtist name-filter fallback for {Name}", artist.Name);
            var allQuery = new InternalItemsQuery(user)
            {
                IncludeItemTypes = [BaseItemKind.MusicAlbum],
                Recursive = true,
            };
            ApplyFolderScoping(allQuery, folderIds);
            albums = _library.GetItemList(allQuery).OfType<MusicAlbum>()
                .Where(a => string.Equals(a.AlbumArtist, artist.Name, StringComparison.OrdinalIgnoreCase)
                         || a.AlbumArtists.Any(n => string.Equals(n, artist.Name, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        var artistId = $"ar-{artist.Id:N}";
        var mapped = ItemMapper.ToArtistWithAlbums(artist, albums, a => ItemMapper.ToAlbumShort(a, artistId, UserDataFor(a), StarredAt(a)));
        var json = SubsonicEnvelope.Ok(new() { ["artist"] = mapped });
        return Respond(format, json, XmlBuilder.Artist(mapped));
    }

    // ── getAlbum ─────────────────────────────────────────────────────────────

    private IActionResult GetAlbum(AuthResult auth, User user, QueryParams p, string format)
    {
        var id = p.Id;
        if (!TryParseItemId(id, format, out var guid, out var err)) return err!;

        var album = GetVisibleItem<MusicAlbum>(guid);
        if (album == null) return ErrorResponse(format, ErrorCode.NotFound, "Album not found");

        var songs = AlbumTracks(user, guid);

        var resolvedArtistId = ResolveArtistTagId(album.AlbumArtist ?? album.AlbumArtists.FirstOrDefault());
        var mapped = ItemMapper.ToAlbum(album, songs, s => ToAlbumSong(s, album), resolvedArtistId, UserDataFor(album));
        var json = SubsonicEnvelope.Ok(new() { ["album"] = mapped });
        return Respond(format, json, XmlBuilder.Album(mapped));
    }

    // ── getSong ──────────────────────────────────────────────────────────────

    private IActionResult GetSong(QueryParams p, string format)
    {
        var id = p.Id;
        if (!TryParseItemId(id, format, out var guid, out var err)) return err!;

        var song = GetVisibleItem<Audio>(guid);
        if (song == null) return ErrorResponse(format, ErrorCode.NotFound, "Song not found");

        var mapped = ToSongWithArtist(song);
        var json = SubsonicEnvelope.Ok(new() { ["song"] = mapped });
        return Respond(format, json, XmlBuilder.Song(mapped));
    }

    // ── getMusicDirectory ────────────────────────────────────────────────────

    private IActionResult GetMusicDirectory(AuthResult auth, User user, QueryParams p, string format)
    {
        var id = p.Id;
        if (!TryParseItemId(id, format, out var guid, out var err)) return err!;

        var item = GetVisibleItem(guid);
        if (item == null) return ErrorResponse(format, ErrorCode.NotFound, "Not found");

        // An album's direct children may be disc folders (Album/CD 1/...), which have no
        // Subsonic representation; list its tracks instead.
        IReadOnlyList<BaseItem> children = item is MusicAlbum
            ? AlbumTracks(user, guid)
            : _library.GetItemList(new InternalItemsQuery(user)
            {
                ParentId = guid,
                OrderBy = [(ItemSortBy.SortName, SortOrder.Ascending)],
            });

        // Fallback for special-char artists (e.g. *NSYNC, AC/DC): getIndexes emits the tag entity GUID
        // (parentless, used by AlbumArtistIds), but getMusicDirectory needs the folder/hierarchy entity
        // GUIDs (the actual parents of albums). For normal artists the same entity serves both roles;
        // for special-char artists Jellyfin normalizes the folder name (e.g. _NSYNC for *NSYNC) and
        // creates a separate tag entity. There may be one folder entity per library.
        if (children.Count == 0 && item is MusicArtist && item.ParentId == Guid.Empty)
        {
            var targetKey = CanonicalArtistKey(item.Name ?? "");
            var folderQuery = new InternalItemsQuery(user)
            {
                IncludeItemTypes = [BaseItemKind.MusicArtist],
                Recursive = true,
            };
            ApplyFolderScoping(folderQuery, GetEffectiveFolderIds(auth, null));
            var folderEntities = _library.GetItemList(folderQuery)
              .OfType<MusicArtist>()
              .Where(a => a.ParentId != Guid.Empty && CanonicalArtistKey(a.Name ?? "") == targetKey)
              .ToList();

            if (folderEntities.Count > 0)
            {
                _logger.LogInformation("[Subfin] getMusicDirectory fallback: tag entity {TagName} → {Count} folder entity/entities",
                    item.Name, folderEntities.Count);
                children = folderEntities
                    .SelectMany(fe => _library.GetItemList(new InternalItemsQuery(user)
                    {
                        ParentId = fe.Id,
                        OrderBy = [(ItemSortBy.SortName, SortOrder.Ascending)],
                    }))
                    .DistinctBy(c => c.Id)
                    .OrderBy(c => c.SortName)
                    .ToList();
            }
        }

        var childMaps = children.Select(c => c switch
        {
            MusicAlbum album => ToAlbumWithArtist(album),
            Audio song => ToSongWithArtist(song),
            _ => null
        }).Where(c => c != null).Cast<Dictionary<string, object?>>().ToList();

        var parentId = item.ParentId == Guid.Empty ? null : item.ParentId.ToString("N");
        var json = SubsonicEnvelope.Ok(new()
        {
            ["directory"] = new Dictionary<string, object>
            {
                ["id"] = guid.ToString("N"),
                ["name"] = item.Name ?? "",
                ["parent"] = (object?)parentId ?? "",
                ["child"] = childMaps,
            }
        });
        return Respond(format, json, XmlBuilder.MusicDirectory(guid.ToString("N"), item.Name ?? "", parentId, childMaps));
    }

    // ── search3 ──────────────────────────────────────────────────────────────

    private IActionResult Search3(AuthResult auth, User user, QueryParams p, string format, bool search2 = false)
    {
        var query = p.Get("query") ?? "";
        var artistCount = p.GetInt("artistCount", 20);
        var albumCount = p.GetInt("albumCount", 20);
        var songCount = p.GetInt("songCount", 20);
        var artistOffset = p.GetInt("artistOffset", 0);
        var albumOffset = p.GetInt("albumOffset", 0);
        var songOffset = p.GetInt("songOffset", 0);

        // Search the artist index (tag entities, file-tag names) rather than GetItemList(MusicArtist)
        // which returns folder/hierarchy entities with Jellyfin-normalized names (e.g. "B.I.G_" instead of "B.I.G.").
        var folderIdsForSearch = GetEffectiveFolderIds(auth, null);
        var cacheKeyForSearch = $"artistIndex:{auth.JellyfinUserId}:{(folderIdsForSearch == null ? "all" : string.Join(",", folderIdsForSearch.OrderBy(x => x)))}";
        var cachedForSearch = SubsonicStore.GetDerivedCache(cacheKeyForSearch);
        var artistIndex = cachedForSearch != null
            ? (JsonSerializer.Deserialize<List<ArtistCacheEntry>>(cachedForSearch.ValueJson) ?? [])
                .Select(a => (a.Id, a.Name, a.AlbumCount))
            : BuildArtistList(user, folderIdsForSearch).Select(a => (a.Id, a.Name, a.AlbumCount));

        var lowerQuery = query.ToLowerInvariant();
        var artists = artistIndex
            .Where(a => a.Name.Contains(lowerQuery, StringComparison.OrdinalIgnoreCase))
            .Skip(artistOffset)
            .Take(artistCount)
            .Select(a => new Dictionary<string, object?> { ["id"] = a.Id, ["name"] = a.Name, ["coverArt"] = $"ar-{a.Id}", ["albumCount"] = a.AlbumCount })
            .ToList();

        var albums = _library.GetItemList(new InternalItemsQuery(user)
        {
            SearchTerm = query,
            IncludeItemTypes = [BaseItemKind.MusicAlbum],
            Limit = albumCount,
            StartIndex = albumOffset,
            Recursive = true,
        }).OfType<MusicAlbum>().Select(ToAlbumWithArtist).ToList();

        var songs = _library.GetItemList(new InternalItemsQuery(user)
        {
            SearchTerm = query,
            IncludeItemTypes = [BaseItemKind.Audio],
            Limit = songCount,
            StartIndex = songOffset,
            Recursive = true,
        }).OfType<Audio>().Select(ToSongWithArtist).ToList();

        var element = search2 ? "searchResult2" : "searchResult3";
        var json = SubsonicEnvelope.Ok(new()
        {
            [element] = new Dictionary<string, object>
            {
                ["artist"] = artists,
                ["album"] = albums,
                ["song"] = songs,
            }
        });
        return Respond(format, json, XmlBuilder.SearchResult3(artists, albums, songs, element));
    }

    // ── getAlbumList / getAlbumList2 ─────────────────────────────────────────

    private IActionResult GetAlbumList(AuthResult auth, User user, QueryParams p, string format, bool v2)
    {
        var type = p.Get("type") ?? "alphabeticalByName";
        var size = Math.Min(p.GetInt("size", 10), 500);
        var offset = p.GetInt("offset", 0);

        if (type == "recent")
        {
            var recentFolderIds = GetEffectiveFolderIds(auth, p.MusicFolderId);
            var folderSuffix = recentFolderIds == null ? "all" : string.Join(",", recentFolderIds.OrderBy(x => x));
            var cacheKey = $"albumListRecent:{auth.JellyfinUserId}:{folderSuffix}:{size}:{offset}";
            const long RecentTtlMs = 5L * 60 * 1000;

            var albumGuids = GetOrRefreshCache(
                cacheKey, RecentTtlMs,
                build: () => BuildRecentAlbumIds(user, recentFolderIds, offset, size),
                deserialize: json => JsonSerializer.Deserialize<List<string>>(json),
                serialize: v => JsonSerializer.Serialize(v));

            var recentAlbums = albumGuids
                .Select(id => GetVisibleItem<MusicAlbum>(Guid.ParseExact(id, "N")))
                .Where(a => a != null)
                .Cast<MusicAlbum>()
                .Select(ToAlbumWithArtist)
                .ToList();

            var recentJson = SubsonicEnvelope.Ok(new() { [v2 ? "albumList2" : "albumList"] = new Dictionary<string, object> { ["album"] = recentAlbums } });
            return Respond(format, recentJson, XmlBuilder.AlbumList(recentAlbums, v2));
        }

        var (sortBy, sortOrder) = type switch
        {
            "newest" => (ItemSortBy.DateCreated, SortOrder.Descending),
            "alphabeticalByName" => (ItemSortBy.SortName, SortOrder.Ascending),
            "alphabeticalByArtist" => (ItemSortBy.AlbumArtist, SortOrder.Ascending),
            "random" => (ItemSortBy.Random, SortOrder.Ascending),
            "highest" => (ItemSortBy.CommunityRating, SortOrder.Descending),
            "frequent" => (ItemSortBy.PlayCount, SortOrder.Descending),
            _ => (ItemSortBy.SortName, SortOrder.Ascending),
        };

        var query = new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.MusicAlbum],
            OrderBy = [(sortBy, sortOrder)],
            Limit = size,
            StartIndex = offset,
            Recursive = true,
        };

        if (type == "starred") { query.IsFavorite = true; }
        if (type == "byYear")
        {
            var fromYear = p.GetInt("fromYear", 0);
            var toYear = p.GetInt("toYear", DateTime.UtcNow.Year);
            query.Years = YearRange(fromYear, toYear);
            // fromYear > toYear asks for reverse chronological order
            query.OrderBy = [(ItemSortBy.ProductionYear, fromYear > toYear ? SortOrder.Descending : SortOrder.Ascending)];
        }
        if (type == "byGenre")
        {
            query.Genres = new List<string> { p.Get("genre") ?? "" };
        }

        var folderIds = GetEffectiveFolderIds(auth, p.MusicFolderId);
        ApplyFolderScoping(query, folderIds);

        var albums = _library.GetItemList(query).OfType<MusicAlbum>().Select(ToAlbumWithArtist).ToList();
        var json = SubsonicEnvelope.Ok(new() { [v2 ? "albumList2" : "albumList"] = new Dictionary<string, object> { ["album"] = albums } });
        return Respond(format, json, XmlBuilder.AlbumList(albums, v2));
    }

    // ── getRandomSongs ───────────────────────────────────────────────────────

    private IActionResult GetRandomSongs(AuthResult auth, User user, QueryParams p, string format)
    {
        var size = Math.Min(p.GetInt("size", 10), 500);
        var query = new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.Audio],
            OrderBy = [(ItemSortBy.Random, SortOrder.Ascending)],
            Limit = size,
            Recursive = true,
        };
        if (p.Get("genre") is { } genre) query.Genres = new List<string> { genre };
        if (p.Get("fromYear") != null || p.Get("toYear") != null)
            query.Years = YearRange(p.GetInt("fromYear", 0), p.GetInt("toYear", DateTime.UtcNow.Year));
        ApplyFolderScoping(query, GetEffectiveFolderIds(auth, p.MusicFolderId));
        var songs = _library.GetItemList(query).OfType<Audio>().Select(ToSongWithArtist).ToList();

        var json = SubsonicEnvelope.Ok(new() { ["randomSongs"] = new Dictionary<string, object> { ["song"] = songs } });
        return Respond(format, json, XmlBuilder.RandomSongs(songs));
    }

    // ── getGenres ────────────────────────────────────────────────────────────

    private IActionResult GetGenres(AuthResult auth, User user, QueryParams p, string format)
    {
        var folderIds = GetEffectiveFolderIds(auth, p.MusicFolderId);
        var folderSuffix = folderIds == null ? "all" : string.Join(",", folderIds.OrderBy(x => x));
        var cacheKey = $"genres:{auth.JellyfinUserId}:{folderSuffix}";
        const long TtlMs = 30L * 60 * 1000;

        var cached = GetOrRefreshCache(
            cacheKey, TtlMs,
            build: () =>
            {
                // Use GetMusicGenres (MusicGenre entity population), NOT GetGenres —
                // GetGenres returns the general Genre population dominated by movie/TV
                // genres, which no audio track carries (see github issue #1).
                var genreQuery = new InternalItemsQuery(user)
                {
                    IncludeItemTypes = [BaseItemKind.Audio, BaseItemKind.MusicAlbum],
                    Recursive = true,
                };
                ApplyFolderScoping(genreQuery, folderIds);
                var genreResult = _library.GetMusicGenres(genreQuery);
                return genreResult.Items.Select(g =>
                {
                    var name = g.Item1.Name ?? "";
                    var songQuery = new InternalItemsQuery(user)
                    {
                        Genres = new List<string> { name },
                        IncludeItemTypes = [BaseItemKind.Audio],
                        Recursive = true,
                    };
                    ApplyFolderScoping(songQuery, folderIds);
                    var songCount = _library.GetCount(songQuery);
                    var albumQuery = new InternalItemsQuery(user)
                    {
                        Genres = new List<string> { name },
                        IncludeItemTypes = [BaseItemKind.MusicAlbum],
                        Recursive = true,
                    };
                    ApplyFolderScoping(albumQuery, folderIds);
                    var albumCount = _library.GetCount(albumQuery);
                    return new GenreCacheEntry(name, songCount, albumCount);
                }).ToList();
            },
            deserialize: json => JsonSerializer.Deserialize<List<GenreCacheEntry>>(json),
            serialize: v => JsonSerializer.Serialize(v));

        var genres = cached.Select(g => (g.Name, g.SongCount, g.AlbumCount)).ToList();

        var jsonObj = SubsonicEnvelope.Ok(new()
        {
            ["genres"] = new Dictionary<string, object>
            {
                ["genre"] = genres.Select(g => new Dictionary<string, object> { ["value"] = g.Name, ["songCount"] = g.SongCount, ["albumCount"] = g.AlbumCount }).ToList()
            }
        });
        return Respond(format, jsonObj, XmlBuilder.Genres(genres));
    }

    // ── getSongsByGenre ──────────────────────────────────────────────────────

    private IActionResult GetSongsByGenre(AuthResult auth, User user, QueryParams p, string format)
    {
        var genre = p.Get("genre") ?? "";
        var count = Math.Min(p.GetInt("count", 10), 500);
        var offset = p.GetInt("offset", 0);

        var query = new InternalItemsQuery(user)
        {
            Genres = new List<string> { genre },
            IncludeItemTypes = [BaseItemKind.Audio],
            Limit = count,
            StartIndex = offset,
            Recursive = true,
        };
        ApplyFolderScoping(query, GetEffectiveFolderIds(auth, p.MusicFolderId));
        var songs = _library.GetItemList(query).OfType<Audio>().Select(ToSongWithArtist).ToList();

        var json = SubsonicEnvelope.Ok(new() { ["songsByGenre"] = new Dictionary<string, object> { ["song"] = songs } });
        return Respond(format, json, XmlBuilder.SongsByGenre(songs));
    }

    // ── Playlists ────────────────────────────────────────────────────────────
    // Jellyfin's rules: a playlist is visible to its owner, to users it is shared with and, when
    // public, to everyone; its owner and shares with CanEdit may edit it; only the owner may delete
    // it or change whether it is public. Songs the current user may not access are hidden.

    private IActionResult GetPlaylists(User user, string format)
    {
        var playlists = _playlists.GetPlaylists(user.Id)
            .Select(pl => MapPlaylist(pl, user, false)).ToList();

        var json = SubsonicEnvelope.Ok(new() { ["playlists"] = new Dictionary<string, object> { ["playlist"] = playlists } });
        return Respond(format, json, XmlBuilder.Playlists(playlists));
    }

    private IActionResult GetPlaylist(User user, QueryParams p, string format)
    {
        if (!TryParseItemId(p.Id, format, out var guid, out var err)) return err!;

        var pl = GetVisiblePlaylist(user, guid);
        if (pl == null) return ErrorResponse(format, ErrorCode.NotFound, "Playlist not found");

        var mapped = MapPlaylist(pl, user, true);
        var json = SubsonicEnvelope.Ok(new() { ["playlist"] = mapped });
        return Respond(format, json, XmlBuilder.Playlist(mapped));
    }

    private Playlist? GetVisiblePlaylist(User user, Guid id) =>
        id != Guid.Empty && _library.GetItemById<Playlist>(id) is { } pl && pl.IsVisible(user) ? pl : null;

    private static bool CanEditPlaylist(Playlist pl, User user) =>
        pl.OwnerUserId.Equals(user.Id) || pl.Shares.Any(s => s.UserId.Equals(user.Id) && s.CanEdit);

    /// <summary>
    /// Each entry with its song, or null when the current user may not access it. Entries are
    /// resolved by Jellyfin; ones whose item no longer exists are dropped.
    /// </summary>
    private List<(LinkedChild Child, Audio? Song)> PlaylistEntries(Playlist pl) =>
        pl.GetManageableItems()
            .Select(t => (t.Item1, t.Item2 is Audio a && _currentUser is { } u && a.IsVisibleStandalone(u) ? a : null))
            .ToList();

    /// <summary>Songs for Subsonic ids, in request order (duplicates kept), skipping unknown or inaccessible ones.</summary>
    private List<Audio> VisibleSongs(IEnumerable<string?> ids) =>
        ids.Select(s => Guid.TryParse(ItemMapper.StripPrefix(s ?? ""), out var g) ? GetVisibleItem<Audio>(g) : null)
            .OfType<Audio>().ToList();

    private Dictionary<string, object?> MapPlaylist(Playlist pl, User user, bool includeSongs)
    {
        var changed = pl.DateLastMediaAdded ?? pl.DateCreated;
        var songs = PlaylistEntries(pl).Select(e => e.Song).OfType<Audio>().ToList();
        var owner = pl.OwnerUserId.Equals(user.Id) ? user.Username : _userManager.GetUserById(pl.OwnerUserId)?.Username ?? "";

        return new()
        {
            ["id"] = $"pl-{pl.Id:N}",
            ["name"] = pl.Name ?? "",
            ["comment"] = pl.Overview ?? "",
            ["owner"] = owner,
            ["public"] = pl.OpenAccess,
            ["readonly"] = !CanEditPlaylist(pl, user),
            ["songCount"] = songs.Count,
            ["duration"] = songs.Sum(s => ItemMapper.TicksToSeconds(s.RunTimeTicks)),
            ["created"] = pl.DateCreated.ToString("o"),
            ["changed"] = (changed == default ? pl.DateCreated : changed).ToString("o"),
            ["coverArt"] = $"pl-{pl.Id:N}",
            ["entry"] = includeSongs ? songs.Select(ToSongWithArtist).ToList() : new List<Dictionary<string, object?>>(),
        };
    }

    // ── createPlaylist / updatePlaylist / deletePlaylist ─────────────────────

    private async Task<IActionResult> CreatePlaylist(User user, QueryParams p, string format)
    {
        var songs = VisibleSongs(Request.Query["songId"]);
        Playlist? pl;

        if (p.Get("playlistId") is { } playlistId)
        {
            // With playlistId, createPlaylist replaces the songs of an existing playlist.
            if (!TryParseItemId(playlistId, format, out var guid, out var err)) return err!;
            pl = GetVisiblePlaylist(user, guid);
            if (pl == null) return ErrorResponse(format, ErrorCode.NotFound, "Playlist not found");
            if (!CanEditPlaylist(pl, user)) return ErrorResponse(format, ErrorCode.NotAuthorized, "You may not edit this playlist.");

            // Entries this user can't see weren't sent by the client; keep them.
            var hidden = PlaylistEntries(pl).Where(e => e.Song == null).Select(e => e.Child);
            await SavePlaylistAsync(pl, [.. songs.Select(LinkedChild.Create), .. hidden], added: songs.Count > 0);
        }
        else
        {
            var name = p.Get("name");
            if (string.IsNullOrWhiteSpace(name))
                return ErrorResponse(format, ErrorCode.RequiredParameterMissing, "Required parameter 'name' missing.");

            var result = await _playlists.CreatePlaylist(new MediaBrowser.Model.Playlists.PlaylistCreationRequest
            {
                Name = name,
                ItemIdList = [],
                UserId = user.Id,
                MediaType = MediaType.Audio,
            });
            pl = _library.GetItemById<Playlist>(Guid.Parse(result.Id));
            if (pl == null) return ErrorResponse(format, ErrorCode.Generic, "Failed to create playlist");
            if (songs.Count > 0)
                await SavePlaylistAsync(pl, [.. songs.Select(LinkedChild.Create)], added: true);
        }

        var mapped = MapPlaylist(pl, user, true);
        var json = SubsonicEnvelope.Ok(new() { ["playlist"] = mapped });
        return Respond(format, json, XmlBuilder.Playlist(mapped));
    }

    private async Task<IActionResult> UpdatePlaylist(User user, QueryParams p, string format)
    {
        if (!TryParseItemId(p.Id, format, out var guid, out var err)) return err!;

        var pl = GetVisiblePlaylist(user, guid);
        if (pl == null) return ErrorResponse(format, ErrorCode.NotFound, "Playlist not found");
        if (!CanEditPlaylist(pl, user)) return ErrorResponse(format, ErrorCode.NotAuthorized, "You may not edit this playlist.");

        if (p.Get("public") is { } isPublic)
        {
            if (!pl.OwnerUserId.Equals(user.Id))
                return ErrorResponse(format, ErrorCode.NotAuthorized, "Only the owner can change whether a playlist is public.");
            pl.OpenAccess = string.Equals(isPublic, "true", StringComparison.OrdinalIgnoreCase);
        }
        if (p.Get("name") is { } name) pl.Name = name;
        if (Request.Query.ContainsKey("comment")) pl.Overview = p.Get("comment");

        // songIndexToRemove indexes the entries this user sees (as returned by getPlaylist);
        // entries hidden from them are left alone.
        var toRemove = Request.Query["songIndexToRemove"]
            .Select(s => int.TryParse(s, out var i) ? i : -1)
            .ToHashSet();
        var kept = new List<LinkedChild>();
        var visibleIndex = 0;
        foreach (var (child, song) in PlaylistEntries(pl))
        {
            if (song != null && toRemove.Contains(visibleIndex++)) continue;
            kept.Add(child);
        }
        var added = VisibleSongs(Request.Query["songIdToAdd"]);
        kept.AddRange(added.Select(LinkedChild.Create));

        await SavePlaylistAsync(pl, kept, added: added.Count > 0);
        return Respond(format, SubsonicEnvelope.Ok(), XmlBuilder.Ping());
    }

    private IActionResult DeletePlaylist(User user, QueryParams p, string format)
    {
        if (!TryParseItemId(p.Id, format, out var guid, out var err)) return err!;

        var pl = GetVisiblePlaylist(user, guid);
        if (pl == null) return ErrorResponse(format, ErrorCode.NotFound, "Playlist not found");
        if (!pl.OwnerUserId.Equals(user.Id))
            return ErrorResponse(format, ErrorCode.NotAuthorized, "Only the owner can delete a playlist.");

        _library.DeleteItem(pl, new MediaBrowser.Controller.Library.DeleteOptions { DeleteFileLocation = true });
        return Respond(format, SubsonicEnvelope.Ok(), XmlBuilder.Ping());
    }

    /// <summary>
    /// Saves a playlist's name, comment, visibility and, when given, its complete ordered entry list
    /// in one write. IPlaylistManager can't do this: it has no way to set the comment, its
    /// RemoveItemFromPlaylistAsync removes every occurrence of a song rather than one position, and
    /// UpdatePlaylist clears and re-adds the entries in separate writes.
    /// </summary>
    private async Task SavePlaylistAsync(Playlist pl, IReadOnlyList<LinkedChild>? entries = null, bool added = false)
    {
        if (entries != null)
            pl.LinkedChildren = [.. entries];
        if (added)
            pl.DateLastMediaAdded = DateTime.UtcNow;

        await pl.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None);  // also writes playlist.xml
        if (pl.IsFile)
            _playlists.SavePlaylistFile(pl);
    }

    // ── star / unstar ────────────────────────────────────────────────────────

    private IActionResult Star(User user, QueryParams p, string format, bool star)
    {
        var ids = Request.Query["id"].Concat(Request.Query["albumId"]).Concat(Request.Query["artistId"])
            .Select(s => s ?? "").Where(s => !string.IsNullOrEmpty(s)).ToList();

        foreach (var id in ids)
        {
            if (!Guid.TryParse(ItemMapper.StripPrefix(id), out var guid)) continue;
            var item = GetVisibleItem(guid);
            if (item == null) continue;
            var data = _userData.GetUserData(user, item);
            if (data == null) continue;
            data.IsFavorite = star;
            _userData.SaveUserData(user, item, data, UserDataSaveReason.UpdateUserRating, CancellationToken.None);
            SubsonicStore.SetStarred(user.Id.ToString("N"), item.Id.ToString("N"), star);
        }
        return Respond(format, SubsonicEnvelope.Ok(), XmlBuilder.Ping());
    }

    // ── setRating ────────────────────────────────────────────────────────────

    private IActionResult SetRating(User user, QueryParams p, string format)
    {
        var id = p.Id;
        var rating = p.GetInt("rating", 0);
        if (string.IsNullOrEmpty(id)) return ErrorResponse(format, ErrorCode.RequiredParameterMissing, "Missing id");
        if (!Guid.TryParse(ItemMapper.StripPrefix(id), out var guid)) return Respond(format, SubsonicEnvelope.Ok(), XmlBuilder.Ping());

        var item = GetVisibleItem(guid);
        if (item == null) return Respond(format, SubsonicEnvelope.Ok(), XmlBuilder.Ping());

        var data = _userData.GetUserData(user, item);
        if (data == null) return Respond(format, SubsonicEnvelope.Ok(), XmlBuilder.Ping());
        data.Rating = rating > 0 ? rating : null;
        _userData.SaveUserData(user, item, data, UserDataSaveReason.UpdateUserRating, CancellationToken.None);
        return Respond(format, SubsonicEnvelope.Ok(), XmlBuilder.Ping());
    }

    // ── scrobble ─────────────────────────────────────────────────────────────

    private async Task<IActionResult> Scrobble(AuthResult auth, User user, QueryParams p, string format)
    {
        // Several id parameters (each with its own time) submit a batch, e.g. plays queued offline.
        var ids = Request.Query["id"];
        var times = Request.Query["time"];
        var isSubmission = !string.Equals(Request.Query["submission"].ToString(), "false", StringComparison.OrdinalIgnoreCase);

        for (var i = 0; i < ids.Count; i++)
        {
            if (!Guid.TryParse(ItemMapper.StripPrefix(ids[i] ?? ""), out var guid)) continue;
            var item = GetVisibleItem<Audio>(guid);
            if (item == null) continue;

            // "time" is when the song was listened to (ms since epoch), not a playback position.
            DateTime? listenedAt = i < times.Count && long.TryParse(times[i], out var ms)
                ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime
                : null;

            if (isSubmission && listenedAt is { } at && DateTime.UtcNow - at > LateScrobble)
                RecordPastPlay(user, item, at);
            else
                await ReportPlaybackAsync(auth, user, item, isSubmission);
        }

        return Respond(format, SubsonicEnvelope.Ok(), XmlBuilder.Ping());
    }

    // Plays submitted this long after they happened (offline clients) are recorded as history
    // rather than as a live playback session, so they keep their real time.
    private static readonly TimeSpan LateScrobble = TimeSpan.FromMinutes(10);

    /// <summary>Counts a play that happened at <paramref name="at"/>, in a single user-data write.</summary>
    private void RecordPastPlay(User user, Audio item, DateTime at)
    {
        var data = _userData.GetUserData(user, item);
        if (data == null) return;
        data.PlayCount++;
        data.Played = true;
        if (data.LastPlayedDate is not { } last || last < at)
            data.LastPlayedDate = at;
        _userData.SaveUserData(user, item, data, UserDataSaveReason.PlaybackFinished, CancellationToken.None);
        _logger.LogInformation("[Subfin] scrobble: recorded play from {At:o}", at);
    }

    /// <summary>
    /// Live playback through Jellyfin's session manager: "now playing", and when finished Jellyfin
    /// counts the play, stamps it and notifies scrobbler plugins. It is the only writer of that play
    /// (writing user data here as well would race with it and lose one of the updates).
    /// </summary>
    private async Task ReportPlaybackAsync(AuthResult auth, User user, Audio item, bool finished)
    {
        try
        {
            var remoteIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "";
            var clientName = Request.Query["c"].ToString() is { Length: > 0 } cn ? cn : (auth.JellyfinDeviceName ?? "Subfin");
            var clientVersion = Request.Query["v"].ToString() is { Length: > 0 } cv ? cv : "1.0.0";
            var session = await _sessions.LogSessionActivity(
                clientName, clientVersion,
                auth.JellyfinDeviceId ?? "subfin-unknown",
                auth.JellyfinDeviceName ?? "Subfin Device",
                remoteIp, user);

            await _sessions.OnPlaybackStart(new PlaybackStartInfo
            {
                ItemId = item.Id,
                SessionId = session.Id,
                PositionTicks = 0L,
                PlayMethod = PlayMethod.DirectPlay,
                IsPaused = false,
                CanSeek = true
            });

            if (finished)
            {
                // Stopped at the end: played to completion.
                await _sessions.OnPlaybackStopped(new PlaybackStopInfo
                {
                    ItemId = item.Id,
                    SessionId = session.Id,
                    PositionTicks = item.RunTimeTicks,
                    Failed = false
                });
                _logger.LogInformation("[Subfin] scrobble: sent start+stop (submission)");
            }
            else
            {
                _ = _sessions.OnPlaybackProgress(new PlaybackProgressInfo
                {
                    ItemId = item.Id,
                    SessionId = session.Id,
                    PositionTicks = 0L,
                    IsPaused = false
                });
                _logger.LogInformation("[Subfin] scrobble: sent start+progress (now playing)");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Subfin] scrobble: session reporting failed (non-fatal)");
        }
    }

    // ── getNowPlaying ────────────────────────────────────────────────────────

    private IActionResult GetNowPlaying(string format)
    {
        var sessions = _sessions.Sessions.Where(s => s.NowPlayingItem != null).ToList();
        var entries = sessions.Select(s =>
        {
            var item = s.NowPlayingItem;
            if (item == null) return null;
            var itemId = item.Id.ToString("N");
            var song = new Dictionary<string, object?>
            {
                ["id"] = itemId,
                ["title"] = item.Name ?? "",
                ["parent"] = item.ParentId?.ToString("N") ?? "",
                ["isDir"] = false,
                ["isVideo"] = false,
                ["type"] = "music",
                ["mediaType"] = "song",
                ["duration"] = ItemMapper.TicksToSeconds(item.RunTimeTicks),
                ["artist"] = item.Artists?.FirstOrDefault() ?? "",
                ["album"] = item.Album ?? "",
                ["coverArt"] = itemId,
            };
            var minutesAgo = (int)(DateTime.UtcNow - s.LastActivityDate).TotalMinutes;
            return new NowPlayingXml(song, s.UserName ?? "", minutesAgo, s.Id, s.DeviceName ?? "");
        }).Where(e => e != null).Cast<NowPlayingXml>().ToList();

        var json = SubsonicEnvelope.Ok(new()
        {
            ["nowPlaying"] = new Dictionary<string, object>
            {
                ["entry"] = entries.Select(e => e.Song).ToList()
            }
        });
        return Respond(format, json, XmlBuilder.NowPlaying(entries));
    }

    // ── savePlayQueue / getPlayQueue ─────────────────────────────────────────

    private IActionResult SavePlayQueue(AuthResult auth, QueryParams p, string format)
    {
        var ids = Request.Query["id"].Select(s => s ?? "").Where(s => !string.IsNullOrEmpty(s)).ToList();
        var current = p.Get("current");
        var position = p.GetLong("position", 0);
        SubsonicStore.SavePlayQueue(auth.SubsonicUsername, ids, current, 0, position, "");
        return Respond(format, SubsonicEnvelope.Ok(), XmlBuilder.Ping());
    }

    private IActionResult GetPlayQueue(AuthResult auth, string format)
    {
        var pq = SubsonicStore.GetPlayQueue(auth.SubsonicUsername);
        if (pq == null)
        {
            // playQueue and its username/changed/changedBy are required even when nothing was saved
            var never = DateTimeOffset.UnixEpoch.ToString("o");
            return Respond(format,
                SubsonicEnvelope.Ok(new() { ["playQueue"] = new Dictionary<string, object> { ["username"] = auth.SubsonicUsername, ["changed"] = never, ["changedBy"] = "" } }),
                XmlBuilder.PlayQueue(null, 0, 0, never, "", [], auth.SubsonicUsername));
        }

        var songs = pq.EntryIds.Select(id =>
        {
            if (!Guid.TryParse(ItemMapper.StripPrefix(id), out var guid)) return null;
            var audio = GetVisibleItem<Audio>(guid);
            return audio != null ? ToSongWithArtist(audio) : null;
        }).Where(s => s != null).Cast<Dictionary<string, object?>>().ToList();

        var json = SubsonicEnvelope.Ok(new()
        {
            ["playQueue"] = new Dictionary<string, object>
            {
                ["current"] = pq.CurrentId ?? "",
                ["position"] = pq.PositionMs,
                ["username"] = auth.SubsonicUsername,
                ["changed"] = pq.ChangedAt ?? DateTimeOffset.UnixEpoch.ToString("o"),
                ["changedBy"] = pq.ChangedBy,
                ["entry"] = songs,
            }
        });
        return Respond(format, json, XmlBuilder.PlayQueue(pq.CurrentId, pq.CurrentIndex, pq.PositionMs, pq.ChangedAt, pq.ChangedBy, songs, auth.SubsonicUsername));
    }

    // ── Shares ───────────────────────────────────────────────────────────────

    private IActionResult GetShares(AuthResult auth, User user, string format)
    {
        var shares = SubsonicStore.GetSharesForUser(auth.SubsonicUsername);
        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        var xmlShares = shares.Select(s => BuildShareXml(s, baseUrl, user)).ToList();
        var json = SubsonicEnvelope.Ok(new() { ["shares"] = new Dictionary<string, object> { ["share"] = xmlShares.Select(ShareToJson).ToList() } });
        return Respond(format, json, XmlBuilder.Shares(xmlShares));
    }

    private IActionResult CreateShare(AuthResult auth, User user, QueryParams p, string format)
    {
        var ids = Request.Query["id"].Select(s => s ?? "").Where(s => !string.IsNullOrEmpty(s)).ToList();
        var desc = p.Get("description");
        var expiresParam = p.Get("expires");
        string? expiresAt = null;
        if (!string.IsNullOrEmpty(expiresParam) && long.TryParse(expiresParam, out var ms) && ms > 0)
            expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(ms).ToString("o");

        // The share belongs to the device making the request (it goes away when that device is unlinked).
        var devices = SubsonicStore.GetDevicesForUser(auth.SubsonicUsername);
        var device = devices.FirstOrDefault(d => $"subfin-{d.Id}" == auth.JellyfinDeviceId) ?? devices.FirstOrDefault();
        if (device == null) return ErrorResponse(format, ErrorCode.Generic, "No linked device found. Link a device first.");

        // Expand IDs to a flat list of audio track GUIDs the user may access.
        // Artist IDs are bare GUIDs; album IDs use al- prefix; playlist IDs use pl- prefix.
        var flatIds = LibraryQueries.ExpandShareIds(_library, user, ids);
        if (flatIds.Count == 0) return ErrorResponse(format, ErrorCode.NotFound, "Nothing to share: no accessible songs for the given ids.");

        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
            .Replace("+", "-").Replace("/", "_").Replace("=", "");
        var uid = SubsonicStore.InsertShare(device.Id, ids, flatIds, desc, expiresAt, secret);

        var share = SubsonicStore.GetShare(uid)!;
        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        var xmlShare = BuildShareXml(share, baseUrl, user);
        var json = SubsonicEnvelope.Ok(new() { ["shares"] = new Dictionary<string, object> { ["share"] = new[] { ShareToJson(xmlShare) } } });
        return Respond(format, json, XmlBuilder.ShareCreated(xmlShare));
    }

    /// <summary>Only the user who created a share may change or delete it.</summary>
    private static bool OwnsShare(AuthResult auth, string shareUid) =>
        SubsonicStore.GetSharesForUser(auth.SubsonicUsername).Any(s => s.ShareUid == shareUid);

    private IActionResult UpdateShare(AuthResult auth, QueryParams p, string format)
    {
        var id = p.Id;
        if (string.IsNullOrEmpty(id)) return ErrorResponse(format, ErrorCode.RequiredParameterMissing, "Missing id");
        if (!OwnsShare(auth, id)) return ErrorResponse(format, ErrorCode.NotFound, "Share not found");
        var desc = p.Get("description");
        var expiresParam = p.Get("expires");
        string? expiresAt = null;
        if (!string.IsNullOrEmpty(expiresParam) && long.TryParse(expiresParam, out var ms) && ms > 0)
            expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(ms).ToString("o");
        SubsonicStore.UpdateShare(id, desc, expiresAt);
        return Respond(format, SubsonicEnvelope.Ok(), XmlBuilder.Ping());
    }

    private IActionResult DeleteShare(AuthResult auth, QueryParams p, string format)
    {
        var id = p.Id;
        if (string.IsNullOrEmpty(id)) return ErrorResponse(format, ErrorCode.RequiredParameterMissing, "Missing id");
        if (!OwnsShare(auth, id)) return ErrorResponse(format, ErrorCode.NotFound, "Share not found");
        SubsonicStore.DeleteShare(id);
        return Respond(format, SubsonicEnvelope.Ok(), XmlBuilder.Ping());
    }

    private ShareXml BuildShareXml(ShareRecord s, string baseUrl, User user)
    {
        var secret = SubsonicStore.GetShareSecret(s.ShareUid) ?? "";
        var url = $"{baseUrl}/subfin/share/{s.ShareUid}?secret={Uri.EscapeDataString(secret)}";
        var createdDt = DateTime.Parse(s.CreatedAt, CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);
        var created = createdDt.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var expires = s.ExpiresAt != null
            ? ToShareDateTime(s.ExpiresAt)
            : createdDt.AddYears(1).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var songs = s.EntryIdsFlat.Select(id =>
        {
            if (!Guid.TryParse(ItemMapper.StripPrefix(id), out var guid)) return null;
            var audio = GetVisibleItem<Audio>(guid);
            return audio != null ? ToSongWithArtist(audio) : null;
        }).Where(x => x != null).Cast<Dictionary<string, object?>>().ToList();

        return new ShareXml(s.ShareUid, url, s.Description, user.Username, created, expires, s.VisitCount, songs);
    }

    private static string ToShareDateTime(string sqliteOrIsoDateTime) =>
        DateTime.Parse(sqliteOrIsoDateTime, CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal)
            .ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private static Dictionary<string, object> ShareToJson(ShareXml s) => new()
    {
        ["id"] = s.Id, ["url"] = s.Url, ["description"] = s.Description ?? "",
        ["username"] = s.Username, ["created"] = s.Created, ["expires"] = s.Expires,
        ["visitCount"] = s.VisitCount, ["songCount"] = s.Songs.Count,
        ["entry"] = s.Songs,
    };

    // ── getStarred / getStarred2 ─────────────────────────────────────────────

    private IActionResult GetStarred(User user, string format, bool v2)
    {
        var artists = _library.GetItemList(new InternalItemsQuery(user)
        { IncludeItemTypes = [BaseItemKind.MusicArtist], IsFavorite = true, Recursive = true })
            .OfType<MusicArtist>().Select(a => new Dictionary<string, object?> { ["id"] = a.Id.ToString("N"), ["name"] = a.Name ?? "", ["starred"] = StarredAt(a) ?? a.DateCreated.ToString("o") }).ToList();
        var albums = _library.GetItemList(new InternalItemsQuery(user)
        { IncludeItemTypes = [BaseItemKind.MusicAlbum], IsFavorite = true, Recursive = true })
            .OfType<MusicAlbum>().Select(ToAlbumWithArtist).ToList();
        var songs = _library.GetItemList(new InternalItemsQuery(user)
        { IncludeItemTypes = [BaseItemKind.Audio], IsFavorite = true, Recursive = true })
            .OfType<Audio>().Select(ToSongWithArtist).ToList();

        var json = SubsonicEnvelope.Ok(new()
        {
            [v2 ? "starred2" : "starred"] = new Dictionary<string, object> { ["artist"] = artists, ["album"] = albums, ["song"] = songs }
        });
        return Respond(format, json, XmlBuilder.Starred(artists, albums, songs, v2));
    }

    // ── getUser / getUsers ───────────────────────────────────────────────────

    // Roles mirror the user's Jellyfin permissions; only administrators may look at other users.

    private IActionResult GetUser(User user, QueryParams p, string format)
    {
        var name = p.Get("username") ?? user.Username;
        var target = string.Equals(name, user.Username, StringComparison.OrdinalIgnoreCase) ? user : null;
        if (target == null)
        {
            if (!user.HasPermission(PermissionKind.IsAdministrator))
                return ErrorResponse(format, ErrorCode.NotAuthorized, "Only administrators can view other users.");
            target = _userManager.GetUserByName(name);
            if (target == null) return ErrorResponse(format, ErrorCode.NotFound, "User not found");
        }

        var mapped = MapUser(target);
        return Respond(format, SubsonicEnvelope.Ok(new() { ["user"] = mapped }), XmlBuilder.User(mapped));
    }

    private IActionResult GetUsers(User user, string format)
    {
        if (!user.HasPermission(PermissionKind.IsAdministrator))
            return ErrorResponse(format, ErrorCode.NotAuthorized, "Only administrators can list users.");

        var users = _userManager.GetUsers().Select(MapUser).ToList();
        return Respond(format, SubsonicEnvelope.Ok(new() { ["users"] = new Dictionary<string, object> { ["user"] = users } }), XmlBuilder.Users(users));
    }

    private Dictionary<string, object> MapUser(User u) => new()
    {
        ["username"] = u.Username,
        ["email"] = "",
        ["scrobblingEnabled"] = true,  // scrobbles are recorded as Jellyfin play state
        ["adminRole"] = u.HasPermission(PermissionKind.IsAdministrator),
        ["settingsRole"] = false,
        ["downloadRole"] = u.HasPermission(PermissionKind.EnableContentDownloading),
        ["uploadRole"] = false,
        ["playlistRole"] = true,
        ["coverArtRole"] = false,
        ["commentRole"] = false,
        ["podcastRole"] = false,
        ["streamRole"] = u.HasPermission(PermissionKind.EnableMediaPlayback),
        ["jukeboxRole"] = false,
        ["shareRole"] = SubsonicPlugin.Instance?.Configuration?.SharingEnabled != false,
        ["videoConversionRole"] = false,
        ["folder"] = MusicFoldersFor(u).Select(f => FolderIdToInt(f.ItemId)).ToList(),
    };

    // ── getScanStatus ────────────────────────────────────────────────────────

    private IActionResult GetScanStatus(string format)
    {
        var json = SubsonicEnvelope.Ok(new()
        { ["scanStatus"] = new Dictionary<string, object> { ["scanning"] = false, ["count"] = 0 } });
        return Respond(format, json, XmlBuilder.ScanStatus());
    }

    // ── getArtistInfo / getArtistInfo2 ───────────────────────────────────────

    private async Task<IActionResult> GetArtistInfo(AuthResult auth, User user, QueryParams p, string format, bool v2)
    {
        var id = p.Id;
        if (!TryParseItemId(id, format, out var guid, out var err)) return err!;

        var artist = GetVisibleItem<MusicArtist>(guid);
        if (artist == null) return ErrorResponse(format, ErrorCode.NotFound, "Artist not found");

        var mbid = artist.GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.MusicBrainzArtist);
        var info = await FetchLastFmArtistInfo(mbid, artist.Name);

        var similarArtistDicts = new List<Dictionary<string, object?>>();
        if (info != null)
        {
            foreach (var name in info.SimilarNames)
            {
                var similar = _library.GetArtist(name);
                if (similar == null) continue;
                similarArtistDicts.Add(new Dictionary<string, object?>
                {
                    ["id"] = $"ar-{similar.Id:N}",
                    ["name"] = similar.Name ?? name,
                    ["albumCount"] = 0,
                });
            }
        }

        var bio = info?.Bio;
        var mbidResult = info?.Mbid ?? mbid;
        var url = info?.Url;

        // Build the artist image URL pointing to Jellyfin's image endpoint.
        // Jellyfin stores artist images on the scanned entity; this URL serves it directly.
        var artistImageUrl = $"{Request.Scheme}://{Request.Host}/Items/{artist.Id:N}/Images/Primary";

        var jsonKey = v2 ? "artistInfo2" : "artistInfo";
        var jsonInfo = new Dictionary<string, object>
        {
            ["smallImageUrl"] = artistImageUrl,
            ["mediumImageUrl"] = artistImageUrl,
            ["largeImageUrl"] = artistImageUrl,
            ["similarArtist"] = similarArtistDicts.Select(s => (object)s).ToList(),
        };
        if (bio != null) jsonInfo["biography"] = bio;
        if (mbidResult != null) jsonInfo["musicBrainzId"] = mbidResult;
        if (url != null) jsonInfo["lastFmUrl"] = url;
        var json = SubsonicEnvelope.Ok(new() { [jsonKey] = jsonInfo });
        return Respond(format, json, XmlBuilder.ArtistInfo(bio, mbidResult, url, artistImageUrl, similarArtistDicts, v2));
    }

    // ── getAlbumInfo / getAlbumInfo2 ─────────────────────────────────────────

    private async Task<IActionResult> GetAlbumInfo(AuthResult auth, User user, QueryParams p, string format, bool v2)
    {
        var id = p.Id;
        if (!TryParseItemId(id, format, out var guid, out var err)) return err!;

        var album = GetVisibleItem<MusicAlbum>(guid);
        if (album == null) return ErrorResponse(format, ErrorCode.NotFound, "Album not found");

        var mbid = album.GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.MusicBrainzAlbum);
        var artistName = album.AlbumArtist ?? album.AlbumArtists.FirstOrDefault();
        var info = await FetchLastFmAlbumInfo(artistName, album.Name, mbid);

        var notes = info?.Notes;
        var mbidResult = info?.Mbid ?? mbid;
        var url = info?.Url;

        const string jsonKey = "albumInfo";  // same element for getAlbumInfo and getAlbumInfo2 (unlike artistInfo2)
        var jsonInfo = new Dictionary<string, object>();
        if (notes != null) jsonInfo["notes"] = notes;
        if (mbidResult != null) jsonInfo["musicBrainzId"] = mbidResult;
        if (url != null) jsonInfo["lastFmUrl"] = url;
        var json = SubsonicEnvelope.Ok(new() { [jsonKey] = jsonInfo });
        return Respond(format, json, XmlBuilder.AlbumInfo(notes, mbidResult, url, v2));
    }

    // ── getSimilarSongs / getSimilarSongs2 ───────────────────────────────────

    private IActionResult GetSimilarSongs(User user, QueryParams p, string format, bool v2)
    {
        var id = p.Id;
        if (!TryParseItemId(id, format, out var guid, out var err)) return err!;

        var count = p.GetInt("count", 50);
        var dtoOptions = new DtoOptions();
        IReadOnlyList<BaseItem> results;

        var item = GetVisibleItem(guid);
        if (item == null) return ErrorResponse(format, ErrorCode.NotFound, "Item not found");
        if (item is MusicArtist ma)
            results = _musicManager.GetInstantMixFromArtist(ma, user, dtoOptions);
        else
            results = _musicManager.GetInstantMixFromItem(item, user, dtoOptions);

        var songs = results.Take(count).OfType<Audio>().Select(ToSongWithArtist).ToList();
        var jsonKey = v2 ? "similarSongs2" : "similarSongs";
        var json = SubsonicEnvelope.Ok(new() { [jsonKey] = new Dictionary<string, object> { ["song"] = songs } });
        return Respond(format, json, XmlBuilder.SimilarSongs(songs, v2));
    }

    // ── getTopSongs ──────────────────────────────────────────────────────────

    private IActionResult GetTopSongs(User user, QueryParams p, string format)
    {
        var artistName = p.Get("artist");
        if (string.IsNullOrEmpty(artistName))
            return Respond(format, SubsonicEnvelope.Ok(new() { ["topSongs"] = new Dictionary<string, object> { ["song"] = new List<object>() } }), XmlBuilder.TopSongs([]));

        var count = p.GetInt("count", 50);
        var tagArtist = _library.GetArtist(artistName);
        if (tagArtist == null)
            return Respond(format, SubsonicEnvelope.Ok(new() { ["topSongs"] = new Dictionary<string, object> { ["song"] = new List<object>() } }), XmlBuilder.TopSongs([]));

        var songs = _library.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.Audio],
            AlbumArtistIds = [tagArtist.Id],
            OrderBy = [(ItemSortBy.PlayCount, SortOrder.Descending)],
            Limit = count,
            Recursive = true,
        }).OfType<Audio>().Select(ToSongWithArtist).ToList();

        var json = SubsonicEnvelope.Ok(new() { ["topSongs"] = new Dictionary<string, object> { ["song"] = songs } });
        return Respond(format, json, XmlBuilder.TopSongs(songs));
    }

    // ── Last.fm helpers ──────────────────────────────────────────────────────

    private record LastFmArtistInfo(string? Bio, string? Url, string? Mbid, List<string> SimilarNames);
    private record LastFmAlbumInfo(string? Notes, string? Url, string? Mbid);

    private async Task<LastFmArtistInfo?> FetchLastFmArtistInfo(string? mbid, string? artistName)
    {
        var apiKey = SubsonicPlugin.Instance?.Configuration?.LastFmApiKey;
        if (string.IsNullOrEmpty(apiKey)) return null;

        var cacheKey = !string.IsNullOrEmpty(mbid)
            ? $"lastfm:artist:mbid:{mbid}"
            : $"lastfm:artist:name:{(artistName ?? "").ToLowerInvariant()}";

        var cached = SubsonicStore.GetDerivedCache(cacheKey);
        if (cached != null && (DateTime.UtcNow - DateTime.Parse(cached.CachedAt)).TotalDays < 30)
        {
            try { return JsonSerializer.Deserialize<LastFmArtistInfo>(cached.ValueJson); } catch { }
        }

        try
        {
            var url = !string.IsNullOrEmpty(mbid)
                ? $"https://ws.audioscrobbler.com/2.0/?method=artist.getinfo&mbid={Uri.EscapeDataString(mbid)}&api_key={apiKey}&format=json"
                : $"https://ws.audioscrobbler.com/2.0/?method=artist.getinfo&artist={Uri.EscapeDataString(artistName ?? "")}&api_key={apiKey}&format=json";

            var client = _httpClientFactory.CreateClient();
            var resp = await client.GetStringAsync(url);
            var doc = JsonNode.Parse(resp);
            var a = doc?["artist"];
            if (a == null) return null;

            var bio = a["bio"]?["summary"]?.GetValue<string>();
            var lastFmUrl = a["url"]?.GetValue<string>();
            var artistMbid = a["mbid"]?.GetValue<string>();
            var similar = a["similar"]?["artist"]?.AsArray()
                .Select(n => n?["name"]?.GetValue<string>()).Where(n => n != null).Cast<string>().ToList() ?? [];

            var info = new LastFmArtistInfo(bio, lastFmUrl, artistMbid, similar);
            SubsonicStore.SetDerivedCache(cacheKey, JsonSerializer.Serialize(info), null);
            return info;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Subfin] Last.fm artist lookup failed for {Name}", artistName);
            return null;
        }
    }

    private async Task<LastFmAlbumInfo?> FetchLastFmAlbumInfo(string? artistName, string? albumName, string? mbid)
    {
        var apiKey = SubsonicPlugin.Instance?.Configuration?.LastFmApiKey;
        if (string.IsNullOrEmpty(apiKey)) return null;

        var cacheKey = !string.IsNullOrEmpty(mbid)
            ? $"lastfm:album:mbid:{mbid}"
            : $"lastfm:album:{(artistName ?? "").ToLowerInvariant()}:{(albumName ?? "").ToLowerInvariant()}";

        var cached = SubsonicStore.GetDerivedCache(cacheKey);
        if (cached != null && (DateTime.UtcNow - DateTime.Parse(cached.CachedAt)).TotalDays < 30)
        {
            try { return JsonSerializer.Deserialize<LastFmAlbumInfo>(cached.ValueJson); } catch { }
        }

        try
        {
            var url = !string.IsNullOrEmpty(mbid)
                ? $"https://ws.audioscrobbler.com/2.0/?method=album.getinfo&mbid={Uri.EscapeDataString(mbid)}&api_key={apiKey}&format=json"
                : $"https://ws.audioscrobbler.com/2.0/?method=album.getinfo&artist={Uri.EscapeDataString(artistName ?? "")}&album={Uri.EscapeDataString(albumName ?? "")}&api_key={apiKey}&format=json";

            var client = _httpClientFactory.CreateClient();
            var resp = await client.GetStringAsync(url);
            var doc = JsonNode.Parse(resp);
            var al = doc?["album"];
            if (al == null) return null;

            var notes = al["wiki"]?["summary"]?.GetValue<string>();
            var lastFmUrl = al["url"]?.GetValue<string>();
            var albumMbid = al["mbid"]?.GetValue<string>();

            var info = new LastFmAlbumInfo(notes, lastFmUrl, albumMbid);
            SubsonicStore.SetDerivedCache(cacheKey, JsonSerializer.Serialize(info), null);
            return info;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Subfin] Last.fm album lookup failed for {Name}", albumName);
            return null;
        }
    }

    private async Task<IActionResult> GetLyrics(User user, QueryParams p, string format)
    {
        var artist = p.Get("artist") ?? "";
        var title = p.Get("title") ?? "";
        Audio? song = null;

        // Prefer lookup by id if provided (more reliable than artist/title matching)
        var id = p.Id;
        if (!string.IsNullOrEmpty(id) && Guid.TryParse(ItemMapper.StripPrefix(id), out var guid))
            song = GetVisibleItem<Audio>(guid);

        // Fall back to searching by title
        if (song == null && !string.IsNullOrEmpty(title))
        {
            var results = _library.GetItemList(new InternalItemsQuery(user)
            {
                SearchTerm = title,
                IncludeItemTypes = [BaseItemKind.Audio],
                Limit = 10,
                Recursive = true,
            }).OfType<Audio>();

            // Match artist too if provided
            song = string.IsNullOrEmpty(artist)
                ? results.FirstOrDefault()
                : results.FirstOrDefault(s => s.Artists.Any(a => string.Equals(a, artist, StringComparison.OrdinalIgnoreCase)));
        }

        string lyricsText = "";
        string resolvedArtist = artist;
        string resolvedTitle = title;

        if (song != null)
        {
            resolvedArtist = song.Artists.FirstOrDefault() ?? artist;
            resolvedTitle = song.Name ?? title;
            var dto = await _lyricManager.GetLyricsAsync(song, CancellationToken.None);
            if (dto?.Lyrics != null)
                lyricsText = string.Join("\n", dto.Lyrics.Select(l => l.Text ?? ""));
        }

        var json = SubsonicEnvelope.Ok(new()
        {
            ["lyrics"] = new Dictionary<string, object?>
            {
                ["artist"] = resolvedArtist,
                ["title"] = resolvedTitle,
                ["value"] = lyricsText,
            }
        });

        return Respond(format, json, XmlBuilder.OkEnvelope(w =>
        {
            w.WriteStartElement("lyrics", "http://subsonic.org/restapi");
            w.WriteAttributeString("artist", resolvedArtist);
            w.WriteAttributeString("title", resolvedTitle);
            if (!string.IsNullOrEmpty(lyricsText))
                w.WriteString(lyricsText);
            w.WriteEndElement();
        }));
    }

    private async Task<IActionResult> GetLyricsBySongId(User user, QueryParams p, string format)
    {
        var id = p.Id;
        if (string.IsNullOrEmpty(id)) return ErrorResponse(format, ErrorCode.RequiredParameterMissing, "Missing id");
        if (!TryParseItemId(id, format, out var guid, out var err)) return err!;

        var song = GetVisibleItem<Audio>(guid);
        if (song == null) return ErrorResponse(format, ErrorCode.NotFound, "Song not found");

        var dto = await _lyricManager.GetLyricsAsync(song, CancellationToken.None);

        var structuredLyrics = new List<object>();
        if (dto?.Lyrics != null && dto.Lyrics.Count > 0)
        {
            // Mark synced if metadata says so OR every line has a non-zero timestamp (LRC files
            // don't always set IsSynced). Require ALL lines to have timestamps — Tempus crashes
            // (NPE) on auto-unbox if any synced line is missing start.
            var allHaveTimestamps = dto.Lyrics.All(l => l.Start.HasValue && l.Start.Value > 0);
            var synced = (dto.Metadata?.IsSynced == true || allHaveTimestamps) && dto.Lyrics.All(l => l.Start.HasValue);
            var lang = "und"; // undetermined — Jellyfin doesn't expose language per lyric set
            var displayArtist = song.Artists.FirstOrDefault() ?? "";
            var displayTitle = song.Name ?? "";

            var lines = dto.Lyrics.Select(l =>
            {
                // Always include start (even for unsynced) — Navic's Line.start is non-nullable Int.
                // For unsynced lines or lines without a timestamp, default to 0.
                var startMs = synced && l.Start.HasValue ? (int)(l.Start.Value / 10000L) : 0;
                return (object)new Dictionary<string, object> { ["start"] = startMs, ["value"] = l.Text ?? "" };
            }).ToList();

            structuredLyrics.Add(new Dictionary<string, object>
            {
                ["lang"] = lang,
                ["synced"] = synced,
                ["displayArtist"] = displayArtist,
                ["displayTitle"] = displayTitle,
                ["line"] = lines,
            });
        }

        var json = SubsonicEnvelope.Ok(new()
        {
            ["lyricsList"] = new Dictionary<string, object> { ["structuredLyrics"] = structuredLyrics }
        });

        return Respond(format, json, XmlBuilder.OkEnvelope(w =>
        {
            w.WriteStartElement("lyricsList", "http://subsonic.org/restapi");
            foreach (var entry in structuredLyrics.Cast<Dictionary<string, object>>())
            {
                var isSynced = (bool)entry["synced"];
                var entryLang = (string)entry["lang"];
                w.WriteStartElement("structuredLyrics", "http://subsonic.org/restapi");
                w.WriteAttributeString("lang", entryLang);
                w.WriteAttributeString("synced", isSynced ? "true" : "false");
                w.WriteAttributeString("displayArtist", (string)entry["displayArtist"]);
                w.WriteAttributeString("displayTitle", (string)entry["displayTitle"]);
                foreach (var lineObj in ((List<object>)entry["line"]).Cast<Dictionary<string, object>>())
                {
                    w.WriteStartElement("line", "http://subsonic.org/restapi");
                    w.WriteAttributeString("start", lineObj["start"].ToString());
                    w.WriteAttributeString("value", lineObj["value"]?.ToString() ?? "");
                    w.WriteEndElement();
                }
                w.WriteEndElement();
            }
            w.WriteEndElement();
        }));
    }

    // ── stream / download ────────────────────────────────────────────────────

    private const string PluginApiKeyName = "Subsonic-Plugin-Internal";

    private async Task<string?> GetOrCreatePluginApiKey()
    {
        const string cacheKey = "plugin-api-key";
        const double CacheDays = 30;

        var cached = SubsonicStore.GetDerivedCache(cacheKey);
        if (cached != null)
        {
            var ageDays = (DateTimeOffset.UtcNow - DateTimeOffset.Parse(cached.CachedAt)).TotalDays;
            if (ageDays < CacheDays)
                return cached.ValueJson;
        }

        var keys = await _authManager.GetApiKeys();
        var existing = keys.FirstOrDefault(k => k.AppName == PluginApiKeyName);

        if (existing == null)
        {
            await _authManager.CreateApiKey(PluginApiKeyName);
            keys = await _authManager.GetApiKeys();
            existing = keys.FirstOrDefault(k => k.AppName == PluginApiKeyName);
        }

        if (existing?.AccessToken == null) return null;

        SubsonicStore.SetDerivedCache(cacheKey, existing.AccessToken, null);
        return existing.AccessToken;
    }

    private static (string container, string audioCodec, string mimeType) MapTranscodeFormat(string? format) =>
        (format ?? "mp3") switch
        {
            "mp3"  => ("mp3",  "mp3",    "audio/mpeg"),
            "aac"  => ("aac",  "aac",    "audio/aac"),
            "ogg"  => ("ogg",  "vorbis", "audio/ogg"),
            "opus" => ("webm", "opus",   "audio/webm"),
            "flac" => ("flac", "flac",   "audio/flac"),
            _      => ("mp3",  "mp3",    "audio/mpeg"),
        };

    /// <summary>
    /// The song a binary endpoint (stream/download) may serve, or the Subsonic error to return:
    /// the permission must be granted in Jellyfin, share logins are limited to their items, and
    /// the song must be visible to the user. Errors use the requested format, as the spec asks.
    /// </summary>
    private bool TryGetServableSong(AuthResult auth, User user, QueryParams p, string format, PermissionKind permission,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Audio? song, out IActionResult? error)
    {
        song = null;
        error = null;
        if (!TryParseItemId(p.Id, format, out var guid, out error)) return false;
        if (!user.HasPermission(permission))
            error = ErrorResponse(format, ErrorCode.NotAuthorized, $"Not allowed for this user ({permission}).");
        else if (auth.ShareAllowedIds != null && !auth.ShareAllowedIds.Contains(guid.ToString("N")))
            error = ErrorResponse(format, ErrorCode.NotAuthorized, "Not part of this share.");
        else if ((song = GetVisibleItem<Audio>(guid)) is not { Path: not null })
            error = ErrorResponse(format, ErrorCode.NotFound, "Song not found");
        return error == null;
    }

    private async Task<IActionResult> Stream(AuthResult auth, User user, QueryParams p, string format)
    {
        if (!TryGetServableSong(auth, user, p, format, PermissionKind.EnableMediaPlayback, out var item, out var err)) return err!;
        var id = p.Id;
        var guid = item.Id;

        var targetFormat = p.Format;  // transcode target ("format" is the response format)
        var bitRate = p.MaxBitRate;   // kbps; 0 = unspecified
        var timeOff = p.TimeOffset;   // seconds; 0 = from start

        bool needsTranscode = (targetFormat != null && targetFormat != "raw") || bitRate > 0 || timeOff > 0;

        _logger.LogInformation("[Subfin] stream id={Id} format={Format} bitRate={BitRate} timeOff={TimeOff} needsTranscode={NeedsTranscode}",
            id, targetFormat, bitRate, timeOff, needsTranscode);

        if (!needsTranscode)
            return PhysicalFile(item.Path, ItemMapper.AudioMimeType(item.Container), null, true);

        var apiKey = await GetOrCreatePluginApiKey();
        if (string.IsNullOrEmpty(apiKey))
        {
            _logger.LogWarning("[Subfin] Could not obtain plugin API key — serving direct");
            return PhysicalFile(item.Path, ItemMapper.AudioMimeType(item.Container), null, true);
        }

        var (container, audioCodec, mimeType) = MapTranscodeFormat(targetFormat);
        var qs = $"audioCodec={audioCodec}&static=false" +
                 $"&userId={auth.JellyfinUserId}" +
                 $"&deviceId={Uri.EscapeDataString(auth.JellyfinDeviceId ?? "subfin")}";
        if (bitRate > 0) qs += $"&audioBitRate={bitRate * 1000}";
        if (timeOff > 0) qs += $"&startTimeTicks={(long)timeOff * 10_000_000L}";

        var url = $"{Request.Scheme}://{Request.Host}/Audio/{guid:N}/stream.{container}?{qs}";
        _logger.LogInformation("[Subfin] stream proxy → {Url}", url);
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Authorization", $"MediaBrowser Token=\"{apiKey}\"");

        var rangeHeader = Request.Headers["Range"].ToString();
        if (!string.IsNullOrEmpty(rangeHeader))
            req.Headers.TryAddWithoutValidation("Range", rangeHeader);

        var resp = await _httpClientFactory.CreateClient().SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        Response.RegisterForDispose(resp);
        Response.StatusCode = (int)resp.StatusCode;

        if (resp.Content.Headers.ContentLength.HasValue)
            Response.Headers["Content-Length"] = resp.Content.Headers.ContentLength.Value.ToString();
        if (resp.Content.Headers.ContentRange != null)
            Response.Headers["Content-Range"] = resp.Content.Headers.ContentRange.ToString();

        // Use our known mimeType — Jellyfin may return "video/webm" for audio-only WebM which confuses clients
        return new FileStreamResult(await resp.Content.ReadAsStreamAsync(), mimeType);
    }

    private IActionResult Download(AuthResult auth, User user, QueryParams p, string format)
    {
        if (!TryGetServableSong(auth, user, p, format, PermissionKind.EnableContentDownloading, out var item, out var err)) return err!;

        return PhysicalFile(item.Path, ItemMapper.AudioMimeType(item.Container), Path.GetFileName(item.Path), true);
    }

    // ── getCoverArt / getAvatar ──────────────────────────────────────────────

    private IActionResult GetCoverArt(QueryParams p, string format)
    {
        if (!TryParseItemId(p.Id, format, out var guid, out var err)) return err!;
        if (GetVisibleItem(guid) == null) return ErrorResponse(format, ErrorCode.NotFound, "Cover art not found");

        // Jellyfin's image endpoint does the scaling; "size" bounds the longest edge.
        var size = p.GetInt("size", 0);
        var scale = size > 0 ? $"?maxWidth={size}&maxHeight={size}" : "";
        return Redirect($"{Request.PathBase}/Items/{guid:N}/Images/Primary{scale}");
    }

    private IActionResult GetAvatar(User user, QueryParams p, string format)
    {
        var target = p.Get("username") is { } name && name != user.Username ? _userManager.GetUserByName(name) : user;
        if (target == null) return ErrorResponse(format, ErrorCode.NotFound, "User not found");
        return Redirect($"{Request.PathBase}/Users/{target.Id:N}/Images/Primary");
    }

    // ── Cache helpers ────────────────────────────────────────────────────────

    // Stale-while-revalidate cache helper.
    // fresh hit (ageMs < ttl) → return deserialized value immediately
    // stale hit (ageMs >= ttl) → return stale value + fire background rebuild
    // miss                     → build synchronously, cache, return
    private T GetOrRefreshCache<T>(
        string cacheKey, long ttlMs,
        Func<T> build,
        Func<string, T?> deserialize,
        Func<T, string> serialize)
        where T : class
    {
        var cached = SubsonicStore.GetDerivedCache(cacheKey);
        if (cached != null)
        {
            var ageMs = (DateTimeOffset.UtcNow - DateTimeOffset.Parse(cached.CachedAt)).TotalMilliseconds;
            var value = deserialize(cached.ValueJson);
            if (value != null)
            {
                if (ageMs >= ttlMs)
                    FireBackgroundRefresh(cacheKey, build, serialize);
                return value;
            }
        }
        var built = build();
        SubsonicStore.SetDerivedCache(cacheKey, serialize(built), null);
        return built;
    }

    private void FireBackgroundRefresh<T>(string cacheKey, Func<T> build, Func<T, string> serialize) where T : class
    {
        if (!_refreshInProgress.TryAdd(cacheKey, 0)) return;
        _ = Task.Run(() =>
        {
            try
            {
                var v = build();
                SubsonicStore.SetDerivedCache(cacheKey, serialize(v), null);
                _logger.LogInformation("[Subfin] BG cache refresh done: {Key}", cacheKey);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Subfin] BG cache refresh failed: {Key}", cacheKey);
            }
            finally
            {
                _refreshInProgress.TryRemove(cacheKey, out _);
            }
        });
    }

    // Jellyfin only sets LastPlayedDate on Audio (track) entities, not MusicAlbum.
    // Derive recently-played album order from track play history; returns album GUIDs (N-format).
    private List<string> BuildRecentAlbumIds(User user, List<string>? folderIds, int offset, int size)
    {
        var trackLimit = Math.Max(200, (offset + size) * 10);
        var trackQuery = new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.Audio],
            OrderBy = [(ItemSortBy.DatePlayed, SortOrder.Descending)],
            Limit = trackLimit,
            Recursive = true,
        };
        ApplyFolderScoping(trackQuery, folderIds);

        var seenAlbums = new HashSet<Guid>();
        var albumIds = new List<Guid>();
        foreach (var track in _library.GetItemList(trackQuery).OfType<Audio>())
        {
            if (track.ParentId != Guid.Empty && seenAlbums.Add(track.ParentId))
                albumIds.Add(track.ParentId);
        }
        return albumIds.Skip(offset).Take(size).Select(id => id.ToString("N")).ToList();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    // Converts a Jellyfin music folder GUID string to a stable positive integer for JSON responses.
    // subsonic-kotlin (Navic) types MusicFolder.id as Int, not String.
    internal static int FolderIdToInt(string guidStr) =>
        (int)(BitConverter.ToUInt32(Guid.Parse(guidStr).ToByteArray(), 0) & 0x7FFFFFFF);

    // Returns false and sets error if id is null/empty or fails Guid parse; sets guid on success.
    private bool TryParseItemId(string? id, string format, out Guid guid, out IActionResult? error)
    {
        if (string.IsNullOrEmpty(id))
        {
            guid = default;
            error = ErrorResponse(format, ErrorCode.RequiredParameterMissing, "Missing id");
            return false;
        }
        // Guid.Empty would make ILibraryManager.GetItemById throw
        if (!Guid.TryParse(ItemMapper.StripPrefix(id), out guid) || guid == Guid.Empty)
        {
            error = ErrorResponse(format, ErrorCode.NotFound, "Not found");
            return false;
        }
        error = null;
        return true;
    }

    /// <summary>Inclusive year range; the bounds may come in either order.</summary>
    internal static int[] YearRange(int fromYear, int toYear)
    {
        var (lo, hi) = fromYear <= toYear ? (fromYear, toYear) : (toYear, fromYear);
        return Enumerable.Range(lo, hi - lo + 1).ToArray();
    }

    private static void ApplyFolderScoping(InternalItemsQuery query, List<string>? folderIds)
    {
        if (folderIds != null)
            query.AncestorIds = folderIds.Select(Guid.Parse).ToArray();
    }

    private Dictionary<string, object?> ToSongWithArtist(Audio s) => ToAlbumSong(s, null);

    private Dictionary<string, object?> ToAlbumSong(Audio s, MusicAlbum? album) =>
        ItemMapper.ToSong(s, album?.Id.ToString("N"), album?.Name,
            artistId: ResolveArtistTagId(s.Artists.FirstOrDefault() ?? s.AlbumArtists.FirstOrDefault()),
            userData: UserDataFor(s), starredAt: StarredAt(s), relativePath: RelativePath(s.Path));

    private Dictionary<string, object?> ToAlbumWithArtist(MusicAlbum a) =>
        ItemMapper.ToAlbumShort(a, ResolveArtistTagId(a.AlbumArtist ?? a.AlbumArtists.FirstOrDefault()), UserDataFor(a), StarredAt(a));

    // Per-request: when the current user starred items through Subfin.
    private Dictionary<string, string>? _starredDates;

    private string? StarredAt(BaseItem item)
    {
        if (_currentUser == null) return null;
        _starredDates ??= SubsonicStore.GetStarredDates(_currentUser.Id.ToString("N"));
        return _starredDates.GetValueOrDefault(item.Id.ToString("N"));
    }

    // Per-request: the library folders, for making file paths relative.
    private List<string>? _libraryRoots;

    private string RelativePath(string? path)
    {
        _libraryRoots ??= _library.GetVirtualFolders().SelectMany(f => f.Locations).ToList();
        return ItemMapper.RelativeToLibrary(path, _libraryRoots);
    }

    /// <summary>
    /// An item by id, or null when it doesn't exist or the current user may not see it (library
    /// access, parental rating, tags) — the check Jellyfin's own single-item endpoints apply.
    /// Plain GetItemById ignores all of that.
    /// </summary>
    private T? GetVisibleItem<T>(Guid id) where T : BaseItem =>
        _currentUser is { } u && id != Guid.Empty && _library.GetItemById<T>(id) is { } item && item.IsVisibleStandalone(u) ? item : null;

    private BaseItem? GetVisibleItem(Guid id) => GetVisibleItem<BaseItem>(id);

    private UserItemData? UserDataFor(BaseItem item) =>
        _currentUser is { } u ? _userData.GetUserData(u, item) : null;

    /// <summary>All tracks of an album, including those in disc subfolders (Album/CD 1/...).</summary>
    private List<Audio> AlbumTracks(User user, Guid albumId) =>
        _library.GetItemList(new InternalItemsQuery(user)
        {
            ParentId = albumId,
            Recursive = true,
            IncludeItemTypes = [BaseItemKind.Audio],
            OrderBy = [(ItemSortBy.ParentIndexNumber, SortOrder.Ascending), (ItemSortBy.IndexNumber, SortOrder.Ascending)],
        }).OfType<Audio>().ToList();

    /// <summary>Music libraries the user may access in Jellyfin.</summary>
    private List<(string ItemId, string Name)> MusicFoldersFor(User user)
    {
        var visible = _library.GetUserRootFolder().GetChildren(user, true).Select(f => f.Id).ToHashSet();
        return _library.GetVirtualFolders()
            .Where(f => f.CollectionType == CollectionTypeOptions.music
                        && Guid.TryParse(f.ItemId, out var id) && visible.Contains(id))
            .Select(f => (f.ItemId, f.Name ?? ""))
            .ToList();
    }

    // Scope for a musicFolderId that doesn't name an accessible library: matches nothing.
    private static readonly List<string> NoFolders = [Guid.Empty.ToString("N")];

    private List<string>? GetEffectiveFolderIds(AuthResult auth, string? clientParam)
    {
        if (!string.IsNullOrEmpty(clientParam))
        {
            // Clients like Navic send musicFolderId as the integer id we emit; others send the GUID.
            var accessible = _currentUser is { } u ? MusicFoldersFor(u) : [];
            var match = int.TryParse(clientParam, out var intId)
                ? accessible.FirstOrDefault(f => FolderIdToInt(f.ItemId) == intId)
                : accessible.FirstOrDefault(f => Guid.TryParse(f.ItemId, out var a) && Guid.TryParse(clientParam, out var b) && a == b);
            return match.ItemId != null ? [match.ItemId] : NoFolders;
        }
        var saved = SubsonicStore.GetUserLibrarySettings(auth.SubsonicUsername);
        return saved.Count == 0 ? null : saved;
    }
}

/// <summary>Thin wrapper over IQueryCollection for convenient param extraction.</summary>
public class QueryParams
{
    private readonly Microsoft.AspNetCore.Http.IQueryCollection _q;
    public QueryParams(Microsoft.AspNetCore.Http.IQueryCollection q) => _q = q;
    public string? Id => Get("id") ?? Get("playlistId");
    public string? MusicFolderId => Get("musicFolderId");
    public string? Format => Get("format");
    public int MaxBitRate => GetInt("maxBitRate", 0) > 0 ? GetInt("maxBitRate", 0) : GetInt("bitRate", 0);
    public int TimeOffset => GetInt("timeOffset", 0);
    public string? Get(string key) { var v = _q[key].ToString(); return string.IsNullOrEmpty(v) ? null : v; }
    public int GetInt(string key, int def) => int.TryParse(_q[key], out var v) ? v : def;
    public long GetLong(string key, long def) => long.TryParse(_q[key], out var v) ? v : def;
}
