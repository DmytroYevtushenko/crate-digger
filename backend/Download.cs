using Dapper;

namespace Crate.Api;

/// <summary>
/// Queues missing tracks (Pending/Failed) and runs them through sldl in the background.
/// Before downloading, enriches authoritative artist/track for a clean query.
/// After a successful download it verifies the file inline (see Verifier).
/// States: Queued -> Downloading -> Verified | Mismatch (quarantined) | Manual (already in lib) | Failed.
/// </summary>
public sealed class Downloader(Db db, YtDlp ytdlp, SldlRunner sldl, Verifier verifier, Tagger tagger, ILogger<Downloader> log)
{
    public int Queue(long sourceId, int limit, out string? error)
    {
        error = null;
        using var c = db.Open();
        var src = c.QuerySingleOrDefault<Source>("SELECT * FROM sources WHERE id=@id", new { id = sourceId });
        if (src is null) { error = "source not found"; return 0; }

        var ids = c.Query<long>(
            "SELECT id FROM tracks WHERE source_id=@id AND state IN ('Pending','Failed') ORDER BY id LIMIT @lim",
            new { id = sourceId, lim = limit }).ToList();
        if (ids.Count == 0) return 0;

        c.Execute("UPDATE tracks SET state='Queued', updated_at=datetime('now') WHERE id IN @ids", new { ids });
        _ = Task.Run(() => ProcessAsync(sourceId));
        return ids.Count;
    }

    private async Task ProcessAsync(long sourceId)
    {
        try
        {
            Source src;
            List<Track> queued;
            using (var c = db.Open())
            {
                src = c.QuerySingle<Source>("SELECT * FROM sources WHERE id=@id", new { id = sourceId });
                queued = c.Query<Track>(
                    "SELECT * FROM tracks WHERE source_id=@id AND state='Queued' ORDER BY id",
                    new { id = sourceId }).ToList();
            }
            foreach (var t in queued)
                await DownloadOneAsync(src, t);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "download batch failed for source {Id}", sourceId);
        }
    }

    private async Task DownloadOneAsync(Source src, Track t)
    {
        SetState(t.Id, "Downloading");

        var artist = t.Artist;
        var title = t.Title;

        // Targeted enrichment (authoritative artist/track/album) — only for this track.
        if (!t.Enriched && t.ExternalId is not null)
        {
            try
            {
                var m = await ytdlp.GetMetaAsync(t.ExternalId);
                if (m is not null)
                {
                    artist = m.Artist ?? artist;
                    title = m.Track ?? title;
                    using var c = db.Open();
                    c.Execute(@"UPDATE tracks SET artist=COALESCE(@a,artist), title=COALESCE(@t,title),
                                album=@al, duration_sec=COALESCE(@d,duration_sec), enriched=1 WHERE id=@id",
                        new { a = m.Artist, t = m.Track, al = m.Album, d = m.Duration, id = t.Id });
                    // reflect enriched values for verification below
                    t.Artist = artist; t.Title = title;
                    t.Album = m.Album; t.DurationSec = m.Duration ?? t.DurationSec;
                }
            }
            catch (Exception ex)
            {
                log.LogWarning("enrich-on-download failed {Ext}: {Msg}", t.ExternalId, ex.Message);
            }
        }

        using (var c = db.Open())
            c.Execute("INSERT INTO download_attempts(track_id, started_at) VALUES(@id, datetime('now'))", new { id = t.Id });

        var res = await sldl.DownloadAsync(artist ?? "", title, src);

        string state;
        var finalPath = res.Path;
        var detail = res.Detail;

        switch (res.Outcome)
        {
            case DlOutcome.Downloaded:
                var v = await verifier.VerifyAsync(res.Path!, t);
                detail = v.Detail;
                if (v.Outcome == VerifyOutcome.Mismatch)
                {
                    finalPath = Quarantine(res.Path!, src);
                    state = "Mismatch";
                }
                else
                {
                    state = "Verified";
                    await tagger.TagAsync(res.Path!, t); // Picard-lite: write clean tags into the file
                }
                break;
            case DlOutcome.AlreadyExists:
                state = "Manual";
                break;
            default:
                state = "Failed";
                break;
        }

        using (var c = db.Open())
        {
            c.Execute("UPDATE tracks SET state=@s, file_path=@p, updated_at=datetime('now') WHERE id=@id",
                new { s = state, p = finalPath, id = t.Id });
            c.Execute(@"UPDATE download_attempts SET finished_at=datetime('now'), result=@r, failure_reason=@f
                        WHERE id=(SELECT MAX(id) FROM download_attempts WHERE track_id=@id)",
                new { r = res.Outcome.ToString(), f = detail, id = t.Id });
        }

        log.LogInformation("Track {Id} '{Artist} - {Title}' => {State} ({Detail})", t.Id, artist, title, state, detail);
    }

    // Move a mismatched file out of the inbox into <dest>/_mismatch so it isn't mistaken for a good download.
    private string? Quarantine(string path, Source src)
    {
        try
        {
            var qdir = Path.Combine(src.DestDir, "_mismatch");
            Directory.CreateDirectory(qdir);
            var dest = Path.Combine(qdir, Path.GetFileName(path));
            File.Move(path, dest, overwrite: true);
            return dest;
        }
        catch (Exception ex)
        {
            log.LogWarning("quarantine move failed for {Path}: {Msg}", path, ex.Message);
            return path;
        }
    }

    private void SetState(long id, string state)
    {
        using var c = db.Open();
        c.Execute("UPDATE tracks SET state=@s, updated_at=datetime('now') WHERE id=@id", new { s = state, id });
    }
}
