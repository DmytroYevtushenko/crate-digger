using Dapper;
using Microsoft.Data.Sqlite;

namespace Crate.Api;

/// <summary>
/// Thin SQLite wrapper: creates the file/dir, initializes the schema, hands out open
/// connections. No ORM/DDD — just Dapper + plain SQL.
/// </summary>
public sealed class Db
{
    private readonly string _connString;

    public Db(string dbPath)
    {
        // snake_case in DB -> PascalCase in C# (duration_sec -> DurationSec).
        DefaultTypeMap.MatchNamesWithUnderscores = true;

        var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _connString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_connString);
        c.Open();
        c.Execute("PRAGMA busy_timeout=5000;");
        return c;
    }

    public void Init()
    {
        using var c = Open();
        c.Execute("PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;");
        c.Execute(Schema);
        // Lightweight migrations: SQLite has no ADD COLUMN IF NOT EXISTS.
        TryAddColumn(c, "tracks", "enriched", "INTEGER NOT NULL DEFAULT 0");
        TryAddColumn(c, "tracks", "created_at", "TEXT");
        c.Execute("UPDATE tracks SET created_at=updated_at WHERE created_at IS NULL");
        TryAddColumn(c, "library_files", "mtime", "INTEGER");
        TryAddColumn(c, "library_files", "size", "INTEGER");
        // Audio bitrate, so lossy (e.g. YouTube-sourced) files are visible and sortable next to FLAC.
        TryAddColumn(c, "library_files", "bitrate_kbps", "INTEGER");
        TryAddColumn(c, "tracks", "bitrate_kbps", "INTEGER");
    }

    private static void TryAddColumn(SqliteConnection c, string table, string col, string decl)
    {
        try { c.Execute($"ALTER TABLE {table} ADD COLUMN {col} {decl};"); }
        catch (SqliteException) { /* column already exists — ok */ }
    }

    // Schema matches PLAN §5. Idempotent — CREATE TABLE IF NOT EXISTS.
    private const string Schema = @"
CREATE TABLE IF NOT EXISTS sources (
    id                    INTEGER PRIMARY KEY AUTOINCREMENT,
    kind                  TEXT    NOT NULL DEFAULT 'youtube',
    url                   TEXT    NOT NULL,
    name                  TEXT    NOT NULL,
    dest_dir              TEXT    NOT NULL,
    cond                  TEXT,
    pref                  TEXT,
    min_format            TEXT,
    upgrade_lower_quality INTEGER NOT NULL DEFAULT 0,
    schedule_cron         TEXT,
    profile               TEXT,
    enabled               INTEGER NOT NULL DEFAULT 1,
    last_run_at           TEXT
);

CREATE TABLE IF NOT EXISTS tracks (
    id               INTEGER PRIMARY KEY AUTOINCREMENT,
    source_id        INTEGER NOT NULL REFERENCES sources(id),
    external_id      TEXT,
    raw_title        TEXT,
    artist           TEXT,
    title            TEXT,
    album            TEXT,
    duration_sec     INTEGER,
    mb_recording_id  TEXT,
    isrc             TEXT,
    expected_len_sec INTEGER,
    state            TEXT    NOT NULL DEFAULT 'Pending',
    enriched         INTEGER NOT NULL DEFAULT 0,
    file_path        TEXT,
    created_at       TEXT,
    updated_at       TEXT    NOT NULL DEFAULT (datetime('now')),
    UNIQUE(source_id, external_id)
);
CREATE INDEX IF NOT EXISTS ix_tracks_state ON tracks(state);
CREATE INDEX IF NOT EXISTS ix_tracks_source ON tracks(source_id);

CREATE TABLE IF NOT EXISTS download_attempts (
    id             INTEGER PRIMARY KEY AUTOINCREMENT,
    track_id       INTEGER NOT NULL REFERENCES tracks(id),
    started_at     TEXT,
    finished_at    TEXT,
    result         TEXT,
    sldl_job_id    TEXT,
    failure_reason TEXT
);

CREATE TABLE IF NOT EXISTS library_files (
    id               INTEGER PRIMARY KEY AUTOINCREMENT,
    path             TEXT    NOT NULL UNIQUE,
    artist           TEXT,
    title            TEXT,
    duration_sec     INTEGER,
    fingerprint      TEXT,
    mtime            INTEGER,
    size             INTEGER,
    matched_track_id INTEGER REFERENCES tracks(id),
    scanned_at       TEXT
);

CREATE TABLE IF NOT EXISTS meta_cache (
    key          TEXT PRIMARY KEY,
    payload_json TEXT,
    fetched_at   TEXT
);

-- Pluggable actions on pipeline events (PLAN §8.1): Navidrome rescan, webhook, shell, ...
CREATE TABLE IF NOT EXISTS actions (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    source_id   INTEGER REFERENCES sources(id),
    event       TEXT    NOT NULL,
    type        TEXT    NOT NULL,
    config_json TEXT,
    enabled     INTEGER NOT NULL DEFAULT 1
);

-- Files a manual-review decision rejected for a track, so fuzzy matching never re-links
-- the same rejected file back onto it (see ManualVerifyService.Resolve ""keep-download"").
CREATE TABLE IF NOT EXISTS track_ignored_files (
    track_id INTEGER NOT NULL REFERENCES tracks(id),
    path     TEXT    NOT NULL,
    PRIMARY KEY (track_id, path)
);
";
}
