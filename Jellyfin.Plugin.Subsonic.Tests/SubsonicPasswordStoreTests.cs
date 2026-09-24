using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.Subsonic.Store;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jellyfin.Plugin.Subsonic.Tests;

[Collection("StaticState")]
public sealed class SubsonicPasswordStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("subfin-test").FullName;
    private readonly string _db;

    public SubsonicPasswordStoreTests()
    {
        _db = Path.Combine(_dir, "subsonic.db");
        SubsonicStore.Initialize(_db, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Password_RoundTrips_AndIsReplacedOrRemoved()
    {
        Assert.Null(SubsonicStore.GetSubsonicPassword("aaaa"));
        SubsonicStore.SetSubsonicPassword("aaaa", "first-pass");
        SubsonicStore.SetSubsonicPassword("bbbb", "bobs-pass");
        Assert.Equal("first-pass", SubsonicStore.GetSubsonicPassword("aaaa"));

        SubsonicStore.SetSubsonicPassword("aaaa", "second-pass");
        Assert.Equal("second-pass", SubsonicStore.GetSubsonicPassword("aaaa"));
        Assert.Equal(["aaaa", "bbbb"], SubsonicStore.GetSubsonicPasswordDates().Keys.Order());

        SubsonicStore.DeleteSubsonicPassword("aaaa");
        Assert.Null(SubsonicStore.GetSubsonicPassword("aaaa"));
        Assert.Equal("bobs-pass", SubsonicStore.GetSubsonicPassword("bbbb"));
    }

    [Fact]
    public void Password_IsEncryptedAtRest()
    {
        SubsonicStore.SetSubsonicPassword("aaaa", "plain-text-here");
        SqliteConnection.ClearAllPools();
        Assert.DoesNotContain("plain-text-here", Encoding.UTF8.GetString(File.ReadAllBytes(_db)) + ReadWal());
    }

    private string ReadWal()
    {
        var wal = _db + "-wal";
        if (!File.Exists(wal)) return "";
        using var stream = new FileStream(wal, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
