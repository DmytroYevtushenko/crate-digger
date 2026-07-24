using System.Diagnostics;

namespace Crate.Api;

public enum DlOutcome { Downloaded, AlreadyExists, NotFound, Error }
public record DlResult(DlOutcome Outcome, string? Path, string? Detail);

/// <summary>
/// Runs sldl as a subprocess for ONE track. Success is detected reliably and version-independently:
/// snapshot the audio files in dest BEFORE and AFTER the run — a new file means it downloaded.
/// stdout is also scanned for "already exists / skipped" and "not found" markers.
/// The password comes from configuration (env SLDL_PASS) and is NEVER logged.
/// </summary>
public sealed class SldlRunner(IConfiguration cfg, ILogger<SldlRunner> log)
{
    private static readonly string[] AudioExt = [".flac", ".mp3", ".m4a", ".ogg", ".opus", ".wav"];

    public bool IsConfigured => !string.IsNullOrWhiteSpace(User) && !string.IsNullOrWhiteSpace(Pass);
    private string Exe => cfg["SldlPath"] ?? "sldl";
    private string? User => cfg["SLDL_USER"];
    private string? Pass => cfg["SLDL_PASS"];

    public async Task<DlResult> DownloadAsync(string artist, string? title, Source src, CancellationToken ct = default)
    {
        var query = string.Join(" - ", new[] { artist, title }.Where(s => !string.IsNullOrWhiteSpace(s)))
                          .Trim();
        if (string.IsNullOrWhiteSpace(query))
            return new DlResult(DlOutcome.Error, null, "empty query");

        var dest = src.DestDir;
        Directory.CreateDirectory(dest);
        var before = Snapshot(dest);

        var args = new List<string> { query };
        if (!string.IsNullOrEmpty(User)) { args.Add("--user"); args.Add(User); }
        if (!string.IsNullOrEmpty(Pass)) { args.Add("--pass"); args.Add(Pass); }
        args.Add("--pref-format"); args.Add(string.IsNullOrWhiteSpace(src.Pref) ? "flac" : src.Pref!);
        if (!string.IsNullOrWhiteSpace(src.Cond)) { args.Add("--cond"); args.Add(src.Cond!); args.Add("--strict-conditions"); }
        args.Add("--length-tol"); args.Add("5");
        args.Add("--remove-ft");
        args.Add("-p"); args.Add(dest);
        if (cfg["SldlIndexPath"] is { Length: > 0 } idx) { args.Add("--index-path"); args.Add(idx); }
        if (cfg["MusicLibDir"] is { Length: > 0 } lib) { args.Add("--skip-music-dir"); args.Add(lib); }
        args.Add("--write-playlist"); args.Add("false");
        args.Add("--fast-search");

        int code;
        string stdout, stderr;
        try
        {
            (code, stdout, stderr) = await RunAsync(args, ct);
        }
        catch (Exception ex)
        {
            log.LogWarning("sldl failed to start for '{Query}': {Msg}", query, ex.Message);
            return new DlResult(DlOutcome.Error, null, ex.Message);
        }

        var after = Snapshot(dest);
        var newFiles = after.Where(kv => !before.ContainsKey(kv.Key)).Select(kv => kv.Key).ToList();
        if (newFiles.Count > 0)
            return new DlResult(DlOutcome.Downloaded, newFiles[0], null);

        var low = (stdout + "\n" + stderr).ToLowerInvariant();
        if (low.Contains("already exist") || low.Contains("skipped") || low.Contains("exists in"))
            return new DlResult(DlOutcome.AlreadyExists, null, "sldl reported already present");
        if (code != 0)
            return new DlResult(DlOutcome.Error, null, $"sldl exit {code}: {Tail(stderr)}");
        return new DlResult(DlOutcome.NotFound, null, "no matching file found");
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
        await p.WaitForExitAsync(ct);
        return (p.ExitCode, await outTask, await errTask);
    }

    private static string Tail(string s) =>
        string.Join(" ", s.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(2)).Trim();
}
