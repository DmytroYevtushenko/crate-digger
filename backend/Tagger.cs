using System.Diagnostics;

namespace Crate.Api;

/// <summary>
/// Writes clean tags (artist/title/album) into a downloaded file via ffmpeg — a light,
/// automatic "Picard-lite" so files land with correct metadata. ffmpeg can't edit in place,
/// so it rewrites to a temp file (stream-copied, no re-encode) and replaces the original.
/// Enabled unless AutoTag=false.
/// </summary>
public sealed class Tagger(IConfiguration cfg, ILogger<Tagger> log)
{
    private bool Enabled => !string.Equals(cfg["AutoTag"], "false", StringComparison.OrdinalIgnoreCase);
    private string Ffmpeg => cfg["FfmpegPath"] ?? "ffmpeg";

    public async Task TagAsync(string path, Track t, CancellationToken ct = default)
    {
        if (!Enabled || !File.Exists(path)) return;
        if (string.IsNullOrWhiteSpace(t.Artist) && string.IsNullOrWhiteSpace(t.Title)) return;

        var dir = Path.GetDirectoryName(path)!;
        var tmp = Path.Combine(dir, Path.GetFileNameWithoutExtension(path) + ".crate-tmp" + Path.GetExtension(path));

        var args = new List<string> { "-y", "-i", path, "-map", "0", "-c", "copy", "-map_metadata", "-1" };
        void Meta(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) { args.Add("-metadata"); args.Add($"{key}={value}"); }
        }
        Meta("ARTIST", t.Artist);
        Meta("TITLE", t.Title);
        Meta("ALBUM", t.Album);
        Meta("ALBUMARTIST", t.Artist);
        args.Add(tmp);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Ffmpeg,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi) ?? throw new InvalidOperationException("cannot start ffmpeg");
            var outTask = p.StandardOutput.ReadToEndAsync(ct);
            var errTask = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            var err = await errTask;
            await outTask;

            if (p.ExitCode == 0 && File.Exists(tmp))
            {
                File.Move(tmp, path, overwrite: true);
                log.LogInformation("Tagged {Path}", path);
            }
            else
            {
                if (File.Exists(tmp)) File.Delete(tmp);
                log.LogWarning("tagging failed for {Path}: {Err}", path, err.Trim());
            }
        }
        catch (Exception ex)
        {
            log.LogWarning("tagging error for {Path}: {Msg}", path, ex.Message);
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore */ }
        }
    }
}
