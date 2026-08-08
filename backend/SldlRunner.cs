using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Crate.Api;

public enum DlOutcome { Downloaded, AlreadyExists, NotFound, Error }
public record DlResult(DlOutcome Outcome, string? Path, string? Detail);

/// <summary>
/// Runs sldl as a subprocess for ONE track. Instead of a loose search string (which grabs the wrong
/// song), it feeds sldl a one-row CSV with Artist/Title/Length so sldl can match by artist + title +
/// length (--length-tol) and reject wrong recordings (--strict-artist).
/// Success is detected version-independently: a new audio file appearing in dest.
/// The password comes from config (env SLDL_PASS) and is NEVER logged.
/// </summary>
public sealed class SldlRunner(IConfiguration cfg, ILogger<SldlRunner> log)
{
    private static readonly string[] AudioExt = [".flac", ".mp3", ".m4a", ".ogg", ".opus", ".wav"];
    private static readonly Regex TopicRe = new(@"(?i)\s*-\s*topic\b", RegexOptions.Compiled);
    private static readonly Regex BracketRe = new(@"\s*[\(\[\{][^)\]\}]*[\)\]\}]", RegexOptions.Compiled);

    public bool IsConfigured => !string.IsNullOrWhiteSpace(User) && !string.IsNullOrWhiteSpace(Pass);
    private string Exe => cfg["SldlPath"] ?? "sldl";
    private string? User => cfg["SLDL_USER"];
    private string? Pass => cfg["SLDL_PASS"];
    private int ListenPort => int.TryParse(cfg["SldlListenPort"], out var p) && p is > 1024 and < 32768 ? p : 21098;

    // sldl always binds a fixed Soulseek listen port under one shared account, so two instances
    // running at once (e.g. a manual retry overlapping the batch queue) fight over the port and
    // one fails or hangs to timeout. Serialize every invocation process-wide.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task<DlResult> DownloadAsync(string artist, string? title, int? lengthSec, Source src, CancellationToken ct = default)
    {
        var qArtist = CleanQuery(artist);
        var qTitle = CleanQuery(title);
        if (qArtist.Length == 0 && qTitle.Length == 0)
            return new DlResult(DlOutcome.Error, null, "empty query");
        var label = string.Join(" - ", new[] { qArtist, qTitle }.Where(s => s.Length > 0));

        var dest = src.DestDir;
        Directory.CreateDirectory(dest);
        var before = Snapshot(dest);
        var beforeAll = AllFiles(dest);

        // One-row CSV so sldl matches structurally (Length must be in seconds).
        var csvPath = Path.Combine(Path.GetTempPath(), "crate-" + Guid.NewGuid().ToString("N") + ".csv");
        var hasLen = lengthSec is > 0;
        var header = hasLen ? "Artist,Title,Length" : "Artist,Title";
        var row = hasLen ? $"{Csv(qArtist)},{Csv(qTitle)},{lengthSec}" : $"{Csv(qArtist)},{Csv(qTitle)}";
        await File.WriteAllTextAsync(csvPath, header + "\n" + row + "\n", ct);

        var args = new List<string> { csvPath, "--input-type", "csv" };
        if (!string.IsNullOrEmpty(User)) { args.Add("--user"); args.Add(User); }
        if (!string.IsNullOrEmpty(Pass)) { args.Add("--pass"); args.Add(Pass); }
        args.Add("--pref-format"); args.Add(string.IsNullOrWhiteSpace(src.Pref) ? "flac" : src.Pref!);
        if (!string.IsNullOrWhiteSpace(src.Cond)) { args.Add("--cond"); args.Add(src.Cond!); args.Add("--strict-conditions"); }
        // Land the file flat in dest with a clean name — otherwise sldl creates a subfolder
        // named after the input CSV (crate-<guid>/) for every single track.
        args.Add("--name-format"); args.Add("{sartist( - )stitle|filename}");
        args.Add("--length-tol"); args.Add("5");
        args.Add("--strict-artist");
        args.Add("--remove-ft");
        args.Add("-p"); args.Add(dest);
        // No --index-path either: sldl's index is a second, independent record of "already handled"
        // that never expires, so a track it once attempted is skipped forever with "1 tracks already
        // exist" — it won't even search. Crate's own per-track state decides what to (re)try, and only
        // tracks it considers missing reach this method.
        // No --skip-music-dir: sldl decides "already owned" by title alone, ignoring the artist, so it
        // reported e.g. "Скриптонит - Стиль" as owned because the library holds "Wellboy - Стиль" — the
        // track then never downloaded. Ownership is Crate's call (Reconcile confirms artist + duration),
        // and only tracks Crate considers missing are queued here in the first place.
        args.Add("--write-playlist"); args.Add("false");
        args.Add("--fast-search");
        // sldl's default incoming port (49998) sits inside the kernel's ephemeral range
        // (32768-60999). We share a network namespace with gluetun's other clients (qbittorrent &
        // friends), so one of their outgoing connections can hold 49998 as its local port and sldl
        // then dies at login with "Failed to start listening on 0.0.0.0:49998". Bind below the
        // ephemeral range instead, where nothing else can squat.
        args.Add("--listen-port"); args.Add(ListenPort.ToString());

        int code;
        string stdout, stderr;
        var timeoutSec = int.TryParse(cfg["SldlTimeoutSec"], out var ts) && ts > 0 ? ts : 300;
        await Gate.WaitAsync(ct);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
            (code, stdout, stderr) = await RunAsync(args, linked.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            TryDelete(csvPath);
            throw; // user pressed Stop
        }
        catch (OperationCanceledException)
        {
            TryDelete(csvPath);
            CleanupNew(dest, beforeAll);
            log.LogWarning("sldl timed out after {Sec}s for '{Label}'", timeoutSec, label);
            return new DlResult(DlOutcome.Error, null, $"timed out after {timeoutSec}s");
        }
        catch (Exception ex)
        {
            TryDelete(csvPath);
            log.LogWarning("sldl failed to start for '{Label}': {Msg}", label, ex.Message);
            return new DlResult(DlOutcome.Error, null, ex.Message);
        }
        finally
        {
            Gate.Release();
        }
        TryDelete(csvPath);

        var after = Snapshot(dest);
        var newFiles = after.Where(kv => !before.ContainsKey(kv.Key)).Select(kv => kv.Key).ToList();
        if (newFiles.Count > 0)
        {
            // sldl may have pulled a whole album folder — keep only the best-matching track,
            // delete every other new file (extra mixes, cover art, @eaDir) and empty dirs.
            var kept = PickBest(newFiles, label);
            CleanupExtras(dest, beforeAll, kept);
            return new DlResult(DlOutcome.Downloaded, kept, null);
        }

        // Nothing usable downloaded — remove any partial/leftover files this attempt created.
        CleanupNew(dest, beforeAll);

        var low = (stdout + "\n" + stderr).ToLowerInvariant();
        // Match only a genuine "already exists" claim. A bare "skipped" also covers tracks sldl skips
        // because its index says they failed last time — reading that as "you already own it" left
        // tracks marked in-library with no file, never downloaded and never checked.
        if (low.Contains("already exist") || low.Contains("exists in"))
            return new DlResult(DlOutcome.AlreadyExists, null, "sldl reported already present");
        if (code != 0)
        {
            var tail = Tail(stdout + "\n" + stderr);
            return new DlResult(DlOutcome.Error, null,
                tail.Length > 0 ? $"sldl exit {code}: {tail}" : $"sldl exit {code} — no match found at this quality");
        }
        return new DlResult(DlOutcome.NotFound, null, "no matching file found");
    }

    // Strip YouTube channel junk ("- Topic") and bracketed noise so the match terms are clean.
    private static string CleanQuery(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = TopicRe.Replace(s, " ");
        s = BracketRe.Replace(s, " ");
        return s.Trim().Trim('-').Trim();
    }

    private static string Csv(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
    }

    private static HashSet<string> AllFiles(string dir)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (Directory.Exists(dir))
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                set.Add(f);
        return set;
    }

    // Pick the new audio file whose filename best matches the target label (most shared words).
    private static string PickBest(List<string> files, string label)
    {
        var target = Toks(label).ToHashSet();
        var best = files[0];
        var bestScore = -1;
        foreach (var f in files)
        {
            // Title-word overlap dominates; prefer .flac on ties.
            var score = Toks(Path.GetFileNameWithoutExtension(f)).Count(target.Contains) * 10
                        + (Path.GetExtension(f).Equals(".flac", StringComparison.OrdinalIgnoreCase) ? 1 : 0);
            if (score > bestScore) { bestScore = score; best = f; }
        }
        return best;
    }

    private static IEnumerable<string> Toks(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s.ToLowerInvariant()) sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        return sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => t.Length > 1);
    }

    // Delete every new file except the one we keep, then remove any emptied directories.
    private void CleanupExtras(string dir, HashSet<string> before, string kept)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToList())
        {
            if (before.Contains(f) || f == kept) continue;
            try { File.Delete(f); } catch { /* ignore */ }
        }
        foreach (var d in Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories)
                     .OrderByDescending(x => x.Length).ToList())
        {
            try { if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); } catch { /* ignore */ }
        }
    }

    private void CleanupNew(string dir, HashSet<string> before)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            if (before.Contains(f)) continue;
            try { File.Delete(f); log.LogInformation("cleaned leftover {File}", f); }
            catch (Exception ex) { log.LogWarning("cleanup failed for {File}: {Msg}", f, ex.Message); }
        }
    }

    private Dictionary<string, long> Snapshot(string dir)
    {
        var map = new Dictionary<string, long>();
        if (!Directory.Exists(dir)) return map;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            if (AudioExt.Contains(Path.GetExtension(f).ToLowerInvariant()))
                map[f] = new FileInfo(f).Length;
        return map;
    }

    private async Task<(int, string, string)> RunAsync(List<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"cannot start {Exe}");
        var outTask = p.StandardOutput.ReadToEndAsync(ct);
        var errTask = p.StandardError.ReadToEndAsync(ct);
        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { try { if (!p.HasExited) p.Kill(true); } catch { /* ignore */ } throw; }
        return (p.ExitCode, await outTask, await errTask);
    }

    private static string Tail(string s) =>
        string.Join(" ", s.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(2)).Trim();
}
