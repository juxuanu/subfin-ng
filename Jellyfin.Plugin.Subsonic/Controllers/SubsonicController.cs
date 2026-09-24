using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Subsonic.Auth;
using Jellyfin.Plugin.Subsonic.Mappers;
using Jellyfin.Plugin.Subsonic.Response;
using Jellyfin.Plugin.Subsonic.Store;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Lyrics;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Security;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Jellyfin.Plugin.Subsonic.Controllers;

/// <summary>
/// Handles the Subsonic API at /opensubsonic/rest/*, signed in with Jellyfin usernames and passwords.
/// Returns XML by default; JSON when f=json is in the query.
/// </summary>
[ApiController]
[Route("opensubsonic/rest")]
public class SubsonicController(
    SubsonicAuth subsonicAuth,
    ILibraryManager library,
    IUserManager userManager,
    IUserDataManager userData,
    ISessionManager sessions,
    IPlaylistManager playlistManager,
    IHttpClientFactory httpClientFactory,
    IMusicManager musicManager,
    IAuthenticationManager authManager,
    ILyricManager lyricManager,
    IServerApplicationHost appHost,
    INetworkManager network,
    ISimilarItemsManager similarItems,
    ILogger<SubsonicController> logger)
    : ControllerBase
{
    private static readonly ConcurrentDictionary<string, byte> RefreshInProgress = new();

    // How each user's songs were last streamed (the file as is, or transcoded). Subsonic reports
    // playback in separate scrobble requests, and Jellyfin's dashboard shows the play method they give.
    private static readonly ConcurrentDictionary<(Guid User, Guid Item), (PlayMethod Method, DateTime At)> StreamMethods = new();

    // Controllers are created per request, so these are the authenticated user of the current call
    // and its parameters (query string plus, for POST, the form body).
    private User? _currentUser;
    private IQueryCollection _query = QueryCollection.Empty;

    // ── Entry point ──────────────────────────────────────────────────────────

    [HttpGet("{method}")]
    [HttpGet("{method}.view")]
    [HttpPost("{method}")]
    [HttpPost("{method}.view")]
    public async Task<IActionResult> Handle(string method)
    {
        var q = _query = await ReadParametersAsync();
        var format = string.Equals(q.First("f"), "json", StringComparison.OrdinalIgnoreCase) ? "json" : "xml";

        var config = SubsonicPlugin.Instance?.Configuration;
        if (config?.LogRestRequests == true)
            logger.LogInformation("[Subfin] {Method} {Format}", method, format);

        var m = method.ToLowerInvariant().TrimEnd();

        // The only endpoint the spec requires to be public; clients use ping to verify credentials.
        if (m is "getopensubsonicextensions")
            return Respond(format, HandleUnauthenticated(m));

        try
        {
            var remoteIp = HttpContext.Connection.RemoteIpAddress;
            var authObj = await subsonicAuth.ResolveAsync(q, remoteIp);
            if (authObj is SubsonicAuth.AuthError err)
                return ErrorResponse(format, err.Code, err.Message);

            var auth = (AuthResult)authObj;

            // Share credentials are embedded in public share pages and act as the sharing user,
            // so they may only fetch the shared tracks (stream/download check the allowlist).
            if (auth.ShareId != null && m is not ("ping" or "getlicense" or "stream" or "download"))
                return ErrorResponse(format, ErrorCode.NotAuthorized, "Share links can only play the shared items.");

            var jellyfinUser = userManager.GetUserById(auth.UserId);
            if (jellyfinUser == null)
                return ErrorResponse(format, ErrorCode.WrongCredentials, "Wrong username or password.");
            // Logins are remembered for a few minutes, so apply Jellyfin's access rules on every request
            if (AccessRules.Denied(jellyfinUser, remoteIp, network) is { } denied)
                return ErrorResponse(format, ErrorCode.NotAuthorized, denied);

            return await HandleAuthenticated(m, auth, jellyfinUser, q, format);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Subfin] Error handling {Method}", method);
            return ErrorResponse(format, ErrorCode.Generic, "Internal server error.");
        }
    }

    /// <summary>
    /// The request's parameters: the query string, plus the form body of POST requests (the formPost
    /// extension). A key sent both ways keeps the body's values first, so single-valued parameters
    /// take the body's value.
    /// </summary>
    private async Task<IQueryCollection> ReadParametersAsync()
    {
        if (!HttpMethods.IsPost(Request.Method) || !Request.HasFormContentType)
            return Request.Query;
        var form = await Request.ReadFormAsync(HttpContext.RequestAborted);
        var merged = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in form) merged[key] = value;
        foreach (var (key, value) in Request.Query)
            merged[key] = merged.TryGetValue(key, out var fromBody) ? StringValues.Concat(fromBody, value) : value;
        return new QueryCollection(merged);
    }

    // ── Unauthenticated ──────────────────────────────────────────────────────

    private static (JsonObject Json, string Xml) HandleUnauthenticated(string method) => method switch
    {
        "ping" => (SubsonicEnvelope.Ok(), XmlBuilder.Ping()),
        "getlicense" => (SubsonicEnvelope.Ok(new Dictionary<string, object> { ["license"] = new Dictionary<string, object> { ["valid"] = true, ["email"] = "", ["licenseExpires"] = "2099-01-01T00:00:00.000Z" } }), XmlBuilder.License()),
        "getopensubsonicextensions" => (SubsonicEnvelope.Ok(new Dictionary<string, object> { ["openSubsonicExtensions"] = SubsonicConstants.Extensions.Select(e => new Dictionary<string, object> { ["name"] = e.Name, ["versions"] = e.Versions }).ToList() }), XmlBuilder.OpenSubsonicExtensions()),
        _ => (SubsonicEnvelope.Error(ErrorCode.NotFound, "Not found"), XmlBuilder.ErrorEnvelope(ErrorCode.NotFound, "Not found")),
    };

    // ── Authenticated dispatch ───────────────────────────────────────────────

    private async Task<IActionResult> HandleAuthenticated(string method, AuthResult auth, User user, IQueryCollection q, string format)
    {
        var p = new QueryParams(q);
        _currentUser = user;

        return method switch
        {
            "ping" or "getlicense" => Respond(format, HandleUnauthenticated(method)),
            "getmusicfolders" => GetMusicFolders(user, format),
            "getartists" => GetArtists(auth, user, p, format),
            "getindexes" => GetIndexes(auth, user, p, format),
            "getartist" => GetArtist(user, p, format),
            "getalbum" => GetAlbum(user, p, format),
            "getsong" => GetSong(p, format),
            "getmusicdirectory" => GetMusicDirectory(user, p, format),
            "search3" or "search2" => Search3(auth, user, p, format, search2: method == "search2"),
            "getalbumlist" => GetAlbumList(auth, user, p, format, false),
            "getalbumlist2" => GetAlbumList(auth, user, p, format, true),
            "getrandomsongs" => GetRandomSongs(user, p, format),
            "getgenres" => GetGenres(auth, user, p, format),
            "getsongsbygenre" => GetSongsByGenre(user, p, format),
            "getplaylists" => GetPlaylists(user, format),
            "getplaylist" => GetPlaylist(user, p, format),
            "createplaylist" => await CreatePlaylist(user, p, format),
            "updateplaylist" => await UpdatePlaylist(user, p, format),
            "deleteplaylist" => DeletePlaylist(user, p, format),
            "star" => Star(user, format, true),
            "unstar" => Star(user, format, false),
            "setrating" => SetRating(user, p, format),
            "scrobble" => await Scrobble(auth, user, format),
            "getuser" => GetUser(user, p, format),
            "getusers" => GetUsers(user, format),
            "getscanstatus" => GetScanStatus(format),
            "getnowplaying" => GetNowPlaying(format),
            "saveplayqueue" => SavePlayQueue(user, p, format),
            "getplayqueue" => GetPlayQueue(user, format),
            "getshares" or "createshare" or "updateshare" or "deleteshare"
                when SubsonicPlugin.Instance?.Configuration?.SharingEnabled == false
                => ErrorResponse(format, ErrorCode.NotAuthorized, "Sharing is disabled."),
            "getshares" => GetShares(user, format),
            "createshare" => CreateShare(user, p, format),
            "updateshare" => UpdateShare(user, p, format),
            "deleteshare" => DeleteShare(user, p, format),
            "getstarred" => GetStarred(user, format, false),
            "getstarred2" => GetStarred(user, format, true),
            "getartistinfo" or "getartistinfo2" => await GetArtistInfo(auth, user, p, format, method.EndsWith('2')),
            "getalbuminfo" or "getalbuminfo2" => GetAlbumInfo(p, format),
            "getsimilarsongs" or "getsimilarsongs2" => GetSimilarSongs(user, p, format, method.EndsWith('2')),
            "gettopsongs" => GetTopSongs(user, p, format),
            "getlyrics" => await GetLyrics(user, p, format),
            "getlyricsbysongid" => await GetLyricsBySongId(p, format),
            "stream" => await Stream(auth, user, p, format),
            "download" => Download(auth, user, p, format),
            "getcoverart" => GetCoverArt(user, p, format),
            "getavatar" => GetAvatar(user, p, format),
            _ => ErrorResponse(format, ErrorCode.NotFound, $"Unknown method: {method}"),
        };
    }

    // ── Artist tag-entity resolution ─────────────────────────────────────────

    /// <summary>
    /// Resolves an artist name to the tag/index entity ID (the one AlbumArtistIds queries match).
    /// Never use album.MusicArtist?.Id — that's the folder-hierarchy entity and won't match.
    /// Artist ids are the bare GUID everywhere, as in getArtists: clients look artists up by the
    /// artistId of albums and songs among the ones getArtists returned.
    /// </summary>
    private string? ResolveArtistTagId(string? name) => string.IsNullOrEmpty(name) ? null : ArtistIdOf(name);

    // Per-request: artist name → id (a song list names the same artists over and over)
    private readonly Dictionary<string, string?> _artistIds = new(StringComparer.OrdinalIgnoreCase);

    private string? ArtistIdOf(string name)
    {
        if (!_artistIds.TryGetValue(name, out var id))
            _artistIds[name] = id = library.GetArtist(name) is { } a ? a.Id.ToString("N") : null;
        return id;
    }

    // ── Response helper ──────────────────────────────────────────────────────

    /// <summary>Answers in the requested format; the XML is only built when it's asked for.</summary>
    private static IActionResult Respond(string format, JsonObject json, Func<string>? xml = null)
    {
        if (format == "json")
            return new ContentResult { Content = json.ToJsonString(), ContentType = "application/json; charset=utf-8", StatusCode = 200 };
        return new ContentResult { Content = xml?.Invoke() ?? XmlBuilder.ErrorEnvelope(0, "XML not implemented"), ContentType = "text/xml; charset=utf-8", StatusCode = 200 };
    }

    private static IActionResult Respond(string format, (JsonObject Json, string Xml) tuple) =>
        Respond(format, tuple.Json, () => tuple.Xml);

    private static IActionResult ErrorResponse(string format, int code, string message) =>
        Respond(format, SubsonicEnvelope.Error(code, message), () => XmlBuilder.ErrorEnvelope(code, message));

    // ── getMusicFolders ──────────────────────────────────────────────────────

    private IActionResult GetMusicFolders(User user, string format)
    {
        var musicFolders = MusicFoldersFor(user);
        var json = SubsonicEnvelope.Ok(new()
        {
            ["musicFolders"] = new Dictionary<string, object>
            {
                ["musicFolder"] = musicFolders.Select(f => new Dictionary<string, object> { ["id"] = FolderIdToInt(f.ItemId), ["name"] = f.Name }).ToList(),
            },
        });
        return Respond(format, json, () => XmlBuilder.MusicFolders(musicFolders.Select(f => (FolderIdToInt(f.ItemId).ToString(CultureInfo.InvariantCulture), f.Name)).ToList()));  // same int ids as JSON
    }

    // ── getArtists / getIndexes ──────────────────────────────────────────────

    private IActionResult GetArtists(AuthResult auth, User user, QueryParams p, string format)
    {
        var index = BuildArtistIndex(auth, user, p.MusicFolderId);
        var json = SubsonicEnvelope.Ok(new() { ["artists"] = BuildArtistsJson(index) });
        return Respond(format, json, () => XmlBuilder.Artists(index));
    }

    private IActionResult GetIndexes(AuthResult auth, User user, QueryParams p, string format)
    {
        var index = BuildArtistIndex(auth, user, p.MusicFolderId);
        // Required by the spec. ifModifiedSince isn't supported, so the index is always current.
        var lastModified = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var indexes = BuildArtistsJson(index);
        indexes["lastModified"] = lastModified;
        var json = SubsonicEnvelope.Ok(new() { ["indexes"] = indexes });
        return Respond(format, json, () => XmlBuilder.Indexes(index, lastModified: lastModified));
    }

    private List<(string Letter, List<(string Id, string Name, int AlbumCount)> Artists)> BuildArtistIndex(
        AuthResult auth, User user, string? musicFolderId)
    {
        var folderIds = GetEffectiveFolderIds(musicFolderId);
        var cacheKey = $"artistIndex:{auth.UserId:N}:{(folderIds == null ? "all" : string.Join(",", folderIds.OrderBy(x => x)))}";
        const long ttlMs = 15L * 60 * 1000;

        var entries = GetOrRefreshCache(
            cacheKey, ttlMs,
            build: () => BuildArtistList(user, folderIds).Select(a => new ArtistCacheEntry(a.Id, a.Name, a.AlbumCount)).ToList(),
            deserialize: json => JsonSerializer.Deserialize<List<ArtistCacheEntry>>(json),
            serialize: v => JsonSerializer.Serialize(v));

        return GroupByLetter(entries.Select(a => (a.Id, a.Name, a.AlbumCount)));
    }

    private record ArtistCacheEntry(string Id, string Name, int AlbumCount);
    private record GenreCacheEntry(string Name, int SongCount, int AlbumCount);

    private List<(string Id, string Name, int AlbumCount)> BuildArtistList(User user, List<string>? folderIds)
        => LibraryQueries.BuildArtistList(library, user, folderIds);

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
            }).ToList(),
        }).ToList(),
    };

    // ── getArtist ────────────────────────────────────────────────────────────

    private IActionResult GetArtist(User user, QueryParams p, string format)
    {
        var id = p.Id;
        if (!TryParseItemId(id, format, out var guid, out var err)) return err!;

        var artist = GetVisibleItem<MusicArtist>(guid);
        if (artist == null) return ErrorResponse(format, ErrorCode.NotFound, "Artist not found");

        var albums = ArtistAlbums(user, artist);
        logger.LogInformation("[Subfin] getArtist {Name} (guid={Guid}): {Count} albums", artist.Name, guid, albums.Count);

        var artistId = artist.Id.ToString("N");
        var mapped = ItemMapper.ToArtistWithAlbums(artist, albums, a => ItemMapper.AsAlbumId3(ItemMapper.ToAlbumShort(a, artistId, UserDataFor(a), StarredAt(a), ArtistIdOf)));
        var json = SubsonicEnvelope.Ok(new() { ["artist"] = mapped });
        return Respond(format, json, () => XmlBuilder.Artist(mapped));
    }

    // ── getAlbum ─────────────────────────────────────────────────────────────

    private IActionResult GetAlbum(User user, QueryParams p, string format)
    {
        var id = p.Id;
        if (!TryParseItemId(id, format, out var guid, out var err)) return err!;

        var album = GetVisibleItem<MusicAlbum>(guid);
        if (album == null) return ErrorResponse(format, ErrorCode.NotFound, "Album not found");

        var songs = AlbumTracks(user, guid);

        var resolvedArtistId = ResolveArtistTagId(album.AlbumArtist ?? album.AlbumArtists.FirstOrDefault());
        var mapped = ItemMapper.ToAlbum(album, songs, s => ToAlbumSong(s, album), resolvedArtistId, UserDataFor(album), artistIdOf: ArtistIdOf);
        var json = SubsonicEnvelope.Ok(new() { ["album"] = mapped });
        return Respond(format, json, () => XmlBuilder.Album(mapped));
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
        return Respond(format, json, () => XmlBuilder.Song(mapped));
    }

    // ── getMusicDirectory ────────────────────────────────────────────────────

    private IActionResult GetMusicDirectory(User user, QueryParams p, string format)
    {
        var id = p.Id;
        if (!TryParseItemId(id, format, out var guid, out var err)) return err!;

        var item = GetVisibleItem(guid);
        if (item == null) return ErrorResponse(format, ErrorCode.NotFound, "Not found");

        // An album's direct children may be disc folders (Album/CD 1/...), which have no
        // Subsonic representation; list its tracks instead.
        var children = item is MusicAlbum
            ? AlbumTracks(user, guid)
            : library.GetItemList(new InternalItemsQuery(user)
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
            var targetKey = LibraryQueries.CanonicalArtistKey(item.Name ?? "");
            var folderQuery = new InternalItemsQuery(user)
            {
                IncludeItemTypes = [BaseItemKind.MusicArtist],
                Recursive = true,
            };
            ApplyFolderScoping(folderQuery, GetEffectiveFolderIds(null));
            var folderEntities = library.GetItemList(folderQuery)
              .OfType<MusicArtist>()
              .Where(a => a.ParentId != Guid.Empty && LibraryQueries.CanonicalArtistKey(a.Name ?? "") == targetKey)
              .ToList();

            if (folderEntities.Count > 0)
            {
                logger.LogInformation("[Subfin] getMusicDirectory fallback: tag entity {TagName} → {Count} folder entity/entities",
                    item.Name, folderEntities.Count);
                children = folderEntities
                    .SelectMany(fe => library.GetItemList(new InternalItemsQuery(user)
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
            _ => null,
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
            },
        });
        return Respond(format, json, () => XmlBuilder.MusicDirectory(guid.ToString("N"), item.Name ?? "", parentId, childMaps));
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
        var folderIdsForSearch = GetEffectiveFolderIds(null);
        var cacheKeyForSearch = $"artistIndex:{auth.UserId:N}:{(folderIdsForSearch == null ? "all" : string.Join(",", folderIdsForSearch.OrderBy(x => x)))}";
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

        var albums = library.GetItemList(new InternalItemsQuery(user)
        {
            SearchTerm = query,
            IncludeItemTypes = [BaseItemKind.MusicAlbum],
            Limit = albumCount,
            StartIndex = albumOffset,
            Recursive = true,
        }).OfType<MusicAlbum>().Select(a => search2 ? ToAlbumWithArtist(a) : ToAlbumId3WithArtist(a)).ToList();

        var songs = library.GetItemList(new InternalItemsQuery(user)
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
            },
        });
        return Respond(format, json, () => XmlBuilder.SearchResult3(artists, albums, songs, element));
    }

    // ── getAlbumList / getAlbumList2 ─────────────────────────────────────────

    private IActionResult GetAlbumList(AuthResult auth, User user, QueryParams p, string format, bool v2)
    {
        var type = p.Get("type") ?? "alphabeticalByName";
        var size = Math.Min(p.GetInt("size", 10), 500);
        var offset = p.GetInt("offset", 0);
        Func<MusicAlbum, Dictionary<string, object?>> toAlbum = v2 ? ToAlbumId3WithArtist : ToAlbumWithArtist;

        if (type == "recent")
        {
            var recentFolderIds = GetEffectiveFolderIds(p.MusicFolderId);
            var folderSuffix = recentFolderIds == null ? "all" : string.Join(",", recentFolderIds.OrderBy(x => x));
            var cacheKey = $"albumListRecent:{auth.UserId:N}:{folderSuffix}:{size}:{offset}";
            const long recentTtlMs = 5L * 60 * 1000;

            var albumGuids = GetOrRefreshCache(
                cacheKey, recentTtlMs,
                build: () => BuildRecentAlbumIds(user, recentFolderIds, offset, size),
                deserialize: json => JsonSerializer.Deserialize<List<string>>(json),
                serialize: v => JsonSerializer.Serialize(v));

            var recentAlbums = albumGuids
                .Select(id => GetVisibleItem<MusicAlbum>(Guid.ParseExact(id, "N")))
                .Where(a => a != null)
                .Cast<MusicAlbum>()
                .Select(toAlbum)
                .ToList();

            var recentJson = SubsonicEnvelope.Ok(new() { [v2 ? "albumList2" : "albumList"] = new Dictionary<string, object> { ["album"] = recentAlbums } });
            return Respond(format, recentJson, () => XmlBuilder.AlbumList(recentAlbums, v2));
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

        var folderIds = GetEffectiveFolderIds(p.MusicFolderId);
        ApplyFolderScoping(query, folderIds);

        var albums = library.GetItemList(query).OfType<MusicAlbum>().Select(toAlbum).ToList();
        var json = SubsonicEnvelope.Ok(new() { [v2 ? "albumList2" : "albumList"] = new Dictionary<string, object> { ["album"] = albums } });
        return Respond(format, json, () => XmlBuilder.AlbumList(albums, v2));
    }

    // ── getRandomSongs ───────────────────────────────────────────────────────

    private IActionResult GetRandomSongs(User user, QueryParams p, string format)
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
        ApplyFolderScoping(query, GetEffectiveFolderIds(p.MusicFolderId));
        var songs = library.GetItemList(query).OfType<Audio>().Select(ToSongWithArtist).ToList();

        var json = SubsonicEnvelope.Ok(new() { ["randomSongs"] = new Dictionary<string, object> { ["song"] = songs } });
        return Respond(format, json, () => XmlBuilder.RandomSongs(songs));
    }

    // ── getGenres ────────────────────────────────────────────────────────────

    private IActionResult GetGenres(AuthResult auth, User user, QueryParams p, string format)
    {
        var folderIds = GetEffectiveFolderIds(p.MusicFolderId);
        var folderSuffix = folderIds == null ? "all" : string.Join(",", folderIds.OrderBy(x => x));
        var cacheKey = $"genres:{auth.UserId:N}:{folderSuffix}";
        const long ttlMs = 30L * 60 * 1000;

        var cached = GetOrRefreshCache(
            cacheKey, ttlMs,
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
                var genreResult = library.GetMusicGenres(genreQuery);
                return genreResult.Items.Select(g =>
                {
                    var name = g.Item.Name ?? "";
                    var songQuery = new InternalItemsQuery(user)
                    {
                        Genres = new List<string> { name },
                        IncludeItemTypes = [BaseItemKind.Audio],
                        Recursive = true,
                    };
                    ApplyFolderScoping(songQuery, folderIds);
                    var songCount = library.GetCount(songQuery);
                    var albumQuery = new InternalItemsQuery(user)
                    {
                        Genres = new List<string> { name },
                        IncludeItemTypes = [BaseItemKind.MusicAlbum],
                        Recursive = true,
                    };
                    ApplyFolderScoping(albumQuery, folderIds);
                    var albumCount = library.GetCount(albumQuery);
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
                ["genre"] = genres.Select(g => new Dictionary<string, object> { ["value"] = g.Name, ["songCount"] = g.SongCount, ["albumCount"] = g.AlbumCount }).ToList(),
            },
        });
        return Respond(format, jsonObj, () => XmlBuilder.Genres(genres));
    }

    // ── getSongsByGenre ──────────────────────────────────────────────────────

    private IActionResult GetSongsByGenre(User user, QueryParams p, string format)
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
        ApplyFolderScoping(query, GetEffectiveFolderIds(p.MusicFolderId));
        var songs = library.GetItemList(query).OfType<Audio>().Select(ToSongWithArtist).ToList();

        var json = SubsonicEnvelope.Ok(new() { ["songsByGenre"] = new Dictionary<string, object> { ["song"] = songs } });
        return Respond(format, json, () => XmlBuilder.SongsByGenre(songs));
    }

    // ── Playlists ────────────────────────────────────────────────────────────
    // Jellyfin's rules: a playlist is visible to its owner, to users it is shared with and, when
    // public, to everyone; its owner and shares with CanEdit may edit it; only the owner may delete
    // it or change whether it is public. Songs the current user may not access are hidden.

    private IActionResult GetPlaylists(User user, string format)
    {
        var playlists = playlistManager.GetPlaylists(user.Id)
            .Select(pl => MapPlaylist(pl, user, false)).ToList();

        var json = SubsonicEnvelope.Ok(new() { ["playlists"] = new Dictionary<string, object> { ["playlist"] = playlists } });
        return Respond(format, json, () => XmlBuilder.Playlists(playlists));
    }

    private IActionResult GetPlaylist(User user, QueryParams p, string format)
    {
        if (!TryParseItemId(p.Id, format, out var guid, out var err)) return err!;

        var pl = GetVisiblePlaylist(user, guid);
        if (pl == null) return ErrorResponse(format, ErrorCode.NotFound, "Playlist not found");

        var mapped = MapPlaylist(pl, user, true);
        var json = SubsonicEnvelope.Ok(new() { ["playlist"] = mapped });
        return Respond(format, json, () => XmlBuilder.Playlist(mapped));
    }

    private Playlist? GetVisiblePlaylist(User user, Guid id) =>
        id != Guid.Empty && library.GetItemById<Playlist>(id) is { } pl && pl.IsVisible(user) ? pl : null;

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
        var owner = pl.OwnerUserId.Equals(user.Id) ? user.Username : userManager.GetUserById(pl.OwnerUserId)?.Username ?? "";

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
        var songs = VisibleSongs(_query["songId"]);
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

            var result = await playlistManager.CreatePlaylist(new MediaBrowser.Model.Playlists.PlaylistCreationRequest
            {
                Name = name,
                ItemIdList = [],
                UserId = user.Id,
                MediaType = MediaType.Audio,
            });
            pl = library.GetItemById<Playlist>(Guid.Parse(result.Id));
            if (pl == null) return ErrorResponse(format, ErrorCode.Generic, "Failed to create playlist");
            if (songs.Count > 0)
                await SavePlaylistAsync(pl, [.. songs.Select(LinkedChild.Create)], added: true);
        }

        var mapped = MapPlaylist(pl, user, true);
        var json = SubsonicEnvelope.Ok(new() { ["playlist"] = mapped });
        return Respond(format, json, () => XmlBuilder.Playlist(mapped));
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
        if (_query.ContainsKey("comment")) pl.Overview = p.Get("comment");

        // songIndexToRemove indexes the entries this user sees (as returned by getPlaylist);
        // entries hidden from them are left alone.
        var toRemove = _query["songIndexToRemove"]
            .Select(s => int.TryParse(s, out var i) ? i : -1)
            .ToHashSet();
        var kept = new List<LinkedChild>();
        var visibleIndex = 0;
        foreach (var (child, song) in PlaylistEntries(pl))
        {
            if (song != null && toRemove.Contains(visibleIndex++)) continue;
            kept.Add(child);
        }
        var added = VisibleSongs(_query["songIdToAdd"]);
        kept.AddRange(added.Select(LinkedChild.Create));

        await SavePlaylistAsync(pl, kept, added: added.Count > 0);
        return Respond(format, SubsonicEnvelope.Ok(), XmlBuilder.Ping);
    }

    private IActionResult DeletePlaylist(User user, QueryParams p, string format)
    {
        if (!TryParseItemId(p.Id, format, out var guid, out var err)) return err!;

        var pl = GetVisiblePlaylist(user, guid);
        if (pl == null) return ErrorResponse(format, ErrorCode.NotFound, "Playlist not found");
        if (!pl.OwnerUserId.Equals(user.Id))
            return ErrorResponse(format, ErrorCode.NotAuthorized, "Only the owner can delete a playlist.");

        library.DeleteItem(pl, new DeleteOptions { DeleteFileLocation = true });
        return Respond(format, SubsonicEnvelope.Ok(), XmlBuilder.Ping);
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
            playlistManager.SavePlaylistFile(pl);
    }

    // ── star / unstar ────────────────────────────────────────────────────────

    private IActionResult Star(User user, string format, bool star)
    {
        var ids = _query["id"].Concat(_query["albumId"]).Concat(_query["artistId"])
            .Select(s => s ?? "").Where(s => !string.IsNullOrEmpty(s)).ToList();

        foreach (var id in ids)
        {
            if (!Guid.TryParse(ItemMapper.StripPrefix(id), out var guid)) continue;
            var item = GetVisibleItem(guid);
            if (item == null) continue;
            var data = userData.GetUserData(user, item);
            if (data == null) continue;
            data.IsFavorite = star;
            userData.SaveUserData(user, item, data, UserDataSaveReason.UpdateUserRating, CancellationToken.None);
            SubsonicStore.SetStarred(user.Id.ToString("N"), item.Id.ToString("N"), star);
        }
        return Respond(format, SubsonicEnvelope.Ok(), XmlBuilder.Ping);
    }

    // ── setRating ────────────────────────────────────────────────────────────

    private IActionResult SetRating(User user, QueryParams p, string format)
    {
        var id = p.Id;
        var rating = p.GetInt("rating", 0);
        if (string.IsNullOrEmpty(id)) return ErrorResponse(format, ErrorCode.RequiredParameterMissing, "Missing id");
        if (!Guid.TryParse(ItemMapper.StripPrefix(id), out var guid)) return Respond(format, SubsonicEnvelope.Ok(), XmlBuilder.Ping);

        var item = GetVisibleItem(guid);
        if (item == null) return Respond(format, SubsonicEnvelope.Ok(), XmlBuilder.Ping);

        var data = userData.GetUserData(user, item);
        if (data == null) return Respond(format, SubsonicEnvelope.Ok(), XmlBuilder.Ping);
        data.Rating = rating > 0 ? rating : null;
        userData.SaveUserData(user, item, data, UserDataSaveReason.UpdateUserRating, CancellationToken.None);
        return Respond(format, SubsonicEnvelope.Ok(), XmlBuilder.Ping);
    }

    // ── scrobble ─────────────────────────────────────────────────────────────

    private async Task<IActionResult> Scrobble(AuthResult auth, User user, string format)
    {
        // Several id parameters (each with its own time) submit a batch, e.g. plays queued offline.
        var ids = _query["id"];
        var times = _query["time"];
        var isSubmission = !string.Equals(_query.First("submission"), "false", StringComparison.OrdinalIgnoreCase);

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

        return Respond(format, SubsonicEnvelope.Ok(), XmlBuilder.Ping);
    }

    // Plays submitted this long after they happened (offline clients) are recorded as history
    // rather than as a live playback session, so they keep their real time.
    private static readonly TimeSpan LateScrobble = TimeSpan.FromMinutes(10);

    /// <summary>Counts a play that happened at <paramref name="at"/>, in a single user-data write.</summary>
    private void RecordPastPlay(User user, Audio item, DateTime at)
    {
        var data = userData.GetUserData(user, item);
        if (data == null) return;
        data.PlayCount++;
        data.Played = true;
        if (data.LastPlayedDate is not { } last || last < at)
            data.LastPlayedDate = at;
        userData.SaveUserData(user, item, data, UserDataSaveReason.PlaybackFinished, CancellationToken.None);
        logger.LogInformation("[Subfin] scrobble: recorded play from {At:o}", at);
    }

    private static void RememberStreamedAs(Guid userId, Guid itemId, PlayMethod method)
    {
        var now = DateTime.UtcNow;
        if (StreamMethods.Count > 2000)
            foreach (var stale in StreamMethods.Where(kv => kv.Value.At < now.AddHours(-12)).Select(kv => kv.Key).ToList())
                StreamMethods.TryRemove(stale, out _);
        StreamMethods[(userId, itemId)] = (method, now);
    }

    /// <summary>How the song was streamed to this user; the file as is unless the plugin transcoded it.</summary>
    private static PlayMethod StreamedAs(Guid userId, Guid itemId) =>
        StreamMethods.TryGetValue((userId, itemId), out var streamed) ? streamed.Method : PlayMethod.DirectPlay;

    /// <summary>
    /// The Jellyfin device a request plays on: one per user and Subsonic client (the <c>c</c> parameter),
    /// so each app gets its own session in the dashboard.
    /// </summary>
    private (string Id, string Name) ClientDevice(AuthResult auth)
    {
        if (auth.ShareId != null)
            return ($"opensubsonic-share-{auth.ShareId}", "Share link");
        var client = _query.First("c").Trim() is { Length: > 0 } c ? c : "Subsonic client";
        var slug = new string(client.Select(ch => char.IsAsciiLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-').ToArray());
        return ($"opensubsonic-{auth.UserId:N}-{slug}", client);
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
            var (deviceId, clientName) = ClientDevice(auth);
            var clientVersion = _query.First("v") is { Length: > 0 } cv ? cv : "1.0.0";
            var session = await sessions.LogSessionActivity(clientName, clientVersion, deviceId, clientName, remoteIp, user);
            // Every report carries it: left out, it reads as 0, which is Transcode
            var playMethod = StreamedAs(user.Id, item.Id);

            // Jellyfin counts a play when it starts. Clients report "now playing" when a song starts and
            // submit it when it ends: a song this session already started is only stopped, not started again.
            if (session.NowPlayingItem?.Id != item.Id)
            {
                await sessions.OnPlaybackStart(new PlaybackStartInfo
                {
                    ItemId = item.Id,
                    SessionId = session.Id,
                    PositionTicks = 0L,
                    PlayMethod = playMethod,
                    IsPaused = false,
                    CanSeek = true,
                });
            }

            if (finished)
            {
                // Stopped at the end: played to completion.
                await sessions.OnPlaybackStopped(new PlaybackStopInfo
                {
                    ItemId = item.Id,
                    SessionId = session.Id,
                    PositionTicks = item.RunTimeTicks,
                    Failed = false,
                });
                logger.LogInformation("[Subfin] scrobble: sent start+stop (submission)");
            }
            else
            {
                _ = sessions.OnPlaybackProgress(new PlaybackProgressInfo
                {
                    ItemId = item.Id,
                    SessionId = session.Id,
                    PositionTicks = 0L,
                    PlayMethod = playMethod,
                    IsPaused = false,
                });
                logger.LogInformation("[Subfin] scrobble: sent start+progress (now playing)");
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Subfin] scrobble: session reporting failed (non-fatal)");
        }
    }

    // ── getNowPlaying ────────────────────────────────────────────────────────

    private IActionResult GetNowPlaying(string format)
    {
        // Everyone's playback, as in Subsonic, but only songs this user may see.
        var entries = sessions.Sessions
            .Where(s => s.NowPlayingItem != null)
            .Select(s => (Session: s, Song: GetVisibleItem<Audio>(s.NowPlayingItem!.Id)))
            .Where(x => x.Song != null)
            .Select(x => new NowPlayingXml(
                ToSongWithArtist(x.Song!),
                x.Session.UserName ?? "",
                Math.Max(0, (int)(DateTime.UtcNow - x.Session.LastActivityDate).TotalMinutes),
                PlayerId(x.Session.Id),
                x.Session.DeviceName ?? x.Session.Client ?? ""))
            .ToList();

        var json = SubsonicEnvelope.Ok(new()
        {
            ["nowPlaying"] = new Dictionary<string, object>
            {
                ["entry"] = entries.Select(e => new Dictionary<string, object?>(e.Song)
                {
                    ["username"] = e.Username,
                    ["minutesAgo"] = e.MinutesAgo,
                    ["playerId"] = e.PlayerId,
                    ["playerName"] = e.PlayerName,
                }).ToList(),
            },
        });
        return Respond(format, json, () => XmlBuilder.NowPlaying(entries));
    }

    /// <summary>Subsonic player ids are integers; Jellyfin session ids are strings. Stable per session.</summary>
    private static int PlayerId(string sessionId) =>
        (int)(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sessionId))) & 0x7fffffff);

    // ── savePlayQueue / getPlayQueue ─────────────────────────────────────────

    private IActionResult SavePlayQueue(User user, QueryParams p, string format)
    {
        var ids = _query["id"].Select(s => s ?? "").Where(s => !string.IsNullOrEmpty(s)).ToList();
        var current = p.Get("current");
        var position = p.GetLong("position", 0);
        SubsonicStore.SavePlayQueue(user.Id.ToString("N"), ids, current, 0, position, p.Get("c") ?? "");
        return Respond(format, SubsonicEnvelope.Ok(), XmlBuilder.Ping);
    }

    private IActionResult GetPlayQueue(User user, string format)
    {
        var pq = SubsonicStore.GetPlayQueue(user.Id.ToString("N"));
        if (pq == null)
        {
            // playQueue and its username/changed/changedBy are required even when nothing was saved
            var never = DateTimeOffset.UnixEpoch.ToString("o");
            return Respond(format,
                SubsonicEnvelope.Ok(new() { ["playQueue"] = new Dictionary<string, object> { ["username"] = user.Username, ["changed"] = never, ["changedBy"] = "" } }),
                () => XmlBuilder.PlayQueue(null, 0, 0, never, "", [], user.Username));
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
                ["username"] = user.Username,
                ["changed"] = pq.ChangedAt ?? DateTimeOffset.UnixEpoch.ToString("o"),
                ["changedBy"] = pq.ChangedBy,
                ["entry"] = songs,
            },
        });
        return Respond(format, json, () => XmlBuilder.PlayQueue(pq.CurrentId, pq.CurrentIndex, pq.PositionMs, pq.ChangedAt, pq.ChangedBy, songs, user.Username));
    }

    // ── Shares ───────────────────────────────────────────────────────────────

    private IActionResult GetShares(User user, string format)
    {
        var shares = SubsonicStore.GetSharesForUser(user.Id.ToString("N"));
        var baseUrl = $"{Request.Scheme}://{Request.Host}{Request.PathBase}";  // PathBase = Jellyfin base URL
        var xmlShares = shares.Select(s => BuildShareXml(s, baseUrl, user)).ToList();
        var json = SubsonicEnvelope.Ok(new() { ["shares"] = new Dictionary<string, object> { ["share"] = xmlShares.Select(ShareToJson).ToList() } });
        return Respond(format, json, () => XmlBuilder.Shares(xmlShares));
    }

    private IActionResult CreateShare(User user, QueryParams p, string format)
    {
        var ids = _query["id"].Select(s => s ?? "").Where(s => !string.IsNullOrEmpty(s)).ToList();
        if (ids.Count == 0) return ErrorResponse(format, ErrorCode.RequiredParameterMissing, "Missing id");
        var desc = p.Get("description");
        var expiresParam = p.Get("expires");
        string? expiresAt = null;
        if (!string.IsNullOrEmpty(expiresParam) && long.TryParse(expiresParam, out var ms) && ms > 0)
            expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(ms).ToString("o");

        // Expand IDs to a flat list of audio track GUIDs the user may access.
        // Artist IDs are bare GUIDs; album IDs use al- prefix; playlist IDs use pl- prefix.
        var flatIds = LibraryQueries.ExpandShareIds(library, user, ids);
        if (flatIds.Count == 0) return ErrorResponse(format, ErrorCode.NotFound, "Nothing to share: no accessible songs for the given ids.");

        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
            .Replace("+", "-").Replace("/", "_").Replace("=", "");
        var uid = SubsonicStore.InsertShare(user.Id.ToString("N"), ids, flatIds, desc, expiresAt, secret);

        var share = SubsonicStore.GetShare(uid)!;
        var baseUrl = $"{Request.Scheme}://{Request.Host}{Request.PathBase}";  // PathBase = Jellyfin base URL
        var xmlShare = BuildShareXml(share, baseUrl, user);
        var json = SubsonicEnvelope.Ok(new() { ["shares"] = new Dictionary<string, object> { ["share"] = new[] { ShareToJson(xmlShare) } } });
        return Respond(format, json, () => XmlBuilder.ShareCreated(xmlShare));
    }

    /// <summary>Only the user who created a share may change or delete it.</summary>
    private static bool OwnsShare(User user, string shareUid) =>
        SubsonicStore.GetShare(shareUid)?.OwnerUserId == user.Id.ToString("N");

    private static IActionResult UpdateShare(User user, QueryParams p, string format)
    {
        var id = p.Id;
        if (string.IsNullOrEmpty(id)) return ErrorResponse(format, ErrorCode.RequiredParameterMissing, "Missing id");
        if (!OwnsShare(user, id)) return ErrorResponse(format, ErrorCode.NotFound, "Share not found");
        // Only what's given changes; expires=0 removes the expiry
        if (p.Has("description"))
            SubsonicStore.UpdateShareDescription(id, p.Get("description"));
        if (long.TryParse(p.Get("expires"), out var ms))
            SubsonicStore.UpdateShareExpiry(id, ms > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms).ToString("o") : null);
        return Respond(format, SubsonicEnvelope.Ok(), XmlBuilder.Ping);
    }

    private static IActionResult DeleteShare(User user, QueryParams p, string format)
    {
        var id = p.Id;
        if (string.IsNullOrEmpty(id)) return ErrorResponse(format, ErrorCode.RequiredParameterMissing, "Missing id");
        if (!OwnsShare(user, id)) return ErrorResponse(format, ErrorCode.NotFound, "Share not found");
        SubsonicStore.DeleteShare(id);
        return Respond(format, SubsonicEnvelope.Ok(), XmlBuilder.Ping);
    }

    private ShareXml BuildShareXml(ShareRecord s, string baseUrl, User user)
    {
        var secret = SubsonicStore.GetShareSecret(s.ShareUid) ?? "";
        var url = $"{baseUrl}/opensubsonic/share/{s.ShareUid}?secret={Uri.EscapeDataString(secret)}";
        var createdDt = DateTime.Parse(s.CreatedAt, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
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
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)
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
        var artists = library.GetItemList(new InternalItemsQuery(user)
        { IncludeItemTypes = [BaseItemKind.MusicArtist], IsFavorite = true, Recursive = true })
            .OfType<MusicArtist>().Select(a => new Dictionary<string, object?> { ["id"] = a.Id.ToString("N"), ["name"] = a.Name ?? "", ["starred"] = StarredAt(a) ?? a.DateCreated.ToString("o") }).ToList();
        var albums = library.GetItemList(new InternalItemsQuery(user)
        { IncludeItemTypes = [BaseItemKind.MusicAlbum], IsFavorite = true, Recursive = true })
            .OfType<MusicAlbum>().Select(a => v2 ? ToAlbumId3WithArtist(a) : ToAlbumWithArtist(a)).ToList();
        var songs = library.GetItemList(new InternalItemsQuery(user)
        { IncludeItemTypes = [BaseItemKind.Audio], IsFavorite = true, Recursive = true })
            .OfType<Audio>().Select(ToSongWithArtist).ToList();

        var json = SubsonicEnvelope.Ok(new()
        {
            [v2 ? "starred2" : "starred"] = new Dictionary<string, object> { ["artist"] = artists, ["album"] = albums, ["song"] = songs },
        });
        return Respond(format, json, () => XmlBuilder.Starred(artists, albums, songs, v2));
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
            target = userManager.GetUserByName(name);
            if (target == null) return ErrorResponse(format, ErrorCode.NotFound, "User not found");
        }

        var mapped = MapUser(target);
        return Respond(format, SubsonicEnvelope.Ok(new() { ["user"] = mapped }), () => XmlBuilder.User(mapped));
    }

    private IActionResult GetUsers(User user, string format)
    {
        if (!user.HasPermission(PermissionKind.IsAdministrator))
            return ErrorResponse(format, ErrorCode.NotAuthorized, "Only administrators can list users.");

        var users = userManager.GetUsers().Select(MapUser).ToList();
        return Respond(format, SubsonicEnvelope.Ok(new() { ["users"] = new Dictionary<string, object> { ["user"] = users } }), () => XmlBuilder.Users(users));
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

    private static IActionResult GetScanStatus(string format)
    {
        var json = SubsonicEnvelope.Ok(new()
        { ["scanStatus"] = new Dictionary<string, object> { ["scanning"] = false, ["count"] = 0 } });
        return Respond(format, json, XmlBuilder.ScanStatus);
    }

    // ── getArtistInfo / getArtistInfo2 ───────────────────────────────────────

    /// <summary>
    /// Artist details from Jellyfin: its biography (overview), MusicBrainz id and image, and similar artists
    /// from Jellyfin's similar-items providers (per the library's settings, e.g. ListenBrainz).
    /// </summary>
    private async Task<IActionResult> GetArtistInfo(AuthResult auth, User user, QueryParams p, string format, bool v2)
    {
        var id = p.Id;
        if (!TryParseItemId(id, format, out var guid, out var err)) return err!;

        var artist = GetVisibleItem<MusicArtist>(guid);
        if (artist == null) return ErrorResponse(format, ErrorCode.NotFound, "Artist not found");

        var bio = string.IsNullOrWhiteSpace(artist.Overview) ? null : artist.Overview;
        var mbid = artist.GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.MusicBrainzArtist);

        // Only artists the user can open: clients look them up among the ones getArtists returned
        var count = Math.Max(0, p.GetInt("count", 20));
        var known = BuildArtistIndex(auth, user, null).SelectMany(l => l.Artists).ToDictionary(a => a.Id, a => a.AlbumCount);
        var similar = count == 0 ? [] : await similarItems.GetSimilarItemsAsync(
            artist, [], user, new DtoOptions(false), null, library.GetLibraryOptions(artist), HttpContext.RequestAborted);
        var similarArtistDicts = similar
            .OfType<MusicArtist>()
            .Where(a => a.Id != artist.Id && known.ContainsKey(a.Id.ToString("N")))
            .Take(count)
            .Select(a => new Dictionary<string, object?>
            {
                ["id"] = a.Id.ToString("N"),
                ["name"] = a.Name ?? "",
                ["coverArt"] = $"ar-{a.Id:N}",
                ["albumCount"] = known[a.Id.ToString("N")],
            })
            .ToList();

        // Jellyfin's image endpoint serves the artist's image (or the one standing in for it)
        var imageItem = CoverImageItem(user, artist);
        var artistImageUrl = imageItem == null ? null : $"{Request.Scheme}://{Request.Host}{Request.PathBase}/Items/{imageItem.Id:N}/Images/Primary";

        var jsonKey = v2 ? "artistInfo2" : "artistInfo";
        var jsonInfo = new Dictionary<string, object>
        {
            ["similarArtist"] = similarArtistDicts.Select(s => (object)s).ToList(),
        };
        if (artistImageUrl != null)
        {
            jsonInfo["smallImageUrl"] = artistImageUrl;
            jsonInfo["mediumImageUrl"] = artistImageUrl;
            jsonInfo["largeImageUrl"] = artistImageUrl;
        }
        if (bio != null) jsonInfo["biography"] = bio;
        if (mbid != null) jsonInfo["musicBrainzId"] = mbid;
        var json = SubsonicEnvelope.Ok(new() { [jsonKey] = jsonInfo });
        return Respond(format, json, () => XmlBuilder.ArtistInfo(bio, mbid, artistImageUrl, similarArtistDicts, v2));
    }

    // ── getAlbumInfo / getAlbumInfo2 ─────────────────────────────────────────

    /// <summary>Album details from Jellyfin: its notes (overview) and MusicBrainz id.</summary>
    private IActionResult GetAlbumInfo(QueryParams p, string format)
    {
        var id = p.Id;
        if (!TryParseItemId(id, format, out var guid, out var err)) return err!;

        var album = GetVisibleItem<MusicAlbum>(guid);
        if (album == null) return ErrorResponse(format, ErrorCode.NotFound, "Album not found");

        var notes = string.IsNullOrWhiteSpace(album.Overview) ? null : album.Overview;
        var mbid = album.GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.MusicBrainzAlbum);

        const string jsonKey = "albumInfo";  // same element for getAlbumInfo and getAlbumInfo2 (unlike artistInfo2)
        var jsonInfo = new Dictionary<string, object>();
        if (notes != null) jsonInfo["notes"] = notes;
        if (mbid != null) jsonInfo["musicBrainzId"] = mbid;
        var json = SubsonicEnvelope.Ok(new() { [jsonKey] = jsonInfo });
        return Respond(format, json, () => XmlBuilder.AlbumInfo(notes, mbid));
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
            results = musicManager.GetInstantMixFromArtist(ma, user, dtoOptions);
        else
            results = musicManager.GetInstantMixFromItem(item, user, dtoOptions);

        var songs = results.Take(count).OfType<Audio>().Select(ToSongWithArtist).ToList();
        var jsonKey = v2 ? "similarSongs2" : "similarSongs";
        var json = SubsonicEnvelope.Ok(new() { [jsonKey] = new Dictionary<string, object> { ["song"] = songs } });
        return Respond(format, json, () => XmlBuilder.SimilarSongs(songs, v2));
    }

    // ── getTopSongs ──────────────────────────────────────────────────────────

    private IActionResult GetTopSongs(User user, QueryParams p, string format)
    {
        var artistName = p.Get("artist");
        if (string.IsNullOrEmpty(artistName))
            return Respond(format, SubsonicEnvelope.Ok(new() { ["topSongs"] = new Dictionary<string, object> { ["song"] = new List<object>() } }), () => XmlBuilder.TopSongs([]));

        var count = p.GetInt("count", 50);
        var tagArtist = library.GetArtist(artistName);

        var songs = library.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.Audio],
            AlbumArtistIds = [tagArtist.Id],
            OrderBy = [(ItemSortBy.PlayCount, SortOrder.Descending)],
            Limit = count,
            Recursive = true,
        }).OfType<Audio>().Select(ToSongWithArtist).ToList();

        var json = SubsonicEnvelope.Ok(new() { ["topSongs"] = new Dictionary<string, object> { ["song"] = songs } });
        return Respond(format, json, () => XmlBuilder.TopSongs(songs));
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
            var results = library.GetItemList(new InternalItemsQuery(user)
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

        var lyricsText = "";
        var resolvedArtist = artist;
        var resolvedTitle = title;

        if (song != null)
        {
            resolvedArtist = song.Artists.FirstOrDefault() ?? artist;
            resolvedTitle = song.Name ?? title;
            var dto = await lyricManager.GetLyricsAsync(song, CancellationToken.None);
            if (dto?.Lyrics != null)
                lyricsText = string.Join("\n", dto.Lyrics.Select(l => l.Text));
        }

        var json = SubsonicEnvelope.Ok(new()
        {
            ["lyrics"] = new Dictionary<string, object?>
            {
                ["artist"] = resolvedArtist,
                ["title"] = resolvedTitle,
                ["value"] = lyricsText,
            },
        });

        return Respond(format, json, () => XmlBuilder.OkEnvelope(w =>
        {
            w.WriteStartElement("lyrics", "http://subsonic.org/restapi");
            w.WriteAttributeString("artist", resolvedArtist);
            w.WriteAttributeString("title", resolvedTitle);
            if (!string.IsNullOrEmpty(lyricsText))
                w.WriteString(lyricsText);
            w.WriteEndElement();
        }));
    }

    private async Task<IActionResult> GetLyricsBySongId(QueryParams p, string format)
    {
        var id = p.Id;
        if (string.IsNullOrEmpty(id)) return ErrorResponse(format, ErrorCode.RequiredParameterMissing, "Missing id");
        if (!TryParseItemId(id, format, out var guid, out var err)) return err!;

        var song = GetVisibleItem<Audio>(guid);
        if (song == null) return ErrorResponse(format, ErrorCode.NotFound, "Song not found");

        var dto = await lyricManager.GetLyricsAsync(song, CancellationToken.None);

        var structuredLyrics = new List<object>();
        if (dto?.Lyrics is { Count: > 0 })
        {
            // Mark synced if metadata says so OR every line has a non-zero timestamp (LRC files
            // don't always set IsSynced). Require ALL lines to have timestamps — Tempus crashes
            // (NPE) on auto-unbox if any synced line is missing start.
            var allHaveTimestamps = dto.Lyrics.All(l => l.Start is > 0);
            var synced = (dto.Metadata.IsSynced == true || allHaveTimestamps) && dto.Lyrics.All(l => l.Start.HasValue);
            const string lang = "und"; // undetermined — Jellyfin doesn't expose language per lyric set
            var displayArtist = song.Artists.FirstOrDefault() ?? "";
            var displayTitle = song.Name ?? "";

            var lines = dto.Lyrics.Select(l =>
            {
                // Always include start (even for unsynced) — Navic's Line.start is non-nullable Int.
                // For unsynced lines or lines without a timestamp, default to 0.
                var startMs = synced && l.Start.HasValue ? (int)(l.Start.Value / 10000L) : 0;
                return (object)new Dictionary<string, object> { ["start"] = startMs, ["value"] = l.Text };
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
            ["lyricsList"] = new Dictionary<string, object> { ["structuredLyrics"] = structuredLyrics },
        });

        return Respond(format, json, () => XmlBuilder.OkEnvelope(w =>
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
                    w.WriteString(lineObj["value"].ToString() ?? "");  // <line start="…">text</line>
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
        const double cacheDays = 30;

        var cached = SubsonicStore.GetDerivedCache(cacheKey);
        if (cached != null)
        {
            var ageDays = (DateTimeOffset.UtcNow - DateTimeOffset.Parse(cached.CachedAt)).TotalDays;
            if (ageDays < cacheDays)
                return cached.ValueJson;
        }

        var keys = await authManager.GetApiKeys();
        var existing = keys.FirstOrDefault(k => k.AppName == PluginApiKeyName);

        if (existing == null)
        {
            await authManager.CreateApiKey(PluginApiKeyName);
            keys = await authManager.GetApiKeys();
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

        var needsTranscode = (targetFormat != null && targetFormat != "raw") || bitRate > 0 || timeOff > 0;

        logger.LogInformation("[Subfin] stream id={Id} format={Format} bitRate={BitRate} timeOff={TimeOff} needsTranscode={NeedsTranscode}",
            id, targetFormat, bitRate, timeOff, needsTranscode);

        var apiKey = needsTranscode ? await GetOrCreatePluginApiKey() : null;
        if (needsTranscode && string.IsNullOrEmpty(apiKey))
            logger.LogWarning("[Subfin] Could not obtain plugin API key — serving direct");
        if (string.IsNullOrEmpty(apiKey))
        {
            RememberStreamedAs(user.Id, guid, PlayMethod.DirectPlay);
            return PhysicalFile(item.Path, ItemMapper.AudioMimeType(item.Container), null, true);
        }
        RememberStreamedAs(user.Id, guid, PlayMethod.Transcode);

        var (container, audioCodec, mimeType) = MapTranscodeFormat(targetFormat);
        var qs = $"audioCodec={audioCodec}&static=false" +
                 $"&userId={auth.UserId:N}" +
                 $"&deviceId={Uri.EscapeDataString(ClientDevice(auth).Id)}";
        if (bitRate > 0) qs += $"&audioBitRate={bitRate * 1000}";
        if (timeOff > 0) qs += $"&startTimeTicks={timeOff * 10_000_000L}";
        // Jellyfin reuses a song's transcode for the same device and play session: one play session per set
        // of settings keeps a seek or another bitrate from getting back an earlier transcode
        qs += $"&playSessionId=subfin-{audioCodec}-{bitRate}-{timeOff}";

        // Loopback URL (incl. Jellyfin's base URL): the client-facing host may be a reverse proxy,
        // a mapped port or a name this server can't resolve.
        var url = $"{appHost.GetApiUrlForLocalAccess().TrimEnd('/')}/Audio/{guid:N}/stream.{container}?{qs}";
        logger.LogInformation("[Subfin] stream proxy → {Url}", url);
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Authorization", $"MediaBrowser Token=\"{apiKey}\"");

        var rangeHeader = Request.Headers.Range.ToString();
        if (!string.IsNullOrEmpty(rangeHeader))
            req.Headers.TryAddWithoutValidation("Range", rangeHeader);

        var resp = await httpClientFactory.CreateClient().SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        Response.RegisterForDispose(resp);
        Response.StatusCode = (int)resp.StatusCode;

        if (resp.Content.Headers.ContentLength.HasValue)
            Response.Headers["Content-Length"] = resp.Content.Headers.ContentLength.Value.ToString();
        if (resp.Content.Headers.ContentRange != null)
            Response.Headers.ContentRange = resp.Content.Headers.ContentRange.ToString();

        // Use our known mimeType — Jellyfin may return "video/webm" for audio-only WebM which confuses clients
        return new FileStreamResult(await resp.Content.ReadAsStreamAsync(), mimeType);
    }

    private IActionResult Download(AuthResult auth, User user, QueryParams p, string format)
    {
        if (!TryGetServableSong(auth, user, p, format, PermissionKind.EnableContentDownloading, out var item, out var err)) return err!;

        return PhysicalFile(item.Path, ItemMapper.AudioMimeType(item.Container), Path.GetFileName(item.Path), true);
    }

    // ── getCoverArt / getAvatar ──────────────────────────────────────────────

    /// <summary>The albums of an artist the user can see.</summary>
    private List<MusicAlbum> ArtistAlbums(User user, MusicArtist artist)
    {
        var folderIds = GetEffectiveFolderIds(null);
        var guid = artist.Id;

        var query = new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.MusicAlbum],
            AlbumArtistIds = [guid],
            Recursive = true,
        };
        ApplyFolderScoping(query, folderIds);

        var albums = library.GetItemList(query).OfType<MusicAlbum>().ToList();

        // Fallback 1: if no albums found, re-resolve via GetArtist(name) — the guid we received
        // may be a folder/hierarchy entity (whose ID doesn't work with AlbumArtistIds) rather than
        // the tag/index entity. GetArtist(name) returns the tag entity.
        if (albums.Count == 0 && !string.IsNullOrEmpty(artist.Name))
        {
            var tagEntity = library.GetArtist(artist.Name);
            if (tagEntity.Id != guid)
            {
                var fallbackQuery = new InternalItemsQuery(user)
                {
                    IncludeItemTypes = [BaseItemKind.MusicAlbum],
                    AlbumArtistIds = [tagEntity.Id],
                    Recursive = true,
                };
                ApplyFolderScoping(fallbackQuery, folderIds);
                albums = library.GetItemList(fallbackQuery).OfType<MusicAlbum>().ToList();
            }
        }

        // Fallback 2: artists with special characters (e.g. *NSYNC, AC/DC) may still return 0
        // albums because Jellyfin's AlbumArtistIds lookup matches entity name lowercased against
        // CleanValue, which strips special chars (*nsync ≠ nsync). Fall back to fetching all
        // albums and filtering by AlbumArtist name in C#.
        if (albums.Count == 0 && !string.IsNullOrEmpty(artist.Name))
        {
            var allQuery = new InternalItemsQuery(user)
            {
                IncludeItemTypes = [BaseItemKind.MusicAlbum],
                Recursive = true,
            };
            ApplyFolderScoping(allQuery, folderIds);
            albums = library.GetItemList(allQuery).OfType<MusicAlbum>()
                .Where(a => string.Equals(a.AlbumArtist, artist.Name, StringComparison.OrdinalIgnoreCase)
                         || a.AlbumArtists.Any(n => string.Equals(n, artist.Name, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        return albums;
    }

    private IActionResult GetCoverArt(User user, QueryParams p, string format)
    {
        if (!TryParseItemId(p.Id, format, out var guid, out var err)) return err!;
        if (GetVisibleItem(guid) is not { } item || CoverImageItem(user, item) is not { } cover)
            return ErrorResponse(format, ErrorCode.NotFound, "Cover art not found");

        // Jellyfin's image endpoint does the scaling; "size" bounds the longest edge.
        var size = p.GetInt("size", 0);
        var scale = size > 0 ? $"?maxWidth={size}&maxHeight={size}" : "";
        return Redirect($"{Request.PathBase}/Items/{cover.Id:N}/Images/Primary{scale}");
    }

    /// <summary>
    /// The item whose main image shows for this one: itself if it has one. Otherwise an artist uses its
    /// other Jellyfin entry with the same name (Jellyfin keeps one for the library folder and one for
    /// downloaded artist metadata), then one of its album covers; an album uses a song's embedded art;
    /// a song uses its album's cover. Null if there's none.
    /// </summary>
    private BaseItem? CoverImageItem(User user, BaseItem item)
    {
        if (item.HasImage(ImageType.Primary)) return item;
        var candidates = item switch
        {
            MusicArtist artist => library.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = [BaseItemKind.MusicArtist],
                    Name = artist.Name,
                })
                .Where(a => a.Id != artist.Id)
                .Concat(ArtistAlbums(user, artist).OrderByDescending(a => a.ProductionYear ?? 0)),
            MusicAlbum album => AlbumTracks(user, album.Id),
            Audio song => song.AlbumEntity is { } album ? [album] : [],
            _ => [],
        };
        return candidates.FirstOrDefault(c => c.HasImage(ImageType.Primary) && c.IsVisibleStandalone(user));
    }

    private IActionResult GetAvatar(User user, QueryParams p, string format)
    {
        var target = p.Get("username") is { } name && name != user.Username ? userManager.GetUserByName(name) : user;
        if (target == null) return ErrorResponse(format, ErrorCode.NotFound, "User not found");
        if (target.ProfileImage == null) return ErrorResponse(format, ErrorCode.NotFound, "The user has no avatar");
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
        if (!RefreshInProgress.TryAdd(cacheKey, 0)) return;
        _ = Task.Run(() =>
        {
            try
            {
                var v = build();
                SubsonicStore.SetDerivedCache(cacheKey, serialize(v), null);
                logger.LogInformation("[Subfin] BG cache refresh done: {Key}", cacheKey);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[Subfin] BG cache refresh failed: {Key}", cacheKey);
            }
            finally
            {
                RefreshInProgress.TryRemove(cacheKey, out _);
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
        foreach (var track in library.GetItemList(trackQuery).OfType<Audio>())
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
    private static bool TryParseItemId(string? id, string format, out Guid guid, out IActionResult? error)
    {
        if (string.IsNullOrEmpty(id))
        {
            guid = Guid.Empty;
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
            userData: UserDataFor(s), starredAt: StarredAt(s), relativePath: RelativePath(s.Path), artistIdOf: ArtistIdOf);

    private Dictionary<string, object?> ToAlbumWithArtist(MusicAlbum a) =>
        ItemMapper.ToAlbumShort(a, ResolveArtistTagId(a.AlbumArtist ?? a.AlbumArtists.FirstOrDefault()), UserDataFor(a), StarredAt(a), ArtistIdOf);

    /// <summary>An album for the ID3 endpoints (getArtist, getAlbumList2, search3, getStarred2).</summary>
    private Dictionary<string, object?> ToAlbumId3WithArtist(MusicAlbum a) => ItemMapper.AsAlbumId3(ToAlbumWithArtist(a));

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
        _libraryRoots ??= library.GetVirtualFolders().SelectMany(f => f.Locations).ToList();
        return ItemMapper.RelativeToLibrary(path, _libraryRoots);
    }

    /// <summary>
    /// An item by id, or null when it doesn't exist or the current user may not see it (library
    /// access, parental rating, tags) — the check Jellyfin's own single-item endpoints apply.
    /// Plain GetItemById ignores all of that.
    /// </summary>
    private T? GetVisibleItem<T>(Guid id) where T : BaseItem =>
        _currentUser is { } u && id != Guid.Empty && library.GetItemById<T>(id) is { } item && item.IsVisibleStandalone(u) ? item : null;

    private BaseItem? GetVisibleItem(Guid id) => GetVisibleItem<BaseItem>(id);

    private UserItemData? UserDataFor(BaseItem item) =>
        _currentUser is { } u ? userData.GetUserData(u, item) : null;

    /// <summary>All tracks of an album, including those in disc subfolders (Album/CD 1/...).</summary>
    private List<Audio> AlbumTracks(User user, Guid albumId) =>
        library.GetItemList(new InternalItemsQuery(user)
        {
            ParentId = albumId,
            Recursive = true,
            IncludeItemTypes = [BaseItemKind.Audio],
            OrderBy = [(ItemSortBy.ParentIndexNumber, SortOrder.Ascending), (ItemSortBy.IndexNumber, SortOrder.Ascending)],
        }).OfType<Audio>().ToList();

    /// <summary>Music libraries the user may access in Jellyfin.</summary>
    private List<(string ItemId, string Name)> MusicFoldersFor(User user)
    {
        var visible = library.GetUserRootFolder().GetChildren(user, true).Select(f => f.Id).ToHashSet();
        return library.GetVirtualFolders()
            .Where(f => f.CollectionType == CollectionTypeOptions.music
                        && Guid.TryParse(f.ItemId, out var id) && visible.Contains(id))
            .Select(f => (f.ItemId, f.Name ?? ""))
            .ToList();
    }

    // Scope for a musicFolderId that doesn't name an accessible library: matches nothing.
    private static readonly List<string> NoFolders = [Guid.Empty.ToString("N")];

    /// <summary>The libraries a request is scoped to; null means every library the user can see.</summary>
    private List<string>? GetEffectiveFolderIds(string? clientParam)
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
        return null;
    }
}

/// <summary>Thin wrapper over IQueryCollection for convenient param extraction.</summary>
public class QueryParams(IQueryCollection q)
{
    public string? Id => Get("id") ?? Get("playlistId");
    public string? MusicFolderId => Get("musicFolderId");
    public string? Format => Get("format");
    public int MaxBitRate => GetInt("maxBitRate", 0) > 0 ? GetInt("maxBitRate", 0) : GetInt("bitRate", 0);
    public int TimeOffset => GetInt("timeOffset", 0);
    public string? Get(string key) { var v = q.First(key); return string.IsNullOrEmpty(v) ? null : v; }
    /// <summary>Whether the parameter was sent at all, even empty.</summary>
    public bool Has(string key) => q.ContainsKey(key);
    public int GetInt(string key, int def) => int.TryParse(q.First(key), out var v) ? v : def;
    public long GetLong(string key, long def) => long.TryParse(q.First(key), out var v) ? v : def;
}

public static class QueryCollectionExtensions
{
    /// <summary>
    /// A single-valued parameter: its first value, or "" (a repeated key would otherwise read as
    /// its values joined with commas).
    /// </summary>
    public static string First(this IQueryCollection q, string key) => q[key] is { Count: > 0 } v ? v[0] ?? "" : "";
}
