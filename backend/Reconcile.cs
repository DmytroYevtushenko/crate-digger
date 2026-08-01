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
        var files = c.Query<LibraryFile>("SELECT * FROM library_files").ToList();
        var tracks = c.Query<Track>("SELECT * FROM tracks WHERE state IN ('Pending','Failed')").ToList();

        var profiles = files
            .Select(f => new FileProfile(f.Id, f.Path, Tokens(f.Title), Tokens(f.Artist), f.DurationSec))
            .Where(p => p.Title.Count > 0)
            .ToList();

        // Paths a manual-review decision already rejected for a given track — never re-offer them.
        var ignored = c.Query("SELECT track_id, path FROM track_ignored_files")
            .Select(r => ((long)r.track_id, (string)r.path))
            .ToHashSet();

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
                if (ignored.Contains((t.Id, f.Path))) continue;
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

    // Fuzzy library candidates for a track (below the auto-match threshold) — for manual pick in the UI.
    public List<object> Candidates(long trackId, int top = 8)
    {
        using var c = db.Open();
        var t = c.QuerySingleOrDefault<Track>("SELECT * FROM tracks WHERE id=@id", new { id = trackId });
        if (t is null) return new List<object>();
        var tTitle = Tokens(t.Title ?? t.RawTitle);
        var tArtist = Tokens(t.Artist);
        var files = c.Query<LibraryFile>("SELECT * FROM library_files").ToList();
        return files
            .Select(f =>
            {
                var tj = Jaccard(tTitle, Tokens(f.Title));
                var aj = Jaccard(tArtist, Tokens(f.Artist));
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
        var tTitle = Tokens(t.Title ?? t.RawTitle);
        if (tTitle.Count == 0) return false;
        var tArtist = Tokens(t.Artist);

        var ignored = c.Query<string>("SELECT path FROM track_ignored_files WHERE track_id=@id", new { id = trackId }).ToHashSet();
        var files = c.Query<LibraryFile>("SELECT * FROM library_files").ToList();
        long bestId = 0; string? bestPath = null; double bestScore = 0;
        foreach (var f in files)
        {
            if (ignored.Contains(f.Path)) continue;
            var ft = Tokens(f.Title);
            if (ft.Count == 0) continue;
            var tj = Jaccard(tTitle, ft);
            if (tj < 0.6) continue;
            var durClose = t.DurationSec is not null && f.DurationSec is not null
                           && Math.Abs(t.DurationSec.Value - f.DurationSec.Value) <= 7;
            var aj = Jaccard(tArtist, Tokens(f.Artist));
            if (!durClose && aj < 0.34) continue;
            var score = tj + (durClose ? 0.3 : 0) + aj * 0.3;
            if (score > bestScore) { bestScore = score; bestId = f.Id; bestPath = f.Path; }
        }
        if (bestPath is null) return false;

        c.Execute("UPDATE tracks SET state='Manual', file_path=@p, updated_at=datetime('now') WHERE id=@id", new { p = bestPath, id = trackId });
        c.Execute("UPDATE library_files SET matched_track_id=@id WHERE id=@fid", new { id = trackId, fid = bestId });
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

    // ---- fuzzy text helpers ----

    [GeneratedRegex(@"[\(\[\{].*?[\)\]\}]")] private static partial Regex BracketRe();
    [GeneratedRegex(@"(?i)-\s*topic\b")] private static partial Regex TopicRe();
    [GeneratedRegex(@"(?i)\b(feat|ft|featuring|official|video|lyrics?|audio|remaster(ed)?|remix|hd|hq)\b")] private static partial Regex NoiseRe();
    [GeneratedRegex(@"\b\d{4}\b")] private static partial Regex YearRe();

    // Cyrillic (RU/UK) -> Latin, so "Александр Маршал" and "Aleksandr Marshal" reduce to the same tokens.
    private static readonly Dictionary<char, string> Cyr = new()
    {
        ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['ґ'] = "g", ['д'] = "d", ['е'] = "e", ['ё'] = "yo",
        ['є'] = "ye", ['ж'] = "zh", ['з'] = "z", ['и'] = "i", ['і'] = "i", ['ї'] = "yi", ['й'] = "y", ['к'] = "k",
        ['л'] = "l", ['м'] = "m", ['н'] = "n", ['о'] = "o", ['п'] = "p", ['р'] = "r", ['с'] = "s", ['т'] = "t",
        ['у'] = "u", ['ф'] = "f", ['х'] = "kh", ['ц'] = "ts", ['ч'] = "ch", ['ш'] = "sh", ['щ'] = "shch",
        ['ъ'] = "", ['ы'] = "y", ['ь'] = "", ['э'] = "e", ['ю'] = "yu", ['я'] = "ya",
    };

    private static string Latinize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s) sb.Append(Cyr.TryGetValue(ch, out var r) ? r : ch.ToString());
        return sb.ToString();
    }

    private static HashSet<string> Tokens(string? s)
    {
        var set = new HashSet<string>();
        if (string.IsNullOrEmpty(s)) return set;
        var low = Latinize(s.ToLowerInvariant());
        low = TopicRe().Replace(low, " ");   // strip the YouTube "- Topic" channel suffix (a real word "topic" is kept)
        low = BracketRe().Replace(low, " ");
        var fi = low.IndexOf(" feat", StringComparison.Ordinal);
        if (fi >= 0) low = low[..fi];
        low = NoiseRe().Replace(low, " ");
        low = YearRe().Replace(low, " ");

        var sb = new StringBuilder();
        foreach (var ch in low) sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        var compact = new StringBuilder();
        foreach (var tok in sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (tok.Length > 1) set.Add(tok);
            compact.Append(tok);
        }
        // Acronyms / very short titles (e.g. "S.O.S.") yield only 1-char tokens -> fall back to a compact form.
        if (set.Count == 0 && compact.Length > 1) set.Add(compact.ToString());
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
