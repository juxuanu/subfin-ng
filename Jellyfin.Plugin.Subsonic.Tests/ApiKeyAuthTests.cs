using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Jellyfin.Plugin.Subsonic.Auth;
using Jellyfin.Plugin.Subsonic.Store;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace Jellyfin.Plugin.Subsonic.Tests;

/// <summary>SubsonicStore and Crypto are process-wide singletons; tests touching them run serially.</summary>
[CollectionDefinition("StaticState", DisableParallelization = true)]
public class StaticStateCollection;

[Collection("StaticState")]
public sealed class ApiKeyAuthTests : IDisposable
{
    private const string Key = "k7Qm2xZp9sVb4nTw8cRd1eFg";
    private readonly string _dir = Directory.CreateTempSubdirectory("subfin-test").FullName;
    private readonly string _salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private readonly SubsonicAuth _auth = new(null!, NullLogger<SubsonicAuth>.Instance);

    public ApiKeyAuthTests()
    {
        SubsonicStore.Initialize(Path.Combine(_dir, "subsonic.db"), _salt);
        SubsonicStore.InsertDevice("alice", "a11ce", Key, "phone", null, null);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private object Resolve(params (string Key, string Value)[] query)
    {
        var dict = new Dictionary<string, StringValues>();
        foreach (var (k, v) in query) dict[k] = v;
        return _auth.Resolve(new QueryCollection(dict));
    }

    private static int ErrorCodeOf(object result) => Assert.IsType<SubsonicAuth.AuthError>(result).Code;

    [Fact]
    public void ApiKey_Alone_IdentifiesTheDevicesUser()
    {
        var result = Assert.IsType<AuthResult>(Resolve(("apiKey", Key)));
        Assert.Equal("alice", result.SubsonicUsername);
        Assert.Equal("a11ce", result.JellyfinUserId);
    }

    [Theory]
    [InlineData("u", "alice")]
    [InlineData("p", Key)]
    [InlineData("t", "0123456789abcdef")]
    [InlineData("s", "abc123")]
    public void ApiKey_WithAnyOtherCredential_IsError43(string param, string value)
    {
        Assert.Equal(43, ErrorCodeOf(Resolve(("apiKey", Key), (param, value))));
    }

    [Fact]
    public void UnknownApiKey_IsError44()
    {
        Assert.Equal(44, ErrorCodeOf(Resolve(("apiKey", "not-a-key"))));
    }

    [Fact]
    public void PasswordAndToken_Together_IsError43()
    {
        Assert.Equal(43, ErrorCodeOf(Resolve(("u", "alice"), ("p", Key), ("t", "0123"), ("s", "ab"))));
    }

    [Fact]
    public void ResettingThePassword_RevokesTheOldKey()
    {
        var device = Assert.IsType<AuthResult>(Resolve(("apiKey", Key)));
        SubsonicStore.UpdateDevicePassword(long.Parse(device.JellyfinDeviceId!["subfin-".Length..]), "fresh-key-000111222333");

        Assert.Equal(44, ErrorCodeOf(Resolve(("apiKey", Key))));
        Assert.IsType<AuthResult>(Resolve(("apiKey", "fresh-key-000111222333")));
    }

    [Fact]
    public void HiddenWebShareDevice_IsNotAnApiKey()
    {
        SubsonicStore.InsertDevice("alice", "a11ce", "web-share-device-key-xyz", "Subfin Web", SubsonicStore.WebShareDeviceSentinel, "Subfin Web");
        Assert.Equal(44, ErrorCodeOf(Resolve(("apiKey", "web-share-device-key-xyz"))));
    }

    [Fact]
    public void UsernameAndPassword_StillWork()
    {
        Assert.IsType<AuthResult>(Resolve(("u", "alice"), ("p", Key)));
        Assert.Equal(40, ErrorCodeOf(Resolve(("u", "alice"), ("p", "wrong"))));
    }

    [Fact]
    public void ExpiredShare_NoLongerAuthenticates()
    {
        var device = Assert.IsType<AuthResult>(Resolve(("apiKey", Key)));
        var deviceId = long.Parse(device.JellyfinDeviceId!["subfin-".Length..]);
        var live = SubsonicStore.InsertShare(deviceId, ["x"], ["x"], null, DateTimeOffset.UtcNow.AddDays(1).ToString("o"), "live-secret");
        var expired = SubsonicStore.InsertShare(deviceId, ["x"], ["x"], null, DateTimeOffset.UtcNow.AddDays(-1).ToString("o"), "old-secret");

        Assert.IsType<AuthResult>(Resolve(("u", $"share_{live}"), ("p", "live-secret")));
        Assert.Equal(40, ErrorCodeOf(Resolve(("u", $"share_{expired}"), ("p", "old-secret"))));
    }

    [Fact]
    public void DevicesFromBeforeApiKeys_AreMigrated()
    {
        // A database created by an earlier version: no api_key_lookup column.
        var oldDb = Path.Combine(_dir, "old.db");
        using (var db = new SqliteConnection($"Data Source={oldDb}"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = @"CREATE TABLE linked_devices (
                id INTEGER PRIMARY KEY AUTOINCREMENT, subsonic_username TEXT NOT NULL, app_password_hash TEXT NOT NULL,
                app_password_encrypted BLOB NOT NULL, jellyfin_user_id TEXT NOT NULL, device_label TEXT NOT NULL DEFAULT '',
                jellyfin_device_id TEXT, jellyfin_device_name TEXT, created_at TEXT NOT NULL DEFAULT (datetime('now')));
                INSERT INTO linked_devices (subsonic_username, app_password_hash, app_password_encrypted, jellyfin_user_id)
                VALUES ('bob', 'unused', @enc, 'b0b');";
            cmd.Parameters.AddWithValue("@enc", Crypto.Encrypt("bobs-old-password-123", _salt));
            cmd.ExecuteNonQuery();
        }

        SubsonicStore.Initialize(oldDb, _salt);

        var result = Assert.IsType<AuthResult>(Resolve(("apiKey", "bobs-old-password-123")));
        Assert.Equal("bob", result.SubsonicUsername);
    }

    [Fact]
    public void LookupHash_IsKeyedBySalt()
    {
        var other = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var mine = Crypto.LookupHash(Key, _salt);
        Assert.Equal(mine, Crypto.LookupHash(Key, _salt));
        Assert.NotEqual(mine, Crypto.LookupHash(Key, other));
        Assert.Equal(mine, Crypto.LookupHash(Key, _salt));
    }
}
