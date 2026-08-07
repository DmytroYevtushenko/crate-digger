using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Crate.Api;

/// <summary>
/// Scans the master library + inbox (recursively), reads tags/duration via ffprobe into
/// library_files (incremental by path+mtime+size), then matches files to tracks so that
/// tracks you already own become Manual (and aren't downloaded again).
///
/// Matching is fuzzy (see FuzzyText): both sides are cleaned of noise (- Topic, feat., brackets,
/// years, remaster, official/video/lyrics), Cyrillic-transliterated, and compared by word overlap
/// (Jaccard) with duration as a confirming signal. This bridges messy YouTube titles vs clean
/// Picard tags.
/// </summary>
public sealed class ReconcileService(Db db, IConfiguration cfg, ILogger<ReconcileService> log)
{
    private static readonly string[] AudioExt = [".flac", ".mp3", ".m4a", ".ogg", ".opus", ".wav"];
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
            catch (Exception ex) { log.LogError(ex, "reconcile failed"); _last = "error: " + ex.Message; }
            finally { _running = false; }
        });
        return true;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        int scanned = 0, skipped = 0;
        foreach (var root in Roots())
        {
            foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                if (ct.IsCancellationRequested) break;
                if (!AudioExt.Contains(Path.GetExtension(f).ToLowerInvariant())) continue;

                var fi = new FileInfo(f);
                var mtime = fi.LastWriteTimeUtc.Ticks;
                var size = fi.Length;

                using var c = db.Open();
                var ex = c.QueryFirstOrDefault("SELECT mtime, size FROM library_files WHERE path=@p", new { p = f });
                if (ex is not null && (long?)ex.mtime == mtime && (long?)ex.size == size) { skipped++; continue; }

                var (artist, title, _, dur, bitrate) = await ProbeAsync(f, ct);
                c.Execute(@"
INSERT INTO library_files (path, artist, title, duration_sec, bitrate_kbps, mtime, size, scanned_at)
VALUES (@p, @a, @t, @d, @b, @m, @s, datetime('now'))
ON CONFLICT(path) DO UPDATE SET
    artist=@a, title=@t, duration_sec=@d, bitrate_kbps=@b, mtime=@m, size=@s, scanned_at=datetime('now')",
                    new { p = f, a = artist, t = title, d = dur, b = bitrate, m = mtime, s = size });
                scanned++;
            }
        }

        var matched = Match();
        using (var c = db.Open()) BackfillTrackStats(c);
        _last = $"scanned {scanned}, unchanged {skipped}, newly matched {matched}";
        log.LogInformation("Reconcile: {Res}", _last);
    }

    /// <summary>
    /// Indexes one freshly downloaded file into library_files right away (tags, duration, bitrate)
    /// instead of waiting for the next full scan, and links it to the track. Returns its bitrate and size.
    /// </summary>
    public async Task<(int? Bitrate, long? Size)> IndexFileAsync(string path, long trackId, CancellationToken ct = default)
    {
        if (!File.Exists(path)) return (null, null);
        var (artist, title, _, dur, bitrate) = await ProbeAsync(path, ct);
        var fi = new FileInfo(path);

        using var c = db.Open();
        c.Execute(@"
INSERT INTO library_files (path, artist, title, duration_sec, bitrate_kbps, mtime, size, matched_track_id, scanned_at)
VALUES (@p, @a, @t, @d, @b, @m, @s, @tid, datetime('now'))
ON CONFLICT(path) DO UPDATE SET
    artist=@a, title=@t, duration_sec=@d, bitrate_kbps=@b, mtime=@m, size=@s,
    matched_track_id=@tid, scanned_at=datetime('now')",
            new { p = path, a = artist, t = title, d = dur, b = bitrate,
                  m = fi.LastWriteTimeUtc.Ticks, s = fi.Length, tid = trackId });
        return (bitrate, fi.Length);
    }

    // Carries the indexed file's bitrate and size onto the track in the same UPDATE that sets file_path.
    private const string StatsFromLib =
        "bitrate_kbps=(SELECT bitrate_kbps FROM library_files WHERE path=@p), " +
        "size_bytes=(SELECT size FROM library_files WHERE path=@p), ";

    // Fill in bitrate/size for tracks whose file is already indexed (e.g. linked before this was tracked).
    private static void BackfillTrackStats(SqliteConnection c) =>
        c.Execute(@"
UPDATE tracks SET
    bitrate_kbps = COALESCE(bitrate_kbps, (SELECT lf.bitrate_kbps FROM library_files lf WHERE lf.path = tracks.file_path)),
    size_bytes   = COALESCE(size_bytes,   (SELECT lf.size         FROM library_files lf WHERE lf.path = tracks.file_path))
WHERE file_path IS NOT NULL AND (bitrate_kbps IS NULL OR size_bytes IS NULL)");

    /// <summary>
    /// Breaks the link between a track and its file without touching the file itself, and queues the
    /// track for a fresh download. The rejected path is remembered so matching won't re-link it.
    /// This is the safe way to swap versions when the linked file lives in the master library.
    /// </summary>
    public (bool Ok, string? Error) UnlinkFile(long trackId)
    {
        using var c = db.Open();
        var t = c.QuerySingleOrDefault<Track>("SELECT * FROM tracks WHERE id=@id", new { id = trackId });
        if (t is null) return (false, "track not found");
        if (t.FilePath is null) return (false, "track has no linked file");

        c.Execute("INSERT OR IGNORE INTO track_ignored_files(track_id, path) VALUES(@id, @p)", new { id = trackId, p = t.FilePath });
        c.Execute("UPDATE library_files SET matched_track_id=NULL WHERE path=@p", new { p = t.FilePath });
        c.Execute(@"UPDATE tracks SET state='Pending', file_path=NULL, bitrate_kbps=NULL, size_bytes=NULL,
                    updated_at=datetime('now') WHERE id=@id", new { id = trackId });
        log.LogInformation("Unlinked track {Id} from {Path} (kept on disk) => Pending", trackId, t.FilePath);
        return (true, null);
    }

    /// <summary>
    /// Deletes a track's file from disk and drops it from the library index. The track then either
    /// goes back in the download queue (blacklist=false) or is never fetched again (blacklist=true).
    /// Used to get rid of a bad-quality or plain wrong file that a match/download landed on.
    /// </summary>
    public (bool Ok, string? State, string? Error) DeleteFile(long trackId, bool blacklist)
    {
        using var c = db.Open();
        var t = c.QuerySingleOrDefault<Track>("SELECT * FROM tracks WHERE id=@id", new { id = trackId });
        if (t is null) return (false, null, "track not found");
        if (t.FilePath is null) return (false, null, "track has no file to delete");

        try { if (File.Exists(t.FilePath)) File.Delete(t.FilePath); }
        catch (Exception ex)
        {
            log.LogWarning("delete failed for {Path}: {Msg}", t.FilePath, ex.Message);
            return (false, null, $"could not delete the file: {ex.Message}");
        }

        c.Execute("DELETE FROM library_files WHERE path=@p", new { p = t.FilePath });
        var state = blacklist ? "Blacklisted" : "Pending";
        c.Execute(@"UPDATE tracks SET state=@s, file_path=NULL, bitrate_kbps=NULL, size_bytes=NULL,
                    updated_at=datetime('now') WHERE id=@id", new { s = state, id = trackId });
        log.LogInformation("Deleted file for track {Id} ({Path}) => {State}", trackId, t.FilePath, state);
        return (true, state, null);
    }

    private sealed record FileProfile(long Id, string Path, HashSet<string> Title, HashSet<string> Artist, string? ArtistRaw, int? Duration);

    // The shared auto-match rule: strong title overlap, confirmed by duration OR artist, and never
    // over a plain artist contradiction. Title+duration alone is not enough — a common short title
    // ("Restless") plus a coincidental runtime matched a track by the band "untitled" to Alison
    // Krauss & Union Station, which then blocked the real song from ever downloading.
    private static FileProfile? BestMatch(Track t, List<FileProfile> profiles, Func<string, bool> isIgnored)
    {
        var tTitle = FuzzyText.Tokens(t.Title ?? t.RawTitle);
        if (tTitle.Count == 0) return null;
        var tArtist = FuzzyText.Tokens(t.Artist);

        FileProfile? best = null;
        double bestScore = 0;
        foreach (var f in profiles)
        {
            if (isIgnored(f.Path)) continue;
            var tj = FuzzyText.Jaccard(tTitle, f.Title);
            if (tj < 0.6) continue;
            if (FuzzyText.Conflict(t.Artist, f.ArtistRaw)) continue; // different artist — not the same song
            var durClose = t.DurationSec is not null && f.Duration is not null
                           && Math.Abs(t.DurationSec.Value - f.Duration.Value) <= 7;
            var aj = FuzzyText.Jaccard(tArtist, f.Artist);
            if (!durClose && aj < 0.34) continue; // need duration OR artist to confirm
            var score = tj + (durClose ? 0.3 : 0) + aj * 0.3;
            if (score > bestScore) { bestScore = score; best = f; }
        }
        return best;
    }

    private static List<FileProfile> Profiles(SqliteConnection c) =>
        c.Query<LibraryFile>("SELECT * FROM library_files")
            .Select(f => new FileProfile(f.Id, f.Path, FuzzyText.Tokens(f.Title), FuzzyText.Tokens(f.Artist), f.Artist, f.DurationSec))
            .Where(p => p.Title.Count > 0)
            .ToList();

    // Fuzzy-match unmatched library files to Pending/Failed tracks.
    private int Match()
    {
        using var c = db.Open();
        var profiles = Profiles(c);
        var tracks = c.Query<Track>("SELECT * FROM tracks WHERE state IN ('Pending','Failed')").ToList();

        // Paths a manual-review decision already rejected for a given track — never re-offer them.
        var ignored = c.Query("SELECT track_id, path FROM track_ignored_files")
            .Select(r => ((long)r.track_id, (string)r.path))
            .ToHashSet();

        var matched = 0;
        foreach (var t in tracks)
        {
            var best = BestMatch(t, profiles, p => ignored.Contains((t.Id, p)));
            if (best is null) continue;

            c.Execute("UPDATE tracks SET state='Manual', file_path=@p, " + StatsFromLib + "updated_at=datetime('now') WHERE id=@id",
                new { p = best.Path, id = t.Id });
            c.Execute("UPDATE library_files SET matched_track_id=@tid WHERE id=@fid", new { tid = t.Id, fid = best.Id });
            matched++;
        }
        return matched;
    }

    /// <summary>
    /// Locates the library file for a track that is believed to already be present but has no
    /// file_path — e.g. sldl ended the attempt with "already present" without reporting which file
    /// it matched. Links the file (path + matched_track_id) but leaves the track's state alone.
    /// Returns the path, or null if nothing in the library matches.
    /// </summary>
    public string? LinkExistingFile(long trackId)
    {
        using var c = db.Open();
        var t = c.QuerySingleOrDefault<Track>("SELECT * FROM tracks WHERE id=@id", new { id = trackId });
        if (t is null) return null;

        var ignored = c.Query<string>("SELECT path FROM track_ignored_files WHERE track_id=@id", new { id = trackId }).ToHashSet();
        var best = BestMatch(t, Profiles(c), ignored.Contains);
        if (best is null) return null;

        c.Execute("UPDATE tracks SET file_path=@p, " + StatsFromLib + "updated_at=datetime('now') WHERE id=@id", new { p = best.Path, id = trackId });
        c.Execute("UPDATE library_files SET matched_track_id=@id WHERE id=@fid", new { id = trackId, fid = best.Id });
        return best.Path;
    }

    // Fuzzy library candidates for a track (below the auto-match threshold) — for manual pick in the UI.
    public List<object> Candidates(long trackId, int top = 8)
    {
        using var c = db.Open();
        var t = c.QuerySingleOrDefault<Track>("SELECT * FROM tracks WHERE id=@id", new { id = trackId });
        if (t is null) return new List<object>();
        var tTitle = FuzzyText.Tokens(t.Title ?? t.RawTitle);
        var tArtist = FuzzyText.Tokens(t.Artist);
        var files = c.Query<LibraryFile>("SELECT * FROM library_files").ToList();
        return files
            .Select(f =>
            {
                var tj = FuzzyText.Jaccard(tTitle, FuzzyText.Tokens(f.Title));
                var aj = FuzzyText.Jaccard(tArtist, FuzzyText.Tokens(f.Artist));
                var durClose = t.DurationSec is not null && f.DurationSec is not null
                               && Math.Abs(t.DurationSec.Value - f.DurationSec.Value) <= 10;
                var score = Math.Round(tj * 0.7 + aj * 0.3 + (durClose ? 0.1 : 0), 3);
                return new { f.Path, f.Artist, f.Title, f.DurationSec, score };
            })
            .Where(x => x.score >= 0.15)
            .OrderByDescending(x => x.score)
            .Take(top)
            .Cast<object>()
            .ToList();
    }

    // Re-run matching for ALL Pending/Failed tracks against the current library index (no file re-scan).
    public int RematchAll() => Match();

    // Re-run the auto-match for a single track (e.g. right after the user fixed its tags).
    // Returns true if it matched a library file and moved the track to Manual.
    public bool RematchOne(long trackId)
    {
        using var c = db.Open();
        var t = c.QuerySingleOrDefault<Track>("SELECT * FROM tracks WHERE id=@id", new { id = trackId });
        if (t is null) return false;
        // Only tracks with no file yet are eligible — editing tags on a track that's already
        // Manual/ManualReview/Verified/etc. must not silently re-link or flip its state/review queue.
        if (t.State is not ("Pending" or "Failed")) return false;

        var ignored = c.Query<string>("SELECT path FROM track_ignored_files WHERE track_id=@id", new { id = trackId }).ToHashSet();
        var best = BestMatch(t, Profiles(c), ignored.Contains);
        if (best is null) return false;

        c.Execute("UPDATE tracks SET state='Manual', file_path=@p, " + StatsFromLib + "updated_at=datetime('now') WHERE id=@id", new { p = best.Path, id = trackId });
        c.Execute("UPDATE library_files SET matched_track_id=@id WHERE id=@fid", new { id = trackId, fid = best.Id });
        return true;
    }

    private IEnumerable<string> Roots()
    {
        var roots = new List<string>();
        if (cfg["MusicLibDir"] is { Length: > 0 } lib) roots.Add(lib);
        using (var c = db.Open())
            roots.AddRange(c.Query<string>("SELECT DISTINCT dest_dir FROM sources WHERE dest_dir IS NOT NULL"));
        return roots.Where(Directory.Exists).Distinct();
    }

    private async Task<(string?, string?, string?, int?, int?)> ProbeAsync(string path, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = cfg["FfprobePath"] ?? "ffprobe",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var a in new[] { "-v", "error", "-show_entries", "format=duration,bit_rate:format_tags=artist,title,album", "-of", "json", path })
                psi.ArgumentList.Add(a);

            using var p = Process.Start(psi) ?? throw new InvalidOperationException("cannot start ffprobe");
            var outp = await p.StandardOutput.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);

            using var doc = JsonDocument.Parse(outp);
            var fmt = doc.RootElement.GetProperty("format");
            int? dur = fmt.TryGetProperty("duration", out var dEl)
                       && double.TryParse(dEl.GetString(), CultureInfo.InvariantCulture, out var dd)
                ? (int)Math.Round(dd) : null;
            // ffprobe reports bit_rate in bits/s; store kbps to match how quality is talked about.
            int? bitrate = fmt.TryGetProperty("bit_rate", out var bEl)
                           && long.TryParse(bEl.GetString(), CultureInfo.InvariantCulture, out var bb)
                ? (int)(bb / 1000) : null;
            string? artist = null, title = null, album = null;
            if (fmt.TryGetProperty("tags", out var tags))
            {
                artist = Tag(tags, "artist");
                title = Tag(tags, "title");
                album = Tag(tags, "album");
            }
            return (artist, title, album, dur, bitrate);
        }
        catch (Exception ex)
        {
            log.LogWarning("ffprobe failed for {Path}: {Msg}", path, ex.Message);
            return (null, null, null, null, null);
        }
    }

    private static string? Tag(JsonElement tags, string key)
    {
        foreach (var prop in tags.EnumerateObject())
            if (string.Equals(prop.Name, key, StringComparison.OrdinalIgnoreCase))
                return prop.Value.GetString();
        return null;
    }
}
