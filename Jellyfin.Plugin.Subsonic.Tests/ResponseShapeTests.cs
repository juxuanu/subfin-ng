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

    [Theory]
    [InlineData("/media/music/Artist/Album/01 Song.flac", "Artist/Album/01 Song.flac")]
    [InlineData("/media/music-extra/Other/02 B.mp3", "Other/02 B.mp3")]  // prefix of another root doesn't count
    [InlineData("/elsewhere/03 C.mp3", "03 C.mp3")]                     // outside every library: file name only
    [InlineData("", "")]
    public void SongPaths_AreRelativeToTheirLibrary(string path, string expected)
    {
        Assert.Equal(expected, ItemMapper.RelativeToLibrary(path, ["/media/music/", "/media/music-extra"]));
    }
}
