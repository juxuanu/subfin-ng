using System.Text.RegularExpressions;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;

namespace Jellyfin.Plugin.Subsonic.Mappers;

/// <summary>
/// Library query helpers for the OpenSubsonic controller: expanding share items
/// and resolving artists (special-character-safe tag entity IDs).
/// </summary>
public static partial class LibraryQueries
{
    /// <summary>
    /// Expands a list of Subsonic IDs (artist / al- album / pl- playlist / bare track)
    /// into a de-duplicated flat list of audio track GUIDs (no-dash "N" format).
    /// </summary>
    public static List<string> ExpandShareIds(ILibraryManager library, User user, IEnumerable<string> ids)
    {
        var seen = new HashSet<string>();
        var flatIds = new List<string>();
        foreach (var id in ids)
        {
            if (string.IsNullOrEmpty(id)) continue;

            if (id.StartsWith("al-", StringComparison.Ordinal))
            {
                if (!Guid.TryParse(id.AsSpan(3), out var albumGuid)) continue;
                AddAlbumTracks(library, user, albumGuid, seen, flatIds);
            }
            else if (id.StartsWith("pl-", StringComparison.Ordinal))
            {
                if (!Guid.TryParse(id.AsSpan(3), out var plGuid)) continue;
                var pl = library.GetItemById<Playlist>(plGuid);
                if (pl == null || !pl.IsVisible(user)) continue;
                // Resolved by Jellyfin; entries whose item no longer exists are skipped
                foreach (var (_, entry) in pl.GetManageableItems())
                {
                    if (entry is not Audio || !entry.IsVisibleStandalone(user)) continue;
                    var tId = entry.Id.ToString("N");
                    if (seen.Add(tId)) flatIds.Add(tId);
                }
            }
            else
            {
                // Bare GUID (or ar-/al- prefixed) — resolve item type to handle artist, album, or track
                if (!Guid.TryParse(ItemMapper.StripPrefix(id), out var guid) || guid == Guid.Empty) continue;
                var item = library.GetItemById(guid);
                if (item == null || !item.IsVisibleStandalone(user)) continue;
                if (item is MusicArtist artistItem)
                {
                    var albums = library.GetItemList(new InternalItemsQuery(user)
                    {
                        IncludeItemTypes = [BaseItemKind.MusicAlbum],
                        AlbumArtistIds = [artistItem.Id],
                        Recursive = true,
                    }).OfType<MusicAlbum>().ToList();
                    foreach (var album in albums)
                        AddAlbumTracks(library, user, album.Id, seen, flatIds);
                }
                else if (item is MusicAlbum albumItem)
                {
                    AddAlbumTracks(library, user, albumItem.Id, seen, flatIds);
                }
                else if (item is Audio)
                {
                    var tId = guid.ToString("N");
                    if (seen.Add(tId)) flatIds.Add(tId);
                }
            }
        }
        return flatIds;
    }

    private static void AddAlbumTracks(ILibraryManager library, User user, Guid albumGuid, HashSet<string> seen, List<string> flatIds)
    {
        // Recursive: tracks may sit in disc subfolders (Album/CD 1/...)
        var tracks = library.GetItemList(new InternalItemsQuery(user)
        {
            ParentId = albumGuid,
            Recursive = true,
            IncludeItemTypes = [BaseItemKind.Audio],
            OrderBy = [(ItemSortBy.ParentIndexNumber, SortOrder.Ascending), (ItemSortBy.IndexNumber, SortOrder.Ascending)],
        }).OfType<Audio>().ToList();
        foreach (var t in tracks)
            if (seen.Add(t.Id.ToString("N"))) flatIds.Add(t.Id.ToString("N"));
    }

    /// <summary>
    /// Builds the artist index (artist entity IDs + display names + album counts) from
    /// albums in scope. IDs come from <see cref="ResolveArtists"/>, as the artist ids on songs and albums do.
    /// </summary>
    public static List<(string Id, string Name, int AlbumCount)> BuildArtistList(ILibraryManager library, User user, List<string>? folderIds)
    {
        var albumQuery = new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.MusicAlbum],
            Recursive = true,
        };
        if (folderIds != null)
            albumQuery.AncestorIds = folderIds.Select(Guid.Parse).ToArray();

        var allAlbums = library.GetItemList(albumQuery).OfType<MusicAlbum>();

        var byKey = new Dictionary<string, (string Name, int Count)>(StringComparer.OrdinalIgnoreCase);
        foreach (var album in allAlbums)
        {
            foreach (var key in AlbumArtistKeys(album))
            {
                byKey[key.Key] = byKey.TryGetValue(key.Key, out var existing) ? existing with { Count = existing.Count + 1 } : (key.Name, 1);
            }
        }

        var entities = ResolveArtists(library, byKey.Values.Select(v => v.Name).ToList());
        return byKey.Values.Select(v => (entities[v.Name].Id.ToString("N"), v.Name, v.Count)).OrderBy(v => v.Name).ToList();
    }

    /// <summary>
    /// How many albums each of these artists has, counted as <see cref="BuildArtistList"/> counts them.
    /// One query for the albums of all of them, not a scan of every album as the full list needs.
    /// </summary>
    public static Dictionary<Guid, int> AlbumCounts(ILibraryManager library, User user, IReadOnlyCollection<MusicArtist> artists, List<string>? folderIds)
    {
        if (artists.Count == 0) return [];
        var albumQuery = new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.MusicAlbum],
            AlbumArtistIds = artists.Select(a => a.Id).ToArray(),
            Recursive = true,
        };
        if (folderIds != null)
            albumQuery.AncestorIds = folderIds.Select(Guid.Parse).ToArray();

        var byKey = library.GetItemList(albumQuery).OfType<MusicAlbum>()
            .SelectMany(AlbumArtistKeys).CountBy(k => k.Key).ToDictionary();
        return artists.ToDictionary(a => a.Id, a => byKey.GetValueOrDefault(CanonicalArtistKey(a.Name ?? "")));
    }

    // Every album artist, not only the first: songs and albums list them all (OpenSubsonic
    // "artists"), and clients open those artists from the getArtists list. Once each.
    private static IEnumerable<(string Name, string Key)> AlbumArtistKeys(MusicAlbum album)
    {
        var names = album.AlbumArtists.Count > 0 ? album.AlbumArtists : album.AlbumArtist is { Length: > 0 } one ? [one] : [];
        return names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => (Name: n, Key: CanonicalArtistKey(n))).DistinctBy(n => n.Key);
    }

    /// <summary>
    /// The artist entity for each name, whose id clients get: the artist's folder when it has one.
    /// One lookup for all names (one per name took seconds on large libraries); GetArtist(name) only
    /// for names without an entity, as it creates one. GetArtist can't do it all: it matches names
    /// lowercased by SQLite, which leaves non-ASCII capitals, so for "Björk Ñandú" it misses the
    /// folder's entity and creates a second one.
    /// </summary>
    public static Dictionary<string, MusicArtist> ResolveArtists(ILibraryManager library, IReadOnlyList<string> names)
    {
        var found = library.GetArtists(names);  // by cleaned name
        return names.Distinct().ToDictionary(n => n, n => found.GetValueOrDefault(n, [])
            .Where(a => string.Equals(a.Name, n, StringComparison.OrdinalIgnoreCase))
            .OrderBy(a => a.IsAccessedByName ? 1 : 0)
            .FirstOrDefault() ?? library.GetArtist(n));
    }

    [GeneratedRegex(@"[\s._/*'""\-]+")]
    private static partial Regex ArtistKeySeparators();

    internal static string CanonicalArtistKey(string name) => ArtistKeySeparators().Replace(name.ToLowerInvariant(), "");
}
