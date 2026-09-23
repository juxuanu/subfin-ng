using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;

namespace Jellyfin.Plugin.Subsonic.Mappers;

/// <summary>Maps Jellyfin entities to OpenSubsonic response shapes (Dictionary for flexible JSON/XML).</summary>
public static class ItemMapper
{
    private const string IgnoredArticles = "The An A Die Das Ein Eine Les Le La";
    private const long TicksPerSecond = 10_000_000L;

    public static int TicksToSeconds(long? ticks) =>
        ticks.HasValue ? (int)(ticks.Value / TicksPerSecond) : 0;

    public static string AudioMimeType(string? container) => container?.ToLowerInvariant() switch
    {
        "mp3" => "audio/mpeg",
        "flac" => "audio/flac",
        "ogg" or "oga" => "audio/ogg",
        "opus" => "audio/ogg; codecs=opus",
        "aac" or "m4a" => "audio/aac",
        "wav" => "audio/wav",
        "wma" => "audio/x-ms-wma",
        _ => "application/octet-stream",
    };

    public static string IndexLetter(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "#";
        var first = char.ToUpperInvariant(name.TrimStart()[0]);
        return char.IsAsciiLetterOrDigit(first) ? first.ToString() : "#";
    }

    public static string StripPrefix(string id) =>
        id.StartsWith("ar-", StringComparison.OrdinalIgnoreCase) ? id[3..] :
        id.StartsWith("al-", StringComparison.OrdinalIgnoreCase) ? id[3..] :
        id.StartsWith("pl-", StringComparison.OrdinalIgnoreCase) ? id[3..] :
        id;

    /// <summary>
    /// A file's path inside the library folder containing it ("Artist/Album/01 Song.flac"), or just
    /// its file name when no library folder matches — never the server's filesystem layout.
    /// </summary>
    public static string RelativeToLibrary(string? path, IEnumerable<string> libraryRoots)
    {
        if (string.IsNullOrEmpty(path)) return "";
        var root = libraryRoots
            .Select(r => r.TrimEnd('/', '\\'))
            .Where(r => r.Length > 0 && path.Length > r.Length && path.StartsWith(r, StringComparison.Ordinal) && path[r.Length] is '/' or '\\')
            .MaxBy(r => r.Length);
        return (root == null ? System.IO.Path.GetFileName(path) : path[(root.Length + 1)..]).Replace('\\', '/');
    }

    // ── Artist ───────────────────────────────────────────────────────────────

    public static Dictionary<string, object?> ToArtist(MusicArtist artist, int albumCount = 0) => new()
    {
        ["id"] = artist.Id.ToString("N"),
        ["name"] = artist.Name ?? "",
        ["coverArt"] = $"ar-{artist.Id:N}",
        ["albumCount"] = albumCount,
    };

    /// <param name="mapAlbum">Maps each album (with the caller's per-user data).</param>
    public static Dictionary<string, object?> ToArtistWithAlbums(MusicArtist artist, IEnumerable<MusicAlbum> albums, Func<MusicAlbum, Dictionary<string, object?>> mapAlbum)
    {
        var albumList = albums.ToList();
        return new()
        {
            ["id"] = artist.Id.ToString("N"),
            ["name"] = artist.Name ?? "",
            ["coverArt"] = $"ar-{artist.Id:N}",
            ["albumCount"] = albumList.Count,
            ["album"] = albumList.Select(mapAlbum).ToList(),
        };
    }

    // ── Album ────────────────────────────────────────────────────────────────

    public static Dictionary<string, object?> ToAlbumShort(MusicAlbum album, string? resolvedArtistId = null, UserItemData? userData = null, string? starredAt = null)
    {
        var artistName = album.AlbumArtist ?? album.AlbumArtists.FirstOrDefault() ?? "";
        var result = new Dictionary<string, object?>
        {
            ["id"] = album.Id.ToString("N"),
            ["name"] = album.Name ?? "",
            ["isDir"] = true,
            ["coverArt"] = $"al-{album.Id:N}",
            ["songCount"] = album.Tracks.Count(),
            ["duration"] = TicksToSeconds(album.RunTimeTicks),
            ["playCount"] = 0,
            ["artist"] = artistName,
            ["artistId"] = resolvedArtistId ?? "",
            ["year"] = album.ProductionYear,
            ["genre"] = album.Genres.FirstOrDefault() ?? "",
            ["created"] = (album.DateCreated == default ? DateTimeOffset.UnixEpoch.UtcDateTime : album.DateCreated).ToString("o"),
        };
        AddUserData(result, album, userData, starredAt);
        return result;
    }

    /// <param name="mapSong">Maps each track; songs carry their own track artists, not the album artist.</param>
    public static Dictionary<string, object?> ToAlbum(MusicAlbum album, IEnumerable<Audio> songs, Func<Audio, Dictionary<string, object?>> mapSong, string? resolvedArtistId = null, UserItemData? userData = null, string? starredAt = null)
    {
        var songList = songs.ToList();
        var artistName = album.AlbumArtist ?? album.AlbumArtists.FirstOrDefault() ?? "";
        var result = new Dictionary<string, object?>
        {
            ["id"] = album.Id.ToString("N"),
            ["parent"] = album.ParentId.ToString("N"),
            ["album"] = album.Name ?? "",
            ["title"] = album.Name ?? "",
            ["name"] = album.Name ?? "",
            ["isDir"] = true,
            ["coverArt"] = $"al-{album.Id:N}",
            ["songCount"] = songList.Count,
            ["created"] = (album.DateCreated == default ? DateTimeOffset.UnixEpoch.UtcDateTime : album.DateCreated).ToString("o"),
            ["duration"] = songList.Sum(s => TicksToSeconds(s.RunTimeTicks)),
            ["playCount"] = 0,
            ["artistId"] = resolvedArtistId ?? "",
            ["artist"] = artistName,
            ["year"] = album.ProductionYear,
            ["genre"] = album.Genres.FirstOrDefault() ?? "",
            ["song"] = songList.Select(mapSong).ToList(),
        };
        AddUserData(result, album, userData, starredAt);
        return result;
    }

    // ── Song ─────────────────────────────────────────────────────────────────

    /// <param name="relativePath">Path inside its library; defaults to the file name (never the server path).</param>
    public static Dictionary<string, object?> ToSong(Audio song, string? albumId = null, string? albumName = null, string? artistId = null,
        UserItemData? userData = null, string? starredAt = null, string? relativePath = null)
    {
        var duration = TicksToSeconds(song.RunTimeTicks);
        var size = song.Size ?? 0L;
        var bitRate = duration > 0 && size > 0 ? (int)((size * 8L) / duration / 1000L) : 0;

        // ParentId is the album only for single-folder albums; tracks in disc subfolders
        // (Album/CD 1/...) have the disc folder as parent, so ask for the album itself.
        var effectiveAlbumId = albumId ?? (song.AlbumEntity?.Id ?? song.ParentId).ToString("N");
        var albumArtist = string.Join(", ", song.AlbumArtists);
        var trackArtist = song.Artists.Count > 0 ? string.Join(", ", song.Artists) : albumArtist;

        // Get audio stream metadata
        var mediaStream = song.GetMediaStreams()
            .FirstOrDefault(s => s.Type == MediaBrowser.Model.Entities.MediaStreamType.Audio);

        var suffix = song.Container?.ToLowerInvariant() ?? "mp3";
        var mimeType = AudioMimeType(song.Container);
        var result = new Dictionary<string, object?>
        {
            ["id"] = song.Id.ToString("N"),
            ["parent"] = effectiveAlbumId,
            ["title"] = song.Name ?? "",
            ["isDir"] = false,
            ["isVideo"] = false,
            ["type"] = "music",
            ["mediaType"] = "song",
            ["albumId"] = effectiveAlbumId,
            ["album"] = albumName ?? song.Album ?? "",
            ["artist"] = trackArtist,
            ["artistId"] = artistId ?? "",
            ["displayArtist"] = trackArtist,
            ["displayAlbumArtist"] = albumArtist.Length > 0 ? albumArtist : trackArtist,
            ["coverArt"] = $"al-{effectiveAlbumId}",
            ["duration"] = duration,
            ["bitRate"] = bitRate,
            ["track"] = song.IndexNumber ?? 0,
            ["year"] = song.ProductionYear,
            ["genre"] = song.Genres.FirstOrDefault() ?? "",
            ["size"] = size,
            ["suffix"] = suffix,
            ["contentType"] = mimeType,
            ["transcodedSuffix"] = suffix,
            ["transcodedContentType"] = mimeType,
            ["discNumber"] = song.ParentIndexNumber ?? 1,
            ["path"] = relativePath ?? System.IO.Path.GetFileName(song.Path ?? ""),
            ["bitDepth"] = mediaStream?.BitDepth ?? 16,
            ["samplingRate"] = mediaStream?.SampleRate ?? 44100,
            ["channelCount"] = mediaStream?.Channels ?? 2,
        };
        AddUserData(result, song, userData, starredAt);
        return result;
    }

    /// <summary>Per-user fields (starred, userRating, playCount, played); omitted when unset.</summary>
    /// <param name="starredAt">When it was starred through Subfin; Jellyfin itself keeps no date, so
    /// favourites set elsewhere fall back to the item's creation date.</param>
    private static void AddUserData(Dictionary<string, object?> result, BaseItem item, UserItemData? data, string? starredAt)
    {
        if (data == null) return;
        if (data.IsFavorite) result["starred"] = starredAt ?? item.DateCreated.ToString("o");
        if (data.Rating is >= 1 and <= 5) result["userRating"] = (int)data.Rating.Value;
        if (data.PlayCount > 0) result["playCount"] = data.PlayCount;
        if (data.LastPlayedDate is { } played) result["played"] = played.ToString("o");
    }

    // ── Artist index ─────────────────────────────────────────────────────────

    public static Dictionary<string, object?> ToArtistsIndex(IEnumerable<(string Id, string Name, int AlbumCount)> artists)
    {
        var byLetter = new SortedDictionary<string, List<Dictionary<string, object?>>>();
        foreach (var (id, name, albumCount) in artists)
        {
            var letter = IndexLetter(name);
            if (!byLetter.ContainsKey(letter)) byLetter[letter] = [];
            byLetter[letter].Add(new() { ["id"] = id, ["name"] = name, ["coverArt"] = $"ar-{id}", ["albumCount"] = albumCount });
        }
        return new()
        {
            ["ignoredArticles"] = IgnoredArticles,
            ["index"] = byLetter.Select(kv => new Dictionary<string, object?> { ["name"] = kv.Key, ["artist"] = kv.Value }).ToList(),
        };
    }
}
