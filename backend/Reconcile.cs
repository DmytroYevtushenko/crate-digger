using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Dapper;

namespace Crate.Api;

/// <summary>
/// Scans the master library + inbox (recursively), reads tags/duration via ffprobe into
/// library_files (incremental by path+mtime+size), then matches files to tracks so that
/// manual (Picard) additions are recognized: a Pending/Failed track matched by a file
/// becomes Manual. Files matching nothing are just orphans (fine).
/// The DB is the source of truth — old sldl indexes are not consulted.
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

    // Match library files to Pending/Failed tracks by normalized artist+title (+ duration tolerance).
    private int Match()
    {
        using var c = db.Open();
        var files = c.Query<LibraryFile>("SELECT * FROM library_files WHERE matched_track_id IS NULL").ToList();
        var tracks = c.Query<Track>("SELECT * FROM tracks WHERE state IN ('Pending','Failed')").ToList();

        var byKey = new Dictionary<string, List<Track>>();
        foreach (var t in tracks)
        {
            var k = Key(t.Artist, t.Title);
            if (k == "|") continue;
            if (!byKey.TryGetValue(k, out var l)) byKey[k] = l = new List<Track>();
            l.Add(t);
        }

        var used = new HashSet<long>();
        var matched = 0;
        foreach (var f in files)
        {
            if (!byKey.TryGetValue(Key(f.Artist, f.Title), out var cand)) continue;
            var t = cand.FirstOrDefault(x => !used.Contains(x.Id) && DurOk(x.DurationSec, f.DurationSec));
            if (t is null) continue;
            used.Add(t.Id);
            c.Execute("UPDATE tracks SET state='Manual', file_path=@p, updated_at=datetime('now') WHERE id=@id",
                new { p = f.Path, id = t.Id });
            c.Execute("UPDATE library_files SET matched_track_id=@tid WHERE id=@fid", new { tid = t.Id, fid = f.Id });
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

    private static bool DurOk(int? a, int? b) => a is null || b is null || Math.Abs(a.Value - b.Value) <= 5;

    private static string Key(string? artist, string? title) => Norm(artist) + "|" + Norm(title);

    private static string Norm(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder();
        foreach (var ch in s.ToLowerInvariant())
            sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
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
