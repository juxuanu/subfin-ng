-- Subfin-NG SQLite schema.
-- No jellyfin_url anywhere — single-instance scope.

-- The OpenSubsonic password an administrator generated for a Jellyfin user. Token logins
-- (md5 of password + salt) need the plaintext, so it's stored encrypted rather than hashed.
CREATE TABLE IF NOT EXISTS subsonic_passwords (
  user_id TEXT PRIMARY KEY,
  password_encrypted BLOB NOT NULL,
  created_at TEXT NOT NULL DEFAULT (datetime('now'))
);

-- One per Jellyfin user
CREATE TABLE IF NOT EXISTS play_queue (
  user_id TEXT PRIMARY KEY,
  entry_ids TEXT NOT NULL DEFAULT '[]',
  current_id TEXT,
  current_index INTEGER NOT NULL DEFAULT 0,
  position_ms INTEGER NOT NULL DEFAULT 0,
  changed_at TEXT,
  changed_by TEXT NOT NULL DEFAULT ''
);

-- Owned by a Jellyfin user (index created in SubsonicStore.Migrate, after upgrades rebuild the table)
CREATE TABLE IF NOT EXISTS shares (
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

-- When an item was starred through Subfin-NG; Jellyfin's favourites carry no timestamp.
CREATE TABLE IF NOT EXISTS starred_at (
  jellyfin_user_id TEXT NOT NULL,
  item_id TEXT NOT NULL,
  starred_at TEXT NOT NULL,
  PRIMARY KEY (jellyfin_user_id, item_id)
);
