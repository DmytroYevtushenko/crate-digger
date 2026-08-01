using Dapper;

namespace Crate.Api;

/// <summary>
/// Re-runs the same Verifier used right after a download against tracks that are already
/// Manual (linked to a library file by reconcile or a manual match) — catches cases where
/// a fuzzy match landed on the wrong version (e.g. a remix instead of the original).
/// Flags mismatches as ManualReview for the user to resolve via Resolve().
/// </summary>
public sealed class ManualVerifyService(Db db, Verifier verifier, ILogger<ManualVerifyService> log)
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
            manual = c.Query<Track>("SELECT * FROM tracks WHERE state='Manual' AND file_path IS NOT NULL").ToList();

        int checkedCount = 0, flagged = 0;
        foreach (var t in manual)
        {
            if (ct.IsCancellationRequested) break;

            VerifyResult v;
            try { v = await verifier.VerifyAsync(t.FilePath!, t, ct); }
            catch (Exception ex) { log.LogWarning("manual verify failed for track {Id}: {Msg}", t.Id, ex.Message); continue; }
            checkedCount++;

            if (v.Outcome == VerifyOutcome.Mismatch)
            {
                using var c = db.Open();
                c.Execute("UPDATE tracks SET state='ManualReview', updated_at=datetime('now') WHERE id=@id", new { id = t.Id });
                c.Execute(@"INSERT INTO download_attempts(track_id, started_at, finished_at, result, failure_reason)
                            VALUES(@id, datetime('now'), datetime('now'), 'ManualMismatch', @d)",
                    new { id = t.Id, d = v.Detail });
                flagged++;
            }
        }

        _last = $"checked {checkedCount}, flagged {flagged} for review";
        log.LogInformation("Manual verify: {Res}", _last);
    }

    // Resolve a ManualReview track per the user's decision:
    //   keep           - verifier was wrong; dismiss, stays linked to the same file.
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
                c.Execute("UPDATE tracks SET state='Manual', updated_at=datetime('now') WHERE id=@id", new { id = trackId });
                return (true, "Manual", null);

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
