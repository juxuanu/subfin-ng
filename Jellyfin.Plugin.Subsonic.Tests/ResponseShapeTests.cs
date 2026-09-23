using System.Collections.Generic;
using System.Linq;
using System.Xml;
using Jellyfin.Plugin.Subsonic.Controllers;
using Jellyfin.Plugin.Subsonic.Mappers;
using Jellyfin.Plugin.Subsonic.Response;
using Xunit;

namespace Jellyfin.Plugin.Subsonic.Tests;

/// <summary>Response shapes the OpenSubsonic spec requires (checked against its OpenAPI schemas).</summary>
public class ResponseShapeTests
{
    private const string Ns = "http://subsonic.org/restapi";

    private static XmlElement Root(string xml)
    {
        var doc = new XmlDocument();
        doc.LoadXml(xml);
        return doc.DocumentElement!;
    }

    private static Dictionary<string, object?> Album(string name) => new()
    {
        ["id"] = "a1", ["name"] = name, ["title"] = name, ["album"] = name, ["parent"] = "ar-1", ["isDir"] = true,
        ["songCount"] = 1, ["duration"] = 3, ["created"] = "2020-01-01T00:00:00Z",
    };

    [Fact]
    public void Errors_CarryTheOpenSubsonicFields_AndHelpUrl()
    {
        var root = Root(XmlBuilder.ErrorEnvelope(44, "Invalid API key.", "https://example/subfin/"));
        Assert.Equal("failed", root.GetAttribute("status"));
        Assert.Equal("true", root.GetAttribute("openSubsonic"));
        Assert.NotEmpty(root.GetAttribute("type"));
        Assert.NotEmpty(root.GetAttribute("serverVersion"));
        Assert.Equal("https://example/subfin/", root["error", Ns]!.GetAttribute("helpUrl"));

        var json = SubsonicEnvelope.Error(40, "Wrong username or password.")["subsonic-response"]!;
        Assert.True((bool)json["openSubsonic"]!);
        Assert.NotNull(json["type"]);
        Assert.Null(json["error"]!["helpUrl"]);
    }

    [Fact]
    public void Extensions_AreRealSpecExtensions()
    {
        var names = Root(XmlBuilder.OpenSubsonicExtensions())["openSubsonicExtensions", Ns]!
            .ChildNodes.Cast<XmlElement>().Select(e => e.GetAttribute("name")).ToList();
        Assert.Contains("apiKeyAuthentication", names);
        Assert.DoesNotContain("template", names);
    }

    [Fact]
    public void Search2_UsesItsOwnElement_WithChildAlbums()
    {
        var root = Root(XmlBuilder.SearchResult3([], [Album("A")], [], "searchResult2"));
        var album = root["searchResult2", Ns]!["album", Ns]!;
        Assert.Equal("A", album.GetAttribute("title"));
        Assert.Equal("true", album.GetAttribute("isDir"));
    }

    [Fact]
    public void Search3_AlbumsAreAlbumID3_WithoutChildOnlyAttributes()
    {
        var album = Root(XmlBuilder.SearchResult3([], [Album("A")], []))["searchResult3", Ns]!["album", Ns]!;
        Assert.Equal("A", album.GetAttribute("name"));
        foreach (var childOnly in new[] { "isDir", "title", "album", "parent" })
            Assert.False(album.HasAttribute(childOnly), childOnly);
    }

    [Fact]
    public void AlbumInfo2_ReturnsAnAlbumInfoElement()
    {
        Assert.NotNull(Root(XmlBuilder.AlbumInfo("notes", null, null, v2: true))["albumInfo", Ns]);
    }

    [Fact]
    public void PlayQueue_HasItsRequiredUsername()
    {
        var pq = Root(XmlBuilder.PlayQueue(null, 0, 0, "1970-01-01T00:00:00Z", "", [], "alice"))["playQueue", Ns]!;
        Assert.Equal("alice", pq.GetAttribute("username"));
        Assert.Equal("1970-01-01T00:00:00Z", pq.GetAttribute("changed"));
    }

    [Theory]
    [InlineData("/media/music/Artist/Album/01 Song.flac", "Artist/Album/01 Song.flac")]
    [InlineData("/media/music-extra/Other/02 B.mp3", "Other/02 B.mp3")]  // prefix of another root doesn't count
    [InlineData("/elsewhere/03 C.mp3", "03 C.mp3")]                     // outside every library: file name only
    [InlineData("", "")]
    public void SongPaths_AreRelativeToTheirLibrary(string path, string expected)
    {
        Assert.Equal(expected, ItemMapper.RelativeToLibrary(path, ["/media/music/", "/media/music-extra"]));
    }

    [Theory]
    [InlineData(2000, 2002, new[] { 2000, 2001, 2002 })]
    [InlineData(2002, 2000, new[] { 2000, 2001, 2002 })]  // reversed bounds are allowed (newest first)
    [InlineData(1999, 1999, new[] { 1999 })]
    public void YearRanges_AcceptEitherOrder(int from, int to, int[] expected)
    {
        Assert.Equal(expected, SubsonicController.YearRange(from, to));
    }
}
