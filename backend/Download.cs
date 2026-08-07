using Dapper;

namespace Crate.Api;

/// <summary>
/// Queues missing tracks (Pending/Failed) and runs them through sldl in the background.
/// Before downloading, enriches authoritative artist/track for a clean query.
/// After a successful download it verifies the file inline (see Verifier).
/// States: Queued -> Downloading -> Verified | Mismatch (quarantined) | Manual (already in lib) | Failed.
/// Stop() cancels the running queue (kills the in-flight sldl) and returns tracks to Pending.
/// </summary>
public sealed class Downloader(Db db, YtDlp ytdlp, SldlRunner sldl, Verifier verifier, Tagger tagger, ReconcileService reconcile, ILogger<Downloader> log)
{
    private CancellationTokenSource _cts = new();

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
        var token = _cts.Token;
        _ = Task.Run(() => ProcessAsync(sourceId, token));
        return ids.Count;
    }

    // Download a single track now, optionally overriding quality conditions.
    public void QueueOne(long trackId, string? cond, string? pref, out string? error)
    {
        error = null;
        using var c = db.Open();
        var t = c.QuerySingleOrDefault<Track>("SELECT * FROM tracks WHERE id=@id", new { id = trackId });
        if (t is null) { error = "track not found"; return; }
        var src = c.QuerySingleOrDefault<Source>("SELECT * FROM sources WHERE id=@id", new { id = t.SourceId });
        if (src is null) { error = "source not found"; return; }

        if (cond is not null) src.Cond = cond;
        if (!string.IsNullOrWhiteSpace(pref)) src.Pref = pref;

        c.Execute("UPDATE tracks SET state='Queued', updated_at=datetime('now') WHERE id=@id", new { id = trackId });
        var token = _cts.Token;
        _ = Task.Run(() => DownloadOneAsync(src, t, token));
    }

    // Cancel the running queue and return queued/in-flight tracks to Pending. Returns how many were requeued.
    public int Stop()
    {
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        using var c = db.Open();
        var n = c.Execute("UPDATE tracks SET state='Pending', updated_at=datetime('now') WHERE state IN ('Queued','Downloading')");
        log.LogInformation("Downloads stopped, {N} track(s) returned to Pending", n);
        return n;
    }

    private async Task ProcessAsync(long sourceId, CancellationToken ct)
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
            {
                if (ct.IsCancellationRequested) break;
                await DownloadOneAsync(src, t, ct);
            }
        }
        catch (OperationCanceledException) { log.LogInformation("download batch cancelled for source {Id}", sourceId); }
        catch (Exception ex) { log.LogError(ex, "download batch failed for source {Id}", sourceId); }
    }

    private async Task DownloadOneAsync(Source src, Track t, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;
        SetState(t.Id, "Downloading");

        var artist = t.Artist;
        var title = t.Title;

        // Targeted enrichment (authoritative artist/track/album) — only for this track.
        if (!t.Enriched && t.ExternalId is not null)
        {
            try
            {
                var m = await ytdlp.GetMetaAsync(t.ExternalId, ct);
                if (m is not null)
                {
                    artist = m.Artist ?? artist;
                    title = m.Track ?? title;
                    using var c = db.Open();
                    c.Execute(@"UPDATE tracks SET artist=COALESCE(@a,artist), title=COALESCE(@t,title),
                                album=@al, duration_sec=COALESCE(@d,duration_sec), enriched=1 WHERE id=@id",
                        new { a = m.Artist, t = m.Track, al = m.Album, d = m.Duration, id = t.Id });
                    t.Artist = artist; t.Title = title; t.Album = m.Album; t.DurationSec = m.Duration ?? t.DurationSec;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { log.LogWarning("enrich-on-download failed {Ext}: {Msg}", t.ExternalId, ex.Message); }
        }

        using (var c = db.Open())
            c.Execute("INSERT INTO download_attempts(track_id, started_at) VALUES(@id, datetime('now'))", new { id = t.Id });

        var res = await sldl.DownloadAsync(artist ?? "", title, t.ExpectedLenSec ?? t.DurationSec, src, ct);

        string state;
        var finalPath = res.Path;
        var detail = res.Detail;

        switch (res.Outcome)
        {
            case DlOutcome.Downloaded:
                var v = await verifier.VerifyAsync(res.Path!, t, ct);
                detail = v.Detail;
                if (v.Outcome == VerifyOutcome.Mismatch)
                {
                    finalPath = Quarantine(res.Path!, src);
                    state = "Mismatch";
                }
                else
                {
                    state = "Verified";
                    await tagger.TagAsync(res.Path!, t, ct); // Picard-lite: write clean tags into the file
                }
                break;
            case DlOutcome.AlreadyExists:
                // sldl says the file is already there but won't say which one. Only trust that if our
                // own index can point at a real file — otherwise the track would sit "in library" with
                // nothing behind it, never downloaded and never checked. Unconfirmed => still missing.
                finalPath = reconcile.LinkExistingFile(t.Id);
                state = finalPath is null ? "Failed" : "Manual";
                if (finalPath is null) detail = "sldl reported already present, but no matching file is in your library";
                break;
            default:
                state = "Failed";
                break;
        }

        // Index the new file right away so its bitrate is known and it's matchable without a full rescan.
        int? bitrate = null;
        if (finalPath is not null && res.Outcome == DlOutcome.Downloaded)
            bitrate = await reconcile.IndexFileAsync(finalPath, t.Id, ct);

        using (var c = db.Open())
        {
            c.Execute("UPDATE tracks SET state=@s, file_path=@p, bitrate_kbps=COALESCE(@b, bitrate_kbps), updated_at=datetime('now') WHERE id=@id",
                new { s = state, p = finalPath, b = bitrate, id = t.Id });
            c.Execute(@"UPDATE download_attempts SET finished_at=datetime('now'), result=@r, failure_reason=@f
                        WHERE id=(SELECT MAX(id) FROM download_attempts WHERE track_id=@id)",
                new { r = res.Outcome.ToString(), f = detail, id = t.Id });
        }

        log.LogInformation("Track {Id} '{Artist} - {Title}' => {State}{Detail}", t.Id, artist, title, state,
            string.IsNullOrEmpty(detail) ? "" : $" ({detail})");
    }

    /// <summary>
    /// What YouTube has for this track, so the UI can show the quality before you commit to it.
    /// </summary>
    public async Task<(YtAudio? Audio, string? Error)> YoutubeQualityAsync(long trackId, CancellationToken ct = default)
    {
        using var c = db.Open();
        var t = c.QuerySingleOrDefault<Track>("SELECT * FROM tracks WHERE id=@id", new { id = trackId });
        if (t is null) return (null, "track not found");
        if (t.ExternalId is null) return (null, "track has no YouTube source");
        try { return (await ytdlp.GetBestAudioAsync(t.ExternalId, ct), null); }
        catch (Exception ex) { return (null, ex.Message); }
    }

    /// <summary>
    /// Fallback source for tracks Soulseek doesn't carry: pull the audio from the YouTube video the
    /// track came from. Lossy by nature, so the resulting bitrate is recorded on the track.
    /// </summary>
    public async Task<(bool Ok, string? Path, int? Bitrate, string? Error)> DownloadFromYoutubeAsync(long trackId, CancellationToken ct = default)
    {
        Track t;
        Source src;
        using (var c = db.Open())
        {
            var track = c.QuerySingleOrDefault<Track>("SELECT * FROM tracks WHERE id=@id", new { id = trackId });
            if (track is null) return (false, null, null, "track not found");
            if (track.ExternalId is null) return (false, null, null, "track has no YouTube source");
            var source = c.QuerySingleOrDefault<Source>("SELECT * FROM sources WHERE id=@id", new { id = track.SourceId });
            if (source is null) return (false, null, null, "source not found");
            t = track; src = source;
        }

        SetState(t.Id, "Downloading");
        using (var c = db.Open())
            c.Execute("INSERT INTO download_attempts(track_id, started_at) VALUES(@id, datetime('now'))", new { id = t.Id });

        string? path;
        try
        {
            var stem = string.Join(" - ", new[] { t.Artist, t.Title }.Where(s => !string.IsNullOrWhiteSpace(s)));
            path = await ytdlp.DownloadAudioAsync(t.ExternalId!, src.DestDir, stem, ct);
        }
        catch (Exception ex)
        {
            Finish(t.Id, "Failed", null, null, "YoutubeError", ex.Message);
            return (false, null, null, ex.Message);
        }

        if (path is null)
        {
            Finish(t.Id, "Failed", null, null, "YoutubeError", "yt-dlp produced no audio file");
            return (false, null, null, "yt-dlp produced no audio file");
        }

        await tagger.TagAsync(path, t, ct);
        var bitrate = await reconcile.IndexFileAsync(path, t.Id, ct);
        // The YouTube video *is* the track's source of truth here, so there is nothing to verify it
        // against — a fingerprint check would just compare the file to itself.
        Finish(t.Id, "Verified", path, bitrate, "YoutubeDownloaded", $"from YouTube{(bitrate is null ? "" : $" ~{bitrate} kbps")}");
        log.LogInformation("Track {Id} '{Artist} - {Title}' downloaded from YouTube ({Br} kbps)", t.Id, t.Artist, t.Title, bitrate);
        return (true, path, bitrate, null);
    }

    private void Finish(long id, string state, string? path, int? bitrate, string result, string? detail)
    {
        using var c = db.Open();
        c.Execute("UPDATE tracks SET state=@s, file_path=COALESCE(@p, file_path), bitrate_kbps=COALESCE(@b, bitrate_kbps), updated_at=datetime('now') WHERE id=@id",
            new { s = state, p = path, b = bitrate, id });
        c.Execute(@"UPDATE download_attempts SET finished_at=datetime('now'), result=@r, failure_reason=@f
                    WHERE id=(SELECT MAX(id) FROM download_attempts WHERE track_id=@id)",
            new { r = result, f = detail, id });
    }

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
