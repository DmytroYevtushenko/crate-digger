using Dapper;

namespace Crate.Api;

public record SourceInput(
    string Name,
    string Url,
    string DestDir,
    string Kind = "youtube",
    string? Cond = null,
    string? Pref = null,
    string? MinFormat = null,
    bool UpgradeLowerQuality = false,
    string? ScheduleCron = null,
    string? Profile = null,
    bool Enabled = true);

public record SyncResult(bool Ok, string? Error, int Total, int Added);

/// <summary>
/// Imports a playlist into the DB. Phase 1 (fast): flat listing -> insert new tracks as Pending.
/// Phase 2 (background): pull authoritative artist/track/album/duration per new video.
/// </summary>
public sealed class SyncService(Db db, YtDlp ytdlp, ILogger<SyncService> log)
{
    public async Task<SyncResult> RunAsync(long sourceId, CancellationToken ct = default)
    {
        using var c = db.Open();
        var src = c.QuerySingleOrDefault<Source>("SELECT * FROM sources WHERE id=@id", new { id = sourceId });
        if (src is null) return new SyncResult(false, "source not found", 0, 0);

        var entries = await ytdlp.ListPlaylistAsync(src.Url, ct);
        var existing = c.Query<string>(
            "SELECT external_id FROM tracks WHERE source_id=@id AND external_id IS NOT NULL",
            new { id = sourceId }).ToHashSet();

        var added = 0;
        foreach (var e in entries)
        {
            if (existing.Contains(e.Id)) continue;
            c.Execute(@"
INSERT OR IGNORE INTO tracks (source_id, external_id, raw_title, artist, title, duration_sec, state, updated_at)
VALUES (@sid, @eid, @raw, @artist, @title, @dur, 'Pending', datetime('now'))",
                new { sid = sourceId, eid = e.Id, raw = e.Title, artist = e.Channel, title = e.Title, dur = e.Duration });
            added++;
        }

        c.Execute("UPDATE sources SET last_run_at=datetime('now') WHERE id=@id", new { id = sourceId });
        log.LogInformation("Sync source {Id}: total={Total}, added={Added}", sourceId, entries.Count, added);
        return new SyncResult(true, null, entries.Count, added);
    }

    // Phase 2: enrich with authoritative metadata. Best-effort, one video at a time, CAPPED.
    // In prod it is called per missing track before download (M3), not over the whole library.
    public async Task<int> EnrichAsync(long sourceId, int limit = 25, CancellationToken ct = default)
    {
        using var c = db.Open();
        var todo = c.Query<Track>(
            "SELECT * FROM tracks WHERE source_id=@id AND enriched=0 AND external_id IS NOT NULL ORDER BY id LIMIT @lim",
            new { id = sourceId, lim = limit }).ToList();

        foreach (var t in todo)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var m = await ytdlp.GetMetaAsync(t.ExternalId!, ct);
                using var cc = db.Open();
                cc.Execute(@"
UPDATE tracks SET
    artist       = COALESCE(@artist, artist),
    title        = COALESCE(@track,  title),
    album        = @album,
    duration_sec = COALESCE(@dur, duration_sec),
    enriched     = 1,
    updated_at   = datetime('now')
WHERE id=@id",
                    new { artist = m?.Artist, track = m?.Track, album = m?.Album, dur = m?.Duration, id = t.Id });
            }
            catch (Exception ex)
            {
                log.LogWarning("Enrich failed for {Ext}: {Msg}", t.ExternalId, ex.Message);
                // mark as processed so we don't loop forever; flat-mode data stays
                using var cc = db.Open();
                cc.Execute("UPDATE tracks SET enriched=1 WHERE id=@id", new { id = t.Id });
            }
        }
        log.LogInformation("Enrich source {Id}: processed {Count}", sourceId, todo.Count);
        return todo.Count;
    }
}
