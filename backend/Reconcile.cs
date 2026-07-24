using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;

namespace Crate.Api;

/// <summary>
/// Scans the master library + inbox (recursively), reads tags/duration via ffprobe into
/// library_files (incremental by path+mtime+size), then matches files to tracks so that
/// tracks you already own become Manual (and aren't downloaded again).
///
/// Matching is fuzzy: both sides are cleaned of noise (- Topic, feat., brackets, years,
/// remaster, official/video/lyrics), tokenized, and compared by word overlap (Jaccard) with
/// duration as a confirming signal. This bridges messy YouTube titles vs clean Picard tags.
/// (Transliteration — Cyrillic vs Latin — has no word overlap, so those need a manual track edit.)
/// </summary>
public sealed partial class ReconcileService(Db db, IConfiguration cfg, ILogger<ReconcileService> log)
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

                var (artist, title, _, dur) = await ProbeAsync(f, ct);
                c.Execute(@"
INSERT INTO library_files (path, artist, title, duration_sec, mtime, size, scanned_at)
VALUES (@p, @a, @t, @d, @m, @s, datetime('now'))
ON CONFLICT(path) DO UPDATE SET
    artist=@a, title=@t, duration_sec=@d, mtime=@m, size=@s, scanned_at=datetime('now')",
                    new { p = f, a = artist, t = title, d = dur, m = mtime, s = size });
                scanned++;
            }
        }

        var matched = Match();
        _last = $"scanned {scanned}, unchanged {skipped}, newly matched {matched}";
        log.LogInformation("Reconcile: {Res}", _last);
    }

    private sealed record FileProfile(long Id, string Path, HashSet<string> Title, HashSet<string> Artist, int? Duration);

    // Fuzzy-match unmatched library files to Pending/Failed tracks.
    private int Match()
    {
        using var c = db.Open();
        var files = c.Query<LibraryFile>("SELECT * FROM library_files WHERE matched_track_id IS NULL").ToList();
        var tracks = c.Query<Track>("SELECT * FROM tracks WHERE state IN ('Pending','Failed')").ToList();

        var profiles = files
            .Select(f => new FileProfile(f.Id, f.Path, Tokens(f.Title), Tokens(f.Artist), f.DurationSec))
            .Where(p => p.Title.Count > 0)
            .ToList();

        var matched = 0;
        foreach (var t in tracks)
        {
            var tTitle = Tokens(t.Title ?? t.RawTitle);
            if (tTitle.Count == 0) continue;
            var tArtist = Tokens(t.Artist);

            FileProfile? best = null;
            double bestScore = 0;
            foreach (var f in profiles)
            {
                var tj = Jaccard(tTitle, f.Title);
                if (tj < 0.6) continue;
                var durClose = t.DurationSec is not null && f.Duration is not null
                               && Math.Abs(t.DurationSec.Value - f.Duration.Value) <= 7;
                var aj = Jaccard(tArtist, f.Artist);
                if (!durClose && aj < 0.34) continue; // need duration OR artist to confirm
                var score = tj + (durClose ? 0.3 : 0) + aj * 0.3;
                if (score > bestScore) { bestScore = score; best = f; }
            }
            if (best is null) continue;

            c.Execute("UPDATE tracks SET state='Manual', file_path=@p, updated_at=datetime('now') WHERE id=@id",
                new { p = best.Path, id = t.Id });
            c.Execute("UPDATE library_files SET matched_track_id=@tid WHERE id=@fid", new { tid = t.Id, fid = best.Id });
            matched++;
        }
        return matched;
    }

    private IEnumerable<string> Roots()
    {
        var roots = new List<string>();
        if (cfg["MusicLibDir"] is { Length: > 0 } lib) roots.Add(lib);
        using (var c = db.Open())
            roots.AddRange(c.Query<string>("SELECT DISTINCT dest_dir FROM sources WHERE dest_dir IS NOT NULL"));
        return roots.Where(Directory.Exists).Distinct();
    }

    // ---- fuzzy text helpers ----

    [GeneratedRegex(@"[\(\[\{].*?[\)\]\}]")] private static partial Regex BracketRe();
    [GeneratedRegex(@"(?i)\b(feat|ft|featuring|official|video|lyrics?|audio|remaster(ed)?|remix|hd|hq|topic)\b")] private static partial Regex NoiseRe();
    [GeneratedRegex(@"\b\d{4}\b")] private static partial Regex YearRe();

    private static HashSet<string> Tokens(string? s)
    {
        var set = new HashSet<string>();
        if (string.IsNullOrEmpty(s)) return set;
        var low = s.ToLowerInvariant();
        low = BracketRe().Replace(low, " ");
        var fi = low.IndexOf(" feat", StringComparison.Ordinal);
        if (fi >= 0) low = low[..fi];
        low = NoiseRe().Replace(low, " ");
        low = YearRe().Replace(low, " ");

        var sb = new StringBuilder();
        foreach (var ch in low) sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        foreach (var tok in sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (tok.Length > 1) set.Add(tok);
        return set;
    }

    private static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var inter = a.Count(b.Contains);
        var union = a.Count + b.Count - inter;
        return union == 0 ? 0 : (double)inter / union;
    }

    private async Task<(string?, string?, string?, int?)> ProbeAsync(string path, CancellationToken ct)
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
            foreach (var a in new[] { "-v", "error", "-show_entries", "format=duration:format_tags=artist,title,album", "-of", "json", path })
                psi.ArgumentList.Add(a);

            using var p = Process.Start(psi) ?? throw new InvalidOperationException("cannot start ffprobe");
            var outp = await p.StandardOutput.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);

            using var doc = JsonDocument.Parse(outp);
            var fmt = doc.RootElement.GetProperty("format");
            int? dur = fmt.TryGetProperty("duration", out var dEl)
                       && double.TryParse(dEl.GetString(), CultureInfo.InvariantCulture, out var dd)
                ? (int)Math.Round(dd) : null;
            string? artist = null, title = null, album = null;
            if (fmt.TryGetProperty("tags", out var tags))
            {
                artist = Tag(tags, "artist");
                title = Tag(tags, "title");
                album = Tag(tags, "album");
            }
            return (artist, title, album, dur);
        }
        catch (Exception ex)
        {
            log.LogWarning("ffprobe failed for {Path}: {Msg}", path, ex.Message);
            return (null, null, null, null);
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
