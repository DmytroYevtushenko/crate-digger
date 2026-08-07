using Dapper;

namespace Crate.Api;

/// <summary>
/// Re-runs the same Verifier used right after a download against tracks that are already
/// Manual (linked to a library file by reconcile or a manual match) — catches cases where
/// a fuzzy match landed on the wrong version (e.g. a remix instead of the original). A pass
/// moves the track to Verified (confirmed good); a mismatch flags it as ManualReview for the
/// user to resolve via Resolve().
/// </summary>
public sealed class ManualVerifyService(Db db, Verifier verifier, YtDlp ytdlp, ReconcileService reconcile, ILogger<ManualVerifyService> log)
{
    private volatile bool _running;
    private string? _last;

    public bool Running => _running;
    public string? LastResult => _last;

    public bool Start()
    {
        if (_running) return false;
        _running = true;
        _ = Task.Run(async () =>
        {
            try { await RunAsync(); }
            catch (Exception ex) { log.LogError(ex, "manual verify failed"); _last = "error: " + ex.Message; }
            finally { _running = false; }
        });
        return true;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        List<Track> manual;
        using (var c = db.Open())
            manual = c.Query<Track>("SELECT * FROM tracks WHERE state='Manual'").ToList();

        int checkedCount = 0, flagged = 0, linked = 0, requeued = 0;
        foreach (var t in manual)
        {
            if (ct.IsCancellationRequested) break;

            // sldl decides "already present" by title alone (ignoring the artist), so it reports
            // tracks as owned that aren't — leaving them Manual with no file_path, never downloaded
            // and never checked. Trust our own artist+duration-confirmed index instead: link the file
            // if it really is there, otherwise the track simply isn't owned — requeue it. There is no
            // file to judge, so this needs no manual review.
            if (t.FilePath is null)
            {
                t.FilePath = reconcile.LinkExistingFile(t.Id);
                if (t.FilePath is null)
                {
                    SetState(t.Id, "Pending");
                    Record(t.Id, "ManualNotOwned", "reported as already in your library, but it isn't there — queued for download");
                    requeued++;
                    continue;
                }
                linked++;
            }

            // Manual tracks never went through the download pipeline's enrichment step, so their
            // artist/title are often still the raw YouTube channel/video title (e.g. "officialddt"
            // instead of "ДДТ") — that alone makes the Verifier's tag check flag a correct match as
            // a mismatch. Enrich once here, same call the downloader already makes, before comparing.
            if (!t.Enriched && t.ExternalId is not null)
                await EnrichAsync(t, ct);

            VerifyResult v;
            try { v = await verifier.VerifyAsync(t.FilePath!, t, ct); }
            catch (Exception ex) { log.LogWarning("manual verify failed for track {Id}: {Msg}", t.Id, ex.Message); continue; }
            checkedCount++;

            if (v.Outcome == VerifyOutcome.Mismatch)
            {
                SetState(t.Id, "ManualReview");
                Record(t.Id, "ManualMismatch", v.Detail);
                flagged++;
            }
            else
            {
                // Passed on its own — counts the same as a manual "keep": confirmed, no longer
                // just "in library, unchecked".
                SetState(t.Id, "Verified");
                Record(t.Id, "ManualVerified", v.Detail);
            }
        }

        _last = $"checked {checkedCount}, linked {linked}, requeued {requeued}, flagged {flagged} for review";
        log.LogInformation("Manual verify: {Res}", _last);
    }

    private void SetState(long id, string state)
    {
        using var c = db.Open();
        c.Execute("UPDATE tracks SET state=@s, updated_at=datetime('now') WHERE id=@id", new { s = state, id });
    }

    private void Record(long id, string result, string? detail)
    {
        using var c = db.Open();
        c.Execute(@"INSERT INTO download_attempts(track_id, started_at, finished_at, result, failure_reason)
                    VALUES(@id, datetime('now'), datetime('now'), @r, @d)",
            new { id, r = result, d = detail });
    }

    // Same enrichment SyncService.EnrichAsync/Downloader already do per-track — pulls authoritative
    // artist/track/album from YouTube Music and updates both the DB row and the in-memory track.
    private async Task EnrichAsync(Track t, CancellationToken ct)
    {
        try
        {
            var m = await ytdlp.GetMetaAsync(t.ExternalId!, ct);
            using var c = db.Open();
            c.Execute(@"UPDATE tracks SET
    artist       = COALESCE(@artist, artist),
    title        = COALESCE(@track,  title),
    album        = @album,
    duration_sec = COALESCE(@dur, duration_sec),
    enriched     = 1,
    updated_at   = datetime('now')
WHERE id=@id",
                new { artist = m?.Artist, track = m?.Track, album = m?.Album, dur = m?.Duration, id = t.Id });
            t.Artist = m?.Artist ?? t.Artist;
            t.Title = m?.Track ?? t.Title;
            t.Album = m?.Album;
            t.DurationSec = m?.Duration ?? t.DurationSec;
        }
        catch (Exception ex)
        {
            log.LogWarning("manual-verify enrich failed for {Ext}: {Msg}", t.ExternalId, ex.Message);
            using var c = db.Open();
            c.Execute("UPDATE tracks SET enriched=1 WHERE id=@id", new { id = t.Id });
        }
    }

    // Resolve a ManualReview track per the user's decision:
    //   keep           - you listened and confirmed it's the right track; counts as verified.
    //   keep-download  - keep the file in the library untouched, but re-download the track
    //                    fresh; the file is remembered so fuzzy matching won't re-link it.
    //   delete-download- remove the file and re-download the track fresh.
    public (bool Ok, string? State, string? Error) Resolve(long trackId, string decision)
    {
        using var c = db.Open();
        var t = c.QuerySingleOrDefault<Track>("SELECT * FROM tracks WHERE id=@id", new { id = trackId });
        if (t is null) return (false, null, "track not found");
        if (t.State != "ManualReview") return (false, null, "track is not in ManualReview");

        switch (decision)
        {
            case "keep":
                if (t.FilePath is null) return (false, null, "no file is linked to this track — nothing to keep");
                c.Execute("UPDATE tracks SET state='Verified', updated_at=datetime('now') WHERE id=@id", new { id = trackId });
                return (true, "Verified", null);

            case "keep-download":
                if (t.FilePath is { } keepPath)
                {
                    c.Execute("INSERT OR IGNORE INTO track_ignored_files(track_id, path) VALUES(@id, @p)", new { id = trackId, p = keepPath });
                    c.Execute("UPDATE library_files SET matched_track_id=NULL WHERE path=@p", new { p = keepPath });
                }
                c.Execute("UPDATE tracks SET state='Pending', file_path=NULL, updated_at=datetime('now') WHERE id=@id", new { id = trackId });
                return (true, "Pending", null);

            case "delete-download":
                if (t.FilePath is { } delPath)
                {
                    try { if (File.Exists(delPath)) File.Delete(delPath); }
                    catch (Exception ex) { log.LogWarning("delete failed for {Path}: {Msg}", delPath, ex.Message); }
                    c.Execute("DELETE FROM library_files WHERE path=@p", new { p = delPath });
                }
                c.Execute("UPDATE tracks SET state='Pending', file_path=NULL, updated_at=datetime('now') WHERE id=@id", new { id = trackId });
                return (true, "Pending", null);

            default:
                return (false, null, "unknown decision");
        }
    }
}
