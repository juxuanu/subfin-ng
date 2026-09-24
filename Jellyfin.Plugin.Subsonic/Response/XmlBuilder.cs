using System.Globalization;
using System.Text;
using System.Xml;
using Jellyfin.Plugin.Subsonic.Mappers;

namespace Jellyfin.Plugin.Subsonic.Response;

/// <summary>
/// Builds attribute-style Subsonic XML responses.
/// Subsonic XML uses attributes on elements, never child elements for scalar values.
/// </summary>
public static class XmlBuilder
{
    private const string Ns = "http://subsonic.org/restapi";

    /// <summary>Wrap a payload-building action in a subsonic-response envelope.</summary>
    public static string OkEnvelope(Action<XmlWriter> writePayload)
    {
        var sb = new StringBuilder();
        var settings = new XmlWriterSettings { Encoding = Encoding.UTF8, Indent = false, OmitXmlDeclaration = true };
        using var w = XmlWriter.Create(sb, settings);
        w.WriteStartElement("subsonic-response", Ns);
        w.WriteAttributeString("status", "ok");
        w.WriteAttributeString("version", SubsonicConstants.Version);
        w.WriteAttributeString("type", SubsonicConstants.ServerType);
        w.WriteAttributeString("serverVersion", SubsonicConstants.ServerVersion);
        w.WriteAttributeString("openSubsonic", "true");
        writePayload(w);
        w.WriteEndElement();
        w.Flush();
        return sb.ToString();
    }

    /// <summary>Error envelope.</summary>
    public static string ErrorEnvelope(int code, string message, string? helpUrl = null)
    {
        var sb = new StringBuilder();
        var settings = new XmlWriterSettings { Encoding = Encoding.UTF8, Indent = false, OmitXmlDeclaration = true };
        using var w = XmlWriter.Create(sb, settings);
        w.WriteStartElement("subsonic-response", Ns);
        w.WriteAttributeString("status", "failed");
        w.WriteAttributeString("version", SubsonicConstants.Version);
        w.WriteAttributeString("type", SubsonicConstants.ServerType);
        w.WriteAttributeString("serverVersion", SubsonicConstants.ServerVersion);
        w.WriteAttributeString("openSubsonic", "true");
        w.WriteStartElement("error", Ns);
        w.WriteAttributeString("code", code.ToString(CultureInfo.InvariantCulture));
        w.WriteAttributeString("message", message);
        if (helpUrl != null) w.WriteAttributeString("helpUrl", helpUrl);
        w.WriteEndElement();
        w.WriteEndElement();
        w.Flush();
        return sb.ToString();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    // Strips characters that are illegal in XML 1.0 attribute values (control chars except tab/LF/CR).
    // Music file tags sometimes contain these characters and will cause XmlWriter to throw.
    private static string SanitizeXmlString(string s) =>
        string.IsNullOrEmpty(s) ? s :
        new string(s.Where(c => c == '\t' || c == '\n' || c == '\r'
                              || (c >= '\x20' && c != '\uFFFE' && c != '\uFFFF')).ToArray());

    public static void WriteAttr(XmlWriter w, string name, object? value)
    {
        if (value == null) return;
        w.WriteAttributeString(name, value switch
        {
            bool b => b ? "true" : "false",
            int i => i.ToString(CultureInfo.InvariantCulture),
            long l => l.ToString(CultureInfo.InvariantCulture),
            double d => d.ToString(CultureInfo.InvariantCulture),
            _ => SanitizeXmlString(value.ToString() ?? ""),
        });
    }

    public static void WriteDictAsElement(XmlWriter w, string elementName, Dictionary<string, object?> d)
    {
        w.WriteStartElement(elementName, Ns);
        foreach (var kv in d)
        {
            if (kv.Value is List<Dictionary<string, object?>>)
            {
                // child element list (e.g. song[] inside album)
                // determined by context; skip at this level — handled by callers
                continue;
            }
            WriteAttr(w, kv.Key, kv.Value);
        }
        w.WriteEndElement();
    }

    // ── Ping / License / ScanStatus / User ──────────────────────────────────

    public static string Ping() => OkEnvelope(_ => { });

    public static string ScanStatus() => OkEnvelope(w =>
    {
        w.WriteStartElement("scanStatus", Ns);
        w.WriteAttributeString("scanning", "false");
        w.WriteAttributeString("count", "0");
        w.WriteEndElement();
    });

    public static string User(Dictionary<string, object> user) => OkEnvelope(w => WriteUser(w, user));

    public static string Users(List<Dictionary<string, object>> users) => OkEnvelope(w =>
    {
        w.WriteStartElement("users", Ns);
        foreach (var u in users) WriteUser(w, u);
        w.WriteEndElement();
    });

    private static void WriteUser(XmlWriter w, Dictionary<string, object> user)
    {
        w.WriteStartElement("user", Ns);
        foreach (var kv in user)
            if (kv.Key != "folder") WriteAttr(w, kv.Key, kv.Value);
        if (user.TryGetValue("folder", out var folders) && folders is IEnumerable<int> ids)
            foreach (var id in ids) w.WriteElementString("folder", Ns, id.ToString(CultureInfo.InvariantCulture));
        w.WriteEndElement();
    }

    public static string License() => OkEnvelope(w =>
    {
        w.WriteStartElement("license", Ns);
        w.WriteAttributeString("valid", "true");
        w.WriteAttributeString("email", "");
        w.WriteAttributeString("licenseExpires", "2099-01-01T00:00:00.000Z");
        w.WriteEndElement();
    });

    // One <openSubsonicExtensions name="…"> per extension, its versions as <versions> elements (the JSON's array)
    public static string OpenSubsonicExtensions() => OkEnvelope(w =>
    {
        foreach (var (name, versions) in SubsonicConstants.Extensions)
        {
            w.WriteStartElement("openSubsonicExtensions", Ns);
            w.WriteAttributeString("name", name);
            foreach (var v in versions)
                w.WriteElementString("versions", Ns, v.ToString(CultureInfo.InvariantCulture));
            w.WriteEndElement();
        }
    });

    // ── Music Folders ────────────────────────────────────────────────────────

    public static string MusicFolders(List<(string Id, string Name)> folders) => OkEnvelope(w =>
    {
        w.WriteStartElement("musicFolders", Ns);
        foreach (var (id, name) in folders)
        {
            w.WriteStartElement("musicFolder", Ns);
            w.WriteAttributeString("id", id);
            w.WriteAttributeString("name", name);
            w.WriteEndElement();
        }
        w.WriteEndElement();
    });

    // ── Artists / Indexes ────────────────────────────────────────────────────

    public static string Artists(List<(string Letter, List<(string Id, string Name, int AlbumCount)> Artists)> index, string ignoredArticles = "The An A Die Das Ein Eine Les Le La") => OkEnvelope(w =>
    {
        w.WriteStartElement("artists", Ns);
        w.WriteAttributeString("ignoredArticles", ignoredArticles);
        foreach (var (letter, artists) in index)
        {
            w.WriteStartElement("index", Ns);
            w.WriteAttributeString("name", letter);
            foreach (var (id, name, albumCount) in artists)
            {
                w.WriteStartElement("artist", Ns);
                w.WriteAttributeString("id", id);
                w.WriteAttributeString("name", name);
                w.WriteAttributeString("coverArt", $"ar-{id}");
                w.WriteAttributeString("albumCount", albumCount.ToString(CultureInfo.InvariantCulture));
                w.WriteEndElement();
            }
            w.WriteEndElement();
        }
        w.WriteEndElement();
    });

    // Same structure as Artists but uses <indexes> root
    public static string Indexes(List<(string Letter, List<(string Id, string Name, int AlbumCount)> Artists)> index, string ignoredArticles = "The An A Die Das Ein Eine Les Le La", long lastModified = 0) => OkEnvelope(w =>
    {
        w.WriteStartElement("indexes", Ns);
        w.WriteAttributeString("lastModified", lastModified.ToString(CultureInfo.InvariantCulture));
        w.WriteAttributeString("ignoredArticles", ignoredArticles);
        foreach (var (letter, artists) in index)
        {
            w.WriteStartElement("index", Ns);
            w.WriteAttributeString("name", letter);
            foreach (var (id, name, albumCount) in artists)
            {
                w.WriteStartElement("artist", Ns);
                w.WriteAttributeString("id", id);
                w.WriteAttributeString("name", name);
                w.WriteAttributeString("coverArt", $"ar-{id}");
                w.WriteAttributeString("albumCount", albumCount.ToString(CultureInfo.InvariantCulture));
                w.WriteEndElement();
            }
            w.WriteEndElement();
        }
        w.WriteEndElement();
    });

    // ── Artist with albums ───────────────────────────────────────────────────

    public static string Artist(Dictionary<string, object?> artist) => OkEnvelope(w =>
    {
        w.WriteStartElement("artist", Ns);
        WriteAttrsThenChildren(w, artist, "album", WriteAlbumId3Content);
        w.WriteEndElement();
    });

    // ── Album with songs ─────────────────────────────────────────────────────

    public static string Album(Dictionary<string, object?> album) => OkEnvelope(w =>
    {
        w.WriteStartElement("album", Ns);
        WriteAttrsThenChildren(w, album, "song", WriteSongContent);
        w.WriteEndElement();
    });

    /// <summary>
    /// Writes an element's attributes, then its artist lists and its <paramref name="childKey"/> list as
    /// child elements. XML allows no attribute after the first child, and fields such as a user's starred
    /// or played date come after the lists in the dictionary.
    /// </summary>
    private static void WriteAttrsThenChildren(XmlWriter w, Dictionary<string, object?> d, string childKey,
        Action<XmlWriter, Dictionary<string, object?>> writeChild)
    {
        foreach (var kv in d)
            if (kv.Value is not List<Dictionary<string, object?>>) WriteAttr(w, kv.Key, kv.Value);
        WriteArtistLists(w, d);
        if (d.TryGetValue(childKey, out var value) && value is List<Dictionary<string, object?>> children)
        {
            foreach (var child in children)
            {
                w.WriteStartElement(childKey, Ns);
                writeChild(w, child);
                w.WriteEndElement();
            }
        }
    }

    // ── Song ─────────────────────────────────────────────────────────────────

    public static string Song(Dictionary<string, object?> song) => OkEnvelope(w =>
    {
        w.WriteStartElement("song", Ns);
        WriteSongContent(w, song);
        w.WriteEndElement();
    });

    // ── Directory ────────────────────────────────────────────────────────────

    public static string MusicDirectory(string id, string name, string? parent, IEnumerable<Dictionary<string, object?>> children) => OkEnvelope(w =>
    {
        w.WriteStartElement("directory", Ns);
        w.WriteAttributeString("id", id);
        w.WriteAttributeString("name", name);
        if (parent != null) w.WriteAttributeString("parent", parent);
        foreach (var child in children)
        {
            var isDir = child.TryGetValue("isDir", out var isd) && isd is bool b && b;
            w.WriteStartElement("child", Ns);
            if (isDir) WriteAlbumShortContent(w, child);
            else WriteSongContent(w, child);
            w.WriteEndElement();
        }
        w.WriteEndElement();
    });

    // ── Search ───────────────────────────────────────────────────────────────

    public static string SearchResult3(
        List<Dictionary<string, object?>> artists,
        List<Dictionary<string, object?>> albums,
        List<Dictionary<string, object?>> songs,
        string element = "searchResult3") => OkEnvelope(w =>
    {
        w.WriteStartElement(element, Ns);
        foreach (var a in artists) { w.WriteStartElement("artist", Ns); WriteAlbumShortContent(w, a); w.WriteEndElement(); }
        foreach (var a in albums)
        {
            w.WriteStartElement("album", Ns);
            if (element == "searchResult2") WriteAlbumShortContent(w, a); else WriteAlbumId3Content(w, a);  // Child vs AlbumID3
            w.WriteEndElement();
        }
        foreach (var s in songs) { w.WriteStartElement("song", Ns); WriteSongContent(w, s); w.WriteEndElement(); }
        w.WriteEndElement();
    });

    // ── Album lists ──────────────────────────────────────────────────────────

    public static string AlbumList(List<Dictionary<string, object?>> albums, bool list2 = false) => OkEnvelope(w =>
    {
        w.WriteStartElement(list2 ? "albumList2" : "albumList", Ns);
        foreach (var album in albums) { w.WriteStartElement("album", Ns); if (list2) WriteAlbumId3Content(w, album); else WriteAlbumShortContent(w, album); w.WriteEndElement(); }
        w.WriteEndElement();
    });

    public static string RandomSongs(List<Dictionary<string, object?>> songs) => OkEnvelope(w =>
    {
        w.WriteStartElement("randomSongs", Ns);
        foreach (var s in songs) { w.WriteStartElement("song", Ns); WriteSongContent(w, s); w.WriteEndElement(); }
        w.WriteEndElement();
    });

    public static string SongsByGenre(List<Dictionary<string, object?>> songs) => OkEnvelope(w =>
    {
        w.WriteStartElement("songsByGenre", Ns);
        foreach (var s in songs) { w.WriteStartElement("song", Ns); WriteSongContent(w, s); w.WriteEndElement(); }
        w.WriteEndElement();
    });

    public static string TopSongs(List<Dictionary<string, object?>> songs) => OkEnvelope(w =>
    {
        w.WriteStartElement("topSongs", Ns);
        foreach (var s in songs) { w.WriteStartElement("song", Ns); WriteSongContent(w, s); w.WriteEndElement(); }
        w.WriteEndElement();
    });

    public static string SimilarSongs(List<Dictionary<string, object?>> songs, bool v2 = false) => OkEnvelope(w =>
    {
        w.WriteStartElement(v2 ? "similarSongs2" : "similarSongs", Ns);
        foreach (var s in songs) { w.WriteStartElement("song", Ns); WriteSongContent(w, s); w.WriteEndElement(); }
        w.WriteEndElement();
    });

    // ── Genres ───────────────────────────────────────────────────────────────

    public static string Genres(List<(string Name, int SongCount, int AlbumCount)> genres) => OkEnvelope(w =>
    {
        w.WriteStartElement("genres", Ns);
        foreach (var (name, songCount, albumCount) in genres)
        {
            w.WriteStartElement("genre", Ns);
            w.WriteAttributeString("songCount", songCount.ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("albumCount", albumCount.ToString(CultureInfo.InvariantCulture));
            w.WriteString(name);  // the JSON's "value": the element's text
            w.WriteEndElement();
        }
        w.WriteEndElement();
    });

    // ── Playlists ────────────────────────────────────────────────────────────

    public static string Playlists(List<Dictionary<string, object?>> playlists) => OkEnvelope(w =>
    {
        w.WriteStartElement("playlists", Ns);
        foreach (var pl in playlists) { w.WriteStartElement("playlist", Ns); WritePlaylistAttrs(w, pl, false); w.WriteEndElement(); }
        w.WriteEndElement();
    });

    public static string Playlist(Dictionary<string, object?> playlist) => OkEnvelope(w =>
    {
        w.WriteStartElement("playlist", Ns);
        WritePlaylistAttrs(w, playlist, true);
        w.WriteEndElement();
    });

    // ── Play queue ───────────────────────────────────────────────────────────

    public static string PlayQueue(string? currentId, int currentIndex, long positionMs, string? changedAt, string changedBy, List<Dictionary<string, object?>> songs, string username = "") => OkEnvelope(w =>
    {
        w.WriteStartElement("playQueue", Ns);
        if (currentId != null) w.WriteAttributeString("current", currentId);
        w.WriteAttributeString("position", positionMs.ToString(CultureInfo.InvariantCulture));
        w.WriteAttributeString("username", username);
        if (changedAt != null) w.WriteAttributeString("changed", changedAt);
        w.WriteAttributeString("changedBy", changedBy);
        foreach (var s in songs) { w.WriteStartElement("entry", Ns); WriteSongContent(w, s); w.WriteEndElement(); }
        w.WriteEndElement();
    });

    // ── Starred ──────────────────────────────────────────────────────────────

    public static string Starred(List<Dictionary<string, object?>> artists, List<Dictionary<string, object?>> albums, List<Dictionary<string, object?>> songs, bool v2 = false) => OkEnvelope(w =>
    {
        w.WriteStartElement(v2 ? "starred2" : "starred", Ns);
        foreach (var a in artists) { w.WriteStartElement("artist", Ns); foreach (var kv in a) WriteAttr(w, kv.Key, kv.Value); w.WriteEndElement(); }
        foreach (var a in albums) { w.WriteStartElement("album", Ns); if (v2) WriteAlbumId3Content(w, a); else WriteAlbumShortContent(w, a); w.WriteEndElement(); }
        foreach (var s in songs) { w.WriteStartElement("song", Ns); WriteSongContent(w, s); w.WriteEndElement(); }
        w.WriteEndElement();
    });

    // ── Shares ───────────────────────────────────────────────────────────────

    public static string Shares(List<ShareXml> shares) => OkEnvelope(w =>
    {
        w.WriteStartElement("shares", Ns);
        foreach (var share in shares) WriteShare(w, share);
        w.WriteEndElement();
    });

    public static string ShareCreated(ShareXml share) => OkEnvelope(w =>
    {
        w.WriteStartElement("shares", Ns);
        WriteShare(w, share);
        w.WriteEndElement();
    });

    private static void WriteShare(XmlWriter w, ShareXml s)
    {
        w.WriteStartElement("share", Ns);
        w.WriteAttributeString("id", s.Id);
        w.WriteAttributeString("url", s.Url);
        if (s.Description != null) w.WriteAttributeString("description", s.Description);
        w.WriteAttributeString("username", s.Username);
        w.WriteAttributeString("created", s.Created);
        w.WriteAttributeString("expires", s.Expires);
        w.WriteAttributeString("visitCount", s.VisitCount.ToString(CultureInfo.InvariantCulture));
        w.WriteAttributeString("songCount", s.Songs.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var song in s.Songs) { w.WriteStartElement("entry", Ns); WriteSongContent(w, song); w.WriteEndElement(); }
        w.WriteEndElement();
    }

    // ── ArtistInfo ───────────────────────────────────────────────────────────

    public static string ArtistInfo(string? biography, string? musicBrainzId, string? artistImageUrl, List<Dictionary<string, object?>> similarArtists, bool v2 = false) => OkEnvelope(w =>
    {
        var imageUrl = artistImageUrl ?? "";
        w.WriteStartElement(v2 ? "artistInfo2" : "artistInfo", Ns);
        // Text element children, as Subsonic defines them — DSub2000's ArtistInfoParser reads these as text elements
        if (!string.IsNullOrEmpty(biography)) { w.WriteStartElement("biography", Ns); w.WriteString(biography); w.WriteEndElement(); }
        if (!string.IsNullOrEmpty(musicBrainzId)) { w.WriteStartElement("musicBrainzId", Ns); w.WriteString(musicBrainzId); w.WriteEndElement(); }
        { w.WriteStartElement("smallImageUrl", Ns); w.WriteString(imageUrl); w.WriteEndElement(); }
        { w.WriteStartElement("mediumImageUrl", Ns); w.WriteString(imageUrl); w.WriteEndElement(); }
        { w.WriteStartElement("largeImageUrl", Ns); w.WriteString(imageUrl); w.WriteEndElement(); }
        foreach (var sa in similarArtists) { w.WriteStartElement("similarArtist", Ns); foreach (var kv in sa) WriteAttr(w, kv.Key, kv.Value); w.WriteEndElement(); }
        w.WriteEndElement();
    });

    // ── AlbumInfo ────────────────────────────────────────────────────────────

    public static string AlbumInfo(string? notes, string? musicBrainzId) => OkEnvelope(w =>
    {
        w.WriteStartElement("albumInfo", Ns);  // getAlbumInfo and getAlbumInfo2 both return <albumInfo>
        if (!string.IsNullOrEmpty(musicBrainzId)) w.WriteAttributeString("musicBrainzId", musicBrainzId);
        if (!string.IsNullOrEmpty(notes)) { w.WriteStartElement("notes", Ns); w.WriteString(notes); w.WriteEndElement(); }
        w.WriteEndElement();
    });

    // ── NowPlaying ───────────────────────────────────────────────────────────

    public static string NowPlaying(List<NowPlayingXml> entries) => OkEnvelope(w =>
    {
        w.WriteStartElement("nowPlaying", Ns);
        foreach (var e in entries)
        {
            w.WriteStartElement("entry", Ns);
            // Before the song: its artist lists are child elements, and no attribute may follow them
            w.WriteAttributeString("username", e.Username);
            w.WriteAttributeString("minutesAgo", e.MinutesAgo.ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("playerId", e.PlayerId.ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("playerName", e.PlayerName);
            WriteSongContent(w, e.Song);
            w.WriteEndElement();
        }
        w.WriteEndElement();
    });

    // ── Private content writers: attributes, then the artist lists as child elements ──

    private static void WriteSongContent(XmlWriter w, Dictionary<string, object?> song)
    {
        foreach (var kv in song)
        {
            if (kv.Value is List<Dictionary<string, object?>>) continue;
            WriteAttr(w, kv.Key, kv.Value);
        }
        WriteArtistLists(w, song);
    }

    private static void WriteAlbumShortContent(XmlWriter w, Dictionary<string, object?> album)
    {
        foreach (var kv in album)
        {
            if (kv.Value is List<Dictionary<string, object?>>) continue;
            WriteAttr(w, kv.Key, kv.Value);
        }
        WriteArtistLists(w, album);
    }

    private static void WriteAlbumId3Content(XmlWriter w, Dictionary<string, object?> album)
    {
        foreach (var kv in ItemMapper.AsAlbumId3(album))
        {
            if (kv.Value is List<Dictionary<string, object?>>) continue;
            WriteAttr(w, kv.Key, kv.Value);
        }
        WriteArtistLists(w, album);
    }

    /// <summary>OpenSubsonic's artist lists, as child elements: &lt;artists id="…" name="…"/&gt;.</summary>
    private static void WriteArtistLists(XmlWriter w, Dictionary<string, object?> item)
    {
        foreach (var key in (string[])["artists", "albumArtists"])
        {
            if (!item.TryGetValue(key, out var value) || value is not List<Dictionary<string, object?>> artists) continue;
            foreach (var artist in artists)
            {
                w.WriteStartElement(key, Ns);
                foreach (var kv in artist) WriteAttr(w, kv.Key, kv.Value);
                w.WriteEndElement();
            }
        }
    }

    private static void WritePlaylistAttrs(XmlWriter w, Dictionary<string, object?> pl, bool includeSongs)
    {
        foreach (var kv in pl)
        {
            if (kv.Key == "entry" && kv.Value is List<Dictionary<string, object?>> songs)
            {
                if (includeSongs)
                    foreach (var s in songs) { w.WriteStartElement("entry", Ns); WriteSongContent(w, s); w.WriteEndElement(); }
            }
            else WriteAttr(w, kv.Key, kv.Value);
        }
    }
}

// Supporting types for XML builder
public record ShareXml(string Id, string Url, string? Description, string Username, string Created, string Expires, int VisitCount, List<Dictionary<string, object?>> Songs);
public record NowPlayingXml(Dictionary<string, object?> Song, string Username, int MinutesAgo, int PlayerId, string PlayerName);
