using System;
using System.IO;
using System.Security.Cryptography;
using Jellyfin.Plugin.Subsonic.Store;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jellyfin.Plugin.Subsonic.Tests;

[Collection("StaticState")]
public sealed class StoreMigrationTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("subfin-test").FullName;
    private readonly string _salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private static void Exec(string dbPath, string sql, params (string Name, object Value)[] args)
    {
        using var db = new SqliteConnection($"Data Source={dbPath}");
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }

    private static long Count(string dbPath, string sql)
    {
        using var db = new SqliteConnection($"Data Source={dbPath}");
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>A database from the versions with their own device logins.</summary>
    private string DeviceEraDatabase()
    {
        var path = Path.Combine(_dir, "old.db");
        Exec(path, @"
            CREATE TABLE linked_devices (
              id INTEGER PRIMARY KEY AUTOINCREMENT, subsonic_username TEXT NOT NULL, app_password_hash TEXT NOT NULL,
              app_password_encrypted BLOB NOT NULL, jellyfin_user_id TEXT NOT NULL, device_label TEXT NOT NULL DEFAULT '',
              jellyfin_device_id TEXT, jellyfin_device_name TEXT, created_at TEXT NOT NULL DEFAULT (datetime('now')), api_key_lookup TEXT);
            CREATE TABLE pending_quickconnect (secret TEXT PRIMARY KEY, jellyfin_user_id TEXT NOT NULL, created_at TEXT NOT NULL DEFAULT (datetime('now')));
            CREATE TABLE play_queue (
              subsonic_username TEXT PRIMARY KEY, entry_ids TEXT NOT NULL DEFAULT '[]', current_id TEXT,
              current_index INTEGER NOT NULL DEFAULT 0, position_ms INTEGER NOT NULL DEFAULT 0, changed_at TEXT, changed_by TEXT NOT NULL DEFAULT '');
            CREATE TABLE user_library_settings (subsonic_username TEXT PRIMARY KEY, selected_ids TEXT NOT NULL DEFAULT '[]', updated_at TEXT NOT NULL DEFAULT (datetime('now')));
            CREATE TABLE shares (
              share_uid TEXT PRIMARY KEY, linked_device_id INTEGER NOT NULL REFERENCES linked_devices(id) ON DELETE CASCADE,
              entry_ids TEXT NOT NULL DEFAULT '[]', entry_ids_flat TEXT NOT NULL DEFAULT '[]', description TEXT,
              share_secret_encrypted BLOB, expires_at TEXT, visit_count INTEGER NOT NULL DEFAULT 0, created_at TEXT NOT NULL DEFAULT (datetime('now')));
            CREATE INDEX idx_shares_linked_device ON shares(linked_device_id);

            -- alice has a phone and a laptop login, bob one
            INSERT INTO linked_devices (id, subsonic_username, app_password_hash, app_password_encrypted, jellyfin_user_id)
              VALUES (1, 'alice', 'h', x'00', 'aaaa'), (2, 'alice-laptop', 'h', x'00', 'aaaa'), (3, 'bob', 'h', x'00', 'bbbb');
            INSERT INTO shares (share_uid, linked_device_id, entry_ids, entry_ids_flat, description, share_secret_encrypted, visit_count)
              VALUES ('s-phone', 1, '[""al-1""]', '[""t1""]', 'from the phone', @secret, 3),
                     ('s-laptop', 2, '[""t2""]', '[""t2""]', NULL, @secret, 0),
                     ('s-bob', 3, '[""t3""]', '[""t3""]', NULL, @secret, 0);
            INSERT INTO play_queue (subsonic_username, entry_ids, current_id, position_ms, changed_at, changed_by)
              VALUES ('alice', '[""old""]', 'old', 1, '2026-01-01 10:00:00', 'phone'),
                     ('alice-laptop', '[""new""]', 'new', 2, '2026-02-01 10:00:00', 'laptop'),
                     ('bob', '[""b""]', 'b', 3, '2026-01-15 10:00:00', 'bob');
            INSERT INTO user_library_settings (subsonic_username, selected_ids) VALUES ('alice', '[""lib""]');
            INSERT INTO pending_quickconnect (secret, jellyfin_user_id) VALUES ('qc', 'aaaa');",
            ("@secret", Crypto.Encrypt("share-secret", _salt)));
        return path;
    }

    [Fact]
    public void Shares_MoveFromDevicesToTheirJellyfinUser()
    {
        var path = DeviceEraDatabase();
        SubsonicStore.Initialize(path, _salt);

        var alice = SubsonicStore.GetSharesForUser("aaaa");
        Assert.Equal(["s-laptop", "s-phone"], alice.ConvertAll(s => s.ShareUid).Order());
        var phone = alice.Find(s => s.ShareUid == "s-phone")!;
        Assert.Equal("from the phone", phone.Description);
        Assert.Equal(3, phone.VisitCount);
        Assert.Equal(["t1"], phone.EntryIdsFlat);
        Assert.Equal("share-secret", SubsonicStore.GetShareSecret("s-phone"));
        Assert.Equal("bbbb", Assert.Single(SubsonicStore.GetSharesForUser("bbbb")).OwnerUserId);
    }

    [Fact]
    public void PlayQueues_KeepEachUsersLatest()
    {
        var path = DeviceEraDatabase();
        SubsonicStore.Initialize(path, _salt);

        var alice = SubsonicStore.GetPlayQueue("aaaa")!;
        Assert.Equal(["new"], alice.EntryIds);
        Assert.Equal("laptop", alice.ChangedBy);
        Assert.Equal(["b"], SubsonicStore.GetPlayQueue("bbbb")!.EntryIds);
        Assert.Null(SubsonicStore.GetPlayQueue("alice"));
    }

    [Fact]
    public void DeviceLogins_AreDropped()
    {
        var path = DeviceEraDatabase();
        SubsonicStore.Initialize(path, _salt);

        // The stored app passwords go with them
        Assert.Equal(0, Count(path, "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('linked_devices', 'pending_quickconnect', 'user_library_settings', 'idx_shares_linked_device')"));
        Assert.Equal(1, Count(path, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'idx_shares_owner'"));
    }

    [Fact]
    public void MigratedDatabase_OpensAgainUnchanged()
    {
        var path = DeviceEraDatabase();
        SubsonicStore.Initialize(path, _salt);
        SubsonicStore.Initialize(path, _salt);

        Assert.Equal(2, SubsonicStore.GetSharesForUser("aaaa").Count);
        Assert.Equal(["new"], SubsonicStore.GetPlayQueue("aaaa")!.EntryIds);
    }

    [Fact]
    public void NewDatabase_StoresSharesByUser()
    {
        SubsonicStore.Initialize(Path.Combine(_dir, "new.db"), _salt);
        var uid = SubsonicStore.InsertShare("cccc", ["al-1"], ["t1"], "d", null, "sec");

        Assert.Equal(uid, Assert.Single(SubsonicStore.GetSharesForUser("cccc")).ShareUid);
        Assert.Empty(SubsonicStore.GetSharesForUser("aaaa"));
    }
}
