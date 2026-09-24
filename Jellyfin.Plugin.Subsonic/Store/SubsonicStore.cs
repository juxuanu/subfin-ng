using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.Subsonic.Store;

/// <summary>Play queue record.</summary>
public record PlayQueueRecord(
    string UserId,
    List<string> EntryIds,
    string? CurrentId,
    int CurrentIndex,
    long PositionMs,
    string? ChangedAt,
    string ChangedBy);

/// <summary>Share record.</summary>
public record ShareRecord(
    string ShareUid,
    string OwnerUserId,
    List<string> EntryIds,
    List<string> EntryIdsFlat,
    string? Description,
    byte[]? ShareSecretEncrypted,
    string? ExpiresAt,
    int VisitCount,
    string CreatedAt);

/// <summary>
/// All SQLite access goes through this class.
/// Thread-safe: serializes all access via a static lock over a single WAL-mode connection.
/// </summary>
public static class SubsonicStore
{
    private static SqliteConnection? _db;
    private static string _salt = string.Empty;
    private static readonly Lock DbLock = new();

    public static void Initialize(string dbPath, string salt)
    {
        _salt = salt;
        Crypto.SetSalt(salt);

        _db?.Dispose();
        _db = new SqliteConnection($"Data Source={dbPath}");
        _db.Open();

        using var pragma = _db.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL;";
        pragma.ExecuteNonQuery();

        RunSchema();
        Migrate();
    }

    private static SqliteConnection Db => _db ?? throw new InvalidOperationException("Store not initialized");

    private static void RunSchema()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream("Jellyfin.Plugin.Subsonic.Store.Schema.sql")
            ?? throw new FileNotFoundException("Embedded Schema.sql not found");
        using var reader = new StreamReader(stream);
        var sql = reader.ReadToEnd();

        using var cmd = Db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Brings databases created by earlier versions up to the current schema.</summary>
    private static void Migrate()
    {
        // Earlier versions had their own logins: "linked devices" with per-device app passwords,
        // which also owned shares. Logins are Jellyfin's now: re-own shares by user and drop the
        // device data (including the stored app passwords).
        if (TableExists("linked_devices"))
        {
            using var tx = Db.BeginTransaction();
            if (ColumnExists("shares", "linked_device_id"))
            {
                Exec(tx, @"
                    CREATE TABLE shares_v2 (
                      share_uid TEXT PRIMARY KEY,
                      owner_user_id TEXT NOT NULL,
                      entry_ids TEXT NOT NULL DEFAULT '[]',
                      entry_ids_flat TEXT NOT NULL DEFAULT '[]',
                      description TEXT,
                      share_secret_encrypted BLOB,
                      expires_at TEXT,
                      visit_count INTEGER NOT NULL DEFAULT 0,
                      created_at TEXT NOT NULL DEFAULT (datetime('now'))
                    );
                    INSERT INTO shares_v2 (share_uid, owner_user_id, entry_ids, entry_ids_flat, description,
                                           share_secret_encrypted, expires_at, visit_count, created_at)
                      SELECT s.share_uid, d.jellyfin_user_id, s.entry_ids, s.entry_ids_flat, s.description,
                             s.share_secret_encrypted, s.expires_at, s.visit_count, s.created_at
                      FROM shares s JOIN linked_devices d ON d.id = s.linked_device_id;
                    DROP TABLE shares;
                    ALTER TABLE shares_v2 RENAME TO shares;");
            }
            if (ColumnExists("play_queue", "subsonic_username"))
            {
                // Queues were per device login; keep each user's most recently saved one
                Exec(tx, @"
                    CREATE TABLE play_queue_v2 (
                      user_id TEXT PRIMARY KEY,
                      entry_ids TEXT NOT NULL DEFAULT '[]',
                      current_id TEXT,
                      current_index INTEGER NOT NULL DEFAULT 0,
                      position_ms INTEGER NOT NULL DEFAULT 0,
                      changed_at TEXT,
                      changed_by TEXT NOT NULL DEFAULT ''
                    );
                    INSERT OR REPLACE INTO play_queue_v2 (user_id, entry_ids, current_id, current_index, position_ms, changed_at, changed_by)
                      SELECT d.jellyfin_user_id, q.entry_ids, q.current_id, q.current_index, q.position_ms, q.changed_at, q.changed_by
                      FROM play_queue q JOIN linked_devices d ON d.subsonic_username = q.subsonic_username
                      ORDER BY q.changed_at;
                    DROP TABLE play_queue;
                    ALTER TABLE play_queue_v2 RENAME TO play_queue;");
            }
            Exec(tx, @"
                DROP TABLE linked_devices;
                DROP TABLE IF EXISTS pending_quickconnect;
                DROP TABLE IF EXISTS user_library_settings;");
            tx.Commit();
        }
        Exec(null, "CREATE INDEX IF NOT EXISTS idx_shares_owner ON shares(owner_user_id)");
        // Artist and album details from Last.fm, cached by versions that used it
        Exec(null, "DELETE FROM derived_cache WHERE cache_key LIKE 'lastfm:%'");
    }

    private static bool TableExists(string table) =>
        (long)Scalar($"SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '{table}'")! > 0;

    private static bool ColumnExists(string table, string column) =>
        (long)Scalar($"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'")! > 0;

    private static object? Scalar(string sql)
    {
        using var cmd = Db.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private static void Exec(SqliteTransaction? tx, string sql)
    {
        using var cmd = Db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // ── OpenSubsonic passwords ───────────────────────────────────────────────

    /// <summary>The user's OpenSubsonic password, or null if none was generated.</summary>
    public static string? GetSubsonicPassword(string userId)
    {
        lock (DbLock)
        {
            using var cmd = Db.CreateCommand();
            cmd.CommandText = "SELECT password_encrypted FROM subsonic_passwords WHERE user_id = @u";
            cmd.Parameters.AddWithValue("@u", userId);
            if (cmd.ExecuteScalar() is not byte[] blob) return null;
            try { return Crypto.Decrypt(blob, _salt); }
            catch { return null; }
        }
    }

    /// <summary>When each user's OpenSubsonic password was generated (UTC, SQLite format), by user id.</summary>
    public static Dictionary<string, string> GetSubsonicPasswordDates()
    {
        lock (DbLock)
        {
            using var cmd = Db.CreateCommand();
            cmd.CommandText = "SELECT user_id, created_at FROM subsonic_passwords";
            using var reader = cmd.ExecuteReader();
            var dates = new Dictionary<string, string>();
            while (reader.Read()) dates[reader.GetString(0)] = reader.GetString(1);
            return dates;
        }
    }

    public static void SetSubsonicPassword(string userId, string password)
    {
        var encrypted = Crypto.Encrypt(password, _salt);
        lock (DbLock)
        {
            using var cmd = Db.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO subsonic_passwords (user_id, password_encrypted, created_at) VALUES (@u, @p, datetime('now'))
                ON CONFLICT(user_id) DO UPDATE SET password_encrypted = @p, created_at = datetime('now')";
            cmd.Parameters.AddWithValue("@u", userId);
            cmd.Parameters.AddWithValue("@p", encrypted);
            cmd.ExecuteNonQuery();
        }
    }

    public static void DeleteSubsonicPassword(string userId)
    {
        lock (DbLock)
        {
            using var cmd = Db.CreateCommand();
            cmd.CommandText = "DELETE FROM subsonic_passwords WHERE user_id = @u";
            cmd.Parameters.AddWithValue("@u", userId);
            cmd.ExecuteNonQuery();
        }
    }

    // ── Play Queue ───────────────────────────────────────────────────────────

    public static PlayQueueRecord? GetPlayQueue(string userId)
    {
        lock (DbLock)
        {
            using var cmd = Db.CreateCommand();
            cmd.CommandText = "SELECT * FROM play_queue WHERE user_id = @u";
            cmd.Parameters.AddWithValue("@u", userId);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            return new PlayQueueRecord(
                reader.GetString(reader.GetOrdinal("user_id")),
                JsonSerializer.Deserialize<List<string>>(reader.GetString(reader.GetOrdinal("entry_ids"))) ?? [],
                reader.IsDBNull(reader.GetOrdinal("current_id")) ? null : reader.GetString(reader.GetOrdinal("current_id")),
                reader.GetInt32(reader.GetOrdinal("current_index")),
                reader.GetInt64(reader.GetOrdinal("position_ms")),
                reader.IsDBNull(reader.GetOrdinal("changed_at")) ? null : reader.GetString(reader.GetOrdinal("changed_at")),
                reader.GetString(reader.GetOrdinal("changed_by")));
        }
    }

    public static void SavePlayQueue(string userId, List<string> entryIds, string? currentId, int currentIndex, long positionMs, string changedBy)
    {
        lock (DbLock)
        {
            using var cmd = Db.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO play_queue (user_id, entry_ids, current_id, current_index, position_ms, changed_at, changed_by)
                VALUES (@u, @ids, @cid, @cidx, @pos, datetime('now'), @by)
                ON CONFLICT(user_id) DO UPDATE SET
                  entry_ids = @ids, current_id = @cid, current_index = @cidx,
                  position_ms = @pos, changed_at = datetime('now'), changed_by = @by";
            cmd.Parameters.AddWithValue("@u", userId);
            cmd.Parameters.AddWithValue("@ids", JsonSerializer.Serialize(entryIds));
            cmd.Parameters.AddWithValue("@cid", (object?)currentId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@cidx", currentIndex);
            cmd.Parameters.AddWithValue("@pos", positionMs);
            cmd.Parameters.AddWithValue("@by", changedBy);
            cmd.ExecuteNonQuery();
        }
    }

    public static void ClearPlayQueue(string userId)
    {
        lock (DbLock)
        {
            using var cmd = Db.CreateCommand();
            cmd.CommandText = "DELETE FROM play_queue WHERE user_id = @u";
            cmd.Parameters.AddWithValue("@u", userId);
            cmd.ExecuteNonQuery();
        }
    }

    // ── Shares ───────────────────────────────────────────────────────────────

    public static ShareRecord? GetShare(string shareUid)
    {
        lock (DbLock)
        {
            using var cmd = Db.CreateCommand();
            cmd.CommandText = "SELECT * FROM shares WHERE share_uid = @uid";
            cmd.Parameters.AddWithValue("@uid", shareUid);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadShare(reader) : null;
        }
    }

    public static List<ShareRecord> GetSharesForUser(string ownerUserId)
    {
        lock (DbLock)
        {
            var list = new List<ShareRecord>();
            using var cmd = Db.CreateCommand();
            cmd.CommandText = "SELECT * FROM shares WHERE owner_user_id = @o ORDER BY created_at DESC";
            cmd.Parameters.AddWithValue("@o", ownerUserId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) list.Add(ReadShare(reader));
            return list;
        }
    }

    public static string InsertShare(string ownerUserId, List<string> entryIds, List<string> entryIdsFlat, string? description, string? expiresAt, string secret)
    {
        var uid = Guid.NewGuid().ToString("N");
        var secretEncrypted = Crypto.Encrypt(secret, _salt);
        lock (DbLock)
        {
            using var cmd = Db.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO shares (share_uid, owner_user_id, entry_ids, entry_ids_flat, description, expires_at, share_secret_encrypted)
                VALUES (@uid, @owner, @eids, @flat, @desc, @exp, @sec)";
            cmd.Parameters.AddWithValue("@uid", uid);
            cmd.Parameters.AddWithValue("@owner", ownerUserId);
            cmd.Parameters.AddWithValue("@eids", JsonSerializer.Serialize(entryIds));
            cmd.Parameters.AddWithValue("@flat", JsonSerializer.Serialize(entryIdsFlat));
            cmd.Parameters.AddWithValue("@desc", (object?)description ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@exp", (object?)expiresAt ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@sec", secretEncrypted);
            cmd.ExecuteNonQuery();
            return uid;
        }
    }

    /// <summary>Sets when a share expires; null: never.</summary>
    public static void UpdateShareExpiry(string shareUid, string? expiresAt)
    {
        lock (DbLock)
        {
            using var cmd = Db.CreateCommand();
            cmd.CommandText = "UPDATE shares SET expires_at = @exp WHERE share_uid = @uid";
            cmd.Parameters.AddWithValue("@exp", (object?)expiresAt ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@uid", shareUid);
            cmd.ExecuteNonQuery();
        }
    }

    public static void UpdateShareDescription(string shareUid, string? description)
    {
        lock (DbLock)
        {
            using var cmd = Db.CreateCommand();
            cmd.CommandText = "UPDATE shares SET description = @desc WHERE share_uid = @uid";
            cmd.Parameters.AddWithValue("@desc", (object?)description ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@uid", shareUid);
            cmd.ExecuteNonQuery();
        }
    }

    public static void DeleteShare(string shareUid)
    {
        lock (DbLock)
        {
            using var cmd = Db.CreateCommand();
            cmd.CommandText = "DELETE FROM shares WHERE share_uid = @uid";
            cmd.Parameters.AddWithValue("@uid", shareUid);
            cmd.ExecuteNonQuery();
        }
    }

    public static void IncrementShareVisitCount(string shareUid)
    {
        lock (DbLock)
        {
            using var cmd = Db.CreateCommand();
            cmd.CommandText = "UPDATE shares SET visit_count = visit_count + 1 WHERE share_uid = @uid";
            cmd.Parameters.AddWithValue("@uid", shareUid);
            cmd.ExecuteNonQuery();
        }
    }

    public static string? GetShareSecret(string shareUid)
    {
        lock (DbLock)
        {
            using var cmd = Db.CreateCommand();
            cmd.CommandText = "SELECT share_secret_encrypted FROM shares WHERE share_uid = @uid";
            cmd.Parameters.AddWithValue("@uid", shareUid);
            var blob = cmd.ExecuteScalar() as byte[];
            if (blob == null) return null;
            try { return Crypto.Decrypt(blob, _salt); }
            catch { return null; }
        }
    }

    // ── Derived Cache ────────────────────────────────────────────────────────

    public record DerivedCacheEntry(string CacheKey, string ValueJson, string CachedAt, string? LastSourceChangeAt);

    // ── Starred timestamps ───────────────────────────────────────────────────

    /// <summary>Records (keeping the first date) or forgets when a user starred an item.</summary>
    public static void SetStarred(string jellyfinUserId, string itemId, bool starred)
    {
        lock (DbLock)
        {
            using var cmd = Db.CreateCommand();
            cmd.CommandText = starred
                ? "INSERT OR IGNORE INTO starred_at (jellyfin_user_id, item_id, starred_at) VALUES (@u, @i, @at)"
                : "DELETE FROM starred_at WHERE jellyfin_user_id = @u AND item_id = @i";
            cmd.Parameters.AddWithValue("@u", jellyfinUserId);
            cmd.Parameters.AddWithValue("@i", itemId);
            cmd.Parameters.AddWithValue("@at", DateTime.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>item id (N format) → ISO date the user starred it.</summary>
    public static Dictionary<string, string> GetStarredDates(string jellyfinUserId)
    {
        lock (DbLock)
        {
            var result = new Dictionary<string, string>();
            using var cmd = Db.CreateCommand();
            cmd.CommandText = "SELECT item_id, starred_at FROM starred_at WHERE jellyfin_user_id = @u";
            cmd.Parameters.AddWithValue("@u", jellyfinUserId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) result[reader.GetString(0)] = reader.GetString(1);
            return result;
        }
    }

    public static DerivedCacheEntry? GetDerivedCache(string cacheKey)
    {
        lock (DbLock)
        {
            using var cmd = Db.CreateCommand();
            cmd.CommandText = "SELECT * FROM derived_cache WHERE cache_key = @k";
            cmd.Parameters.AddWithValue("@k", cacheKey);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            return new DerivedCacheEntry(
                reader.GetString(reader.GetOrdinal("cache_key")),
                reader.GetString(reader.GetOrdinal("value_json")),
                reader.GetString(reader.GetOrdinal("cached_at")),
                reader.IsDBNull(reader.GetOrdinal("last_source_change_at")) ? null : reader.GetString(reader.GetOrdinal("last_source_change_at")));
        }
    }

    public static void SetDerivedCache(string cacheKey, string valueJson, string? lastSourceChangeAt)
    {
        lock (DbLock)
        {
            using var cmd = Db.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO derived_cache (cache_key, value_json, cached_at, last_source_change_at)
                VALUES (@k, @v, datetime('now'), @lsc)
                ON CONFLICT(cache_key) DO UPDATE SET value_json = @v, cached_at = datetime('now'), last_source_change_at = @lsc";
            cmd.Parameters.AddWithValue("@k", cacheKey);
            cmd.Parameters.AddWithValue("@v", valueJson);
            cmd.Parameters.AddWithValue("@lsc", (object?)lastSourceChangeAt ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private static ShareRecord ReadShare(SqliteDataReader r) => new(
        r.GetString(r.GetOrdinal("share_uid")),
        r.GetString(r.GetOrdinal("owner_user_id")),
        JsonSerializer.Deserialize<List<string>>(r.GetString(r.GetOrdinal("entry_ids"))) ?? [],
        JsonSerializer.Deserialize<List<string>>(r.GetString(r.GetOrdinal("entry_ids_flat"))) ?? [],
        r.IsDBNull(r.GetOrdinal("description")) ? null : r.GetString(r.GetOrdinal("description")),
        r.IsDBNull(r.GetOrdinal("share_secret_encrypted")) ? null : (byte[])r["share_secret_encrypted"],
        r.IsDBNull(r.GetOrdinal("expires_at")) ? null : r.GetString(r.GetOrdinal("expires_at")),
        r.GetInt32(r.GetOrdinal("visit_count")),
        r.GetString(r.GetOrdinal("created_at")));
}
