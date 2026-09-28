using System.Security.Cryptography;
using Jellyfin.Plugin.Subsonic.Store;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jellyfin.Plugin.Subsonic.Tests;

/// <summary>Bookmarks: one per user and song, in the plugin's database.</summary>
[Collection("StaticState")]
public sealed class BookmarkStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("subfin-test").FullName;

    public BookmarkStoreTests() =>
        SubsonicStore.Initialize(Path.Combine(_dir, "subsonic.db"), Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Bookmark_IsReplaced_KeepingWhenItWasCreated()
    {
        SubsonicStore.SetBookmark("user", "song", 1500, "chorus");
        var first = Assert.Single(SubsonicStore.GetBookmarks("user"));
        Assert.Equal(("song", 1500L, "chorus"), (first.ItemId, first.PositionMs, first.Comment));

        Thread.Sleep(20);
        SubsonicStore.SetBookmark("user", "song", 9000, null);
        var moved = Assert.Single(SubsonicStore.GetBookmarks("user"));
        Assert.Equal((9000L, null, first.CreatedAt), (moved.PositionMs, moved.Comment, moved.CreatedAt));
        Assert.True(string.CompareOrdinal(moved.ChangedAt, first.ChangedAt) > 0, $"{first.ChangedAt} → {moved.ChangedAt}");
    }

    [Fact]
    public void Bookmarks_ArePerUser_MostRecentlyChangedFirst()
    {
        SubsonicStore.SetBookmark("user", "a", 1, null);
        Thread.Sleep(20);
        SubsonicStore.SetBookmark("user", "b", 2, null);
        SubsonicStore.SetBookmark("other", "a", 3, null);
        Thread.Sleep(20);
        SubsonicStore.SetBookmark("user", "a", 4, null);

        Assert.Equal(["a", "b"], SubsonicStore.GetBookmarks("user").Select(b => b.ItemId));
        Assert.Equal(3, Assert.Single(SubsonicStore.GetBookmarks("other")).PositionMs);

        SubsonicStore.DeleteBookmark("user", "a");
        SubsonicStore.DeleteBookmark("user", "missing");
        Assert.Equal(["b"], SubsonicStore.GetBookmarks("user").Select(b => b.ItemId));
        Assert.Single(SubsonicStore.GetBookmarks("other"));
    }
}
