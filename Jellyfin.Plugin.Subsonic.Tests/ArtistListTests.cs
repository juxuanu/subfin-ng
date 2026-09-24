using System.Xml;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Subsonic.Mappers;
using Jellyfin.Plugin.Subsonic.Response;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.Subsonic.Tests;

/// <summary>OpenSubsonic artist lists: getArtists lists every album artist, and songs and albums link to them.</summary>
public class ArtistListTests
{
    private const string Ns = "http://subsonic.org/restapi";

    // A folder artist (Music/Artist/...), not one Jellyfin keeps by name only (no parent)
    private static MusicArtist Artist(string name) => new() { Name = name, Id = Guid.NewGuid(), ParentId = Guid.NewGuid() };

    [Fact]
    public void GetArtists_ListsEveryAlbumArtist_WithTheirAlbumCounts()
    {
        var artists = new[] { "Glenn Gould", "Johann Sebastian Bach", "Leonard Bernstein" }.ToDictionary(n => n, Artist);
        var library = Substitute.For<ILibraryManager>();
        library.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem>
        {
            new MusicAlbum { Name = "Goldberg Variations", AlbumArtists = ["Glenn Gould", "Johann Sebastian Bach"] },
            new MusicAlbum { Name = "The Well-Tempered Clavier", AlbumArtists = ["Glenn Gould", "Johann Sebastian Bach"] },
            new MusicAlbum { Name = "Brahms", AlbumArtists = ["Glenn Gould", "Leonard Bernstein", "glenn gould"] },
        });
        // Jellyfin can have a by-name artist beside the folder one; the list uses the folder's, as GetArtist(name) does
        library.GetArtists(Arg.Any<IReadOnlyList<string>>()).Returns(call => (IReadOnlyDictionary<string, MusicArtist[]>)call.Arg<IReadOnlyList<string>>()
            .ToDictionary(n => n, n => new[] { new MusicArtist { Name = n, Id = Guid.NewGuid() }, artists[n] }));

        var list = LibraryQueries.BuildArtistList(library, new User("u", "p", "r"), null);

        Assert.Equal(
            [("Glenn Gould", 3), ("Johann Sebastian Bach", 2), ("Leonard Bernstein", 1)],
            list.Select(a => (a.Name, a.AlbumCount)));
        Assert.All(list, a => Assert.Equal(artists[a.Name].Id.ToString("N"), a.Id));
        library.DidNotReceive().GetArtist(Arg.Any<string>());  // one lookup for all of them
    }

    [Theory]
    [InlineData(false)]  // no entity yet
    [InlineData(true)]   // only one whose name matches once cleaned ("New-Artist"); the batch lookup compares cleaned names
    public void GetArtists_FallsBackToGetArtist_WithoutAnExactMatch(bool similarEntity)
    {
        var library = Substitute.For<ILibraryManager>();
        library.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem> { new MusicAlbum { Name = "Solo", AlbumArtists = ["New Artist"] } });
        library.GetArtists(Arg.Any<IReadOnlyList<string>>()).Returns(similarEntity
            ? new Dictionary<string, MusicArtist[]> { ["New Artist"] = [Artist("New-Artist")] }
            : new Dictionary<string, MusicArtist[]>());
        var exact = Artist("New Artist");
        library.GetArtist("New Artist").Returns(exact);

        var list = LibraryQueries.BuildArtistList(library, new User("u", "p", "r"), null);

        Assert.Equal([(exact.Id.ToString("N"), "New Artist", 1)], list);
    }

    [Fact]
    public void AlbumCounts_CountAsTheArtistList_WithOneQuery()
    {
        var (gould, bernstein, nobody) = (Artist("Glenn Gould"), Artist("Leonard Bernstein"), Artist("Nobody"));
        var library = Substitute.For<ILibraryManager>();
        library.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem>
        {
            new MusicAlbum { Name = "Goldberg Variations", AlbumArtists = ["Glenn Gould", "Johann Sebastian Bach"] },
            new MusicAlbum { Name = "Brahms", AlbumArtists = ["Glenn Gould", "Leonard Bernstein", "glenn gould"] },
        });

        var counts = LibraryQueries.AlbumCounts(library, new User("u", "p", "r"), [gould, bernstein, nobody], null);

        Assert.Equal(new Dictionary<Guid, int> { [gould.Id] = 2, [bernstein.Id] = 1, [nobody.Id] = 0 }, counts);
        library.Received(1).GetItemList(Arg.Is<InternalItemsQuery>(q => q.AlbumArtistIds.SequenceEqual(new[] { gould.Id, bernstein.Id, nobody.Id })));
    }

    [Fact]
    public void ArtistRefs_LinkEachArtistOnce_AndSkipUnknownOnes()
    {
        var ids = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["A"] = "id-a", ["B"] = "id-b", ["Unknown"] = null };
        var refs = ItemMapper.ArtistRefs(["A", "B", "a", "", "Unknown"], ids.GetValueOrDefault);

        Assert.Equal([("id-a", "A"), ("id-b", "B")], refs.Select(r => ((string)r["id"]!, (string)r["name"]!)));
    }

    private static XmlElement Parse(string xml)
    {
        var doc = new XmlDocument();
        doc.LoadXml(xml);
        return doc.DocumentElement!;
    }

    private static List<Dictionary<string, object?>> Refs(params string[] names) =>
        names.Select(n => new Dictionary<string, object?> { ["id"] = $"id-{n}", ["name"] = n }).ToList();

    [Fact]
    public void Xml_SongsCarryTheirArtistsAsChildElements()
    {
        var song = new Dictionary<string, object?>
        {
            ["id"] = "s1", ["title"] = "Tune", ["artist"] = "A, B",
            ["artists"] = Refs("A", "B"), ["albumArtists"] = Refs("Various Artists"),
            ["starred"] = "2026-09-24T00:00:00Z",
        };
        var el = Parse(XmlBuilder.Song(song))["song", Ns]!;

        Assert.Equal("Tune", el.GetAttribute("title"));
        Assert.Equal("2026-09-24T00:00:00Z", el.GetAttribute("starred"));
        Assert.Equal(["A", "B"], el.GetElementsByTagName("artists", Ns).Cast<XmlElement>().Select(a => a.GetAttribute("name")));
        Assert.Equal("id-Various Artists", ((XmlElement)el.GetElementsByTagName("albumArtists", Ns)[0]!).GetAttribute("id"));
    }

    [Fact]
    public void Xml_AlbumsCarryTheirArtists_BeforeTheirSongs()
    {
        var album = new Dictionary<string, object?>
        {
            ["id"] = "al1", ["name"] = "Album",
            ["song"] = new List<Dictionary<string, object?>> { new() { ["id"] = "s1", ["artists"] = Refs("A") } },
            ["artists"] = Refs("A", "B"),
            ["played"] = "2026-09-23T00:00:00Z",
        };
        var el = Parse(XmlBuilder.Album(album))["album", Ns]!;

        Assert.Equal("2026-09-23T00:00:00Z", el.GetAttribute("played"));
        Assert.Equal(["artists", "artists", "song"], el.ChildNodes.Cast<XmlElement>().Select(c => c.LocalName));
        Assert.Equal("A", el["song", Ns]!["artists", Ns]!.GetAttribute("name"));
    }
}
