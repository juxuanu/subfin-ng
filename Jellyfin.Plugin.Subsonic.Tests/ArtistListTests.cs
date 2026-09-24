using System;
using System.Collections.Generic;
using System.Linq;
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

    private static MusicArtist Artist(string name) => new() { Name = name, Id = Guid.NewGuid() };

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
        library.GetArtist(Arg.Any<string>()).Returns(call => artists[call.Arg<string>()]);

        var list = LibraryQueries.BuildArtistList(library, new User("u", "p", "r"), null);

        Assert.Equal(
            [("Glenn Gould", 3), ("Johann Sebastian Bach", 2), ("Leonard Bernstein", 1)],
            list.Select(a => (a.Name, a.AlbumCount)));
        Assert.All(list, a => Assert.Equal(artists[a.Name].Id.ToString("N"), a.Id));
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
        Assert.Equal("A", ((XmlElement)el["song", Ns]!["artists", Ns]!).GetAttribute("name"));
    }
}
