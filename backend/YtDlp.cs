using System.Diagnostics;
using System.Text.Json;

namespace Crate.Api;

public record PlaylistEntry(string Id, string? Title, string? Channel, int? Duration);
public record TrackMeta(string Id, string? Artist, string? Track, string? Album, int? Duration, string? RawTitle);
/// <summary>Best audio YouTube offers for a video — what you'd get if you downloaded it.</summary>
public record YtAudio(int? BitrateKbps, string? Codec, string? Ext, long? SizeBytes);

/// <summary>
/// Thin wrapper around the yt-dlp CLI. Two modes:
///  - ListPlaylistAsync: fast flat playlist listing (id/title/channel/duration) in one call;
///  - GetMetaAsync: full metadata for a single video (authoritative artist/track/album from YT Music).
/// </summary>
public sealed class YtDlp(string exe = "yt-dlp", string? cookiesPath = null)
{
    public async Task<List<PlaylistEntry>> ListPlaylistAsync(string url, CancellationToken ct = default)
    {
        var json = await RunAsync(["-J", "--flat-playlist", "--no-warnings", url], ct);
        using var doc = JsonDocument.Parse(json);
        var res = new List<PlaylistEntry>();
        if (doc.RootElement.TryGetProperty("entries", out var entries) &&
            entries.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in entries.EnumerateArray())
            {
                var id = Str(e, "id");
                if (id is null) continue;
                res.Add(new PlaylistEntry(id, Str(e, "title"),
                    Str(e, "channel") ?? Str(e, "uploader"), IntOrNull(e, "duration")));
            }
        }
        return res;
    }

    public async Task<TrackMeta?> GetMetaAsync(string videoId, CancellationToken ct = default)
    {
        var url = $"https://music.youtube.com/watch?v={videoId}";
        var json = await RunAsync(["-J", "--no-playlist", "--no-warnings", url], ct);
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        return new TrackMeta(videoId,
            Str(r, "artist") ?? Str(r, "creator"),
            Str(r, "track"),
            Str(r, "album"),
            IntOrNull(r, "duration"),
            Str(r, "title"));
    }

    /// <summary>
    /// Best audio-only format YouTube has for this video, so the UI can show what quality you'd get
    /// before committing to the download. Null if yt-dlp can't reach the video (gated/removed).
    /// </summary>
    public async Task<YtAudio?> GetBestAudioAsync(string videoId, CancellationToken ct = default)
    {
        var url = $"https://music.youtube.com/watch?v={videoId}";
        var json = await RunAsync(["-J", "--no-playlist", "--no-warnings", url], ct);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("formats", out var formats) || formats.ValueKind != JsonValueKind.Array)
            return null;

        YtAudio? best = null;
        double bestAbr = -1;
        foreach (var f in formats.EnumerateArray())
        {
            // audio-only formats have no video stream
            if (Str(f, "vcodec") is not (null or "none")) continue;
            var acodec = Str(f, "acodec");
            if (acodec is null or "none") continue;

            var abr = f.TryGetProperty("abr", out var abrEl) && abrEl.ValueKind == JsonValueKind.Number
                ? abrEl.GetDouble() : 0;
            if (abr <= bestAbr) continue;

            bestAbr = abr;
            long? size = f.TryGetProperty("filesize", out var fs) && fs.ValueKind == JsonValueKind.Number
                ? fs.GetInt64()
                : f.TryGetProperty("filesize_approx", out var fa) && fa.ValueKind == JsonValueKind.Number
                    ? fa.GetInt64() : null;
            best = new YtAudio(abr > 0 ? (int)Math.Round(abr) : null, acodec, Str(f, "ext"), size);
        }
        return best;
    }

    /// <summary>
    /// Downloads the best audio-only stream into destDir and returns the resulting file path.
    /// Kept as-is (no re-encode) — transcoding lossy audio would only lose more quality.
    /// </summary>
    public async Task<string?> DownloadAudioAsync(string videoId, string destDir, string fileStem, CancellationToken ct = default)
    {
        Directory.CreateDirectory(destDir);
        var safeStem = string.Join("_", fileStem.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        if (safeStem.Length == 0) safeStem = videoId;
        // Download into a private scratch folder, not the inbox itself — a parallel sldl run would
        // otherwise mistake this file for its own result (see Staging).
        var stage = Staging.Create(destDir);
        var template = Path.Combine(stage, safeStem + ".%(ext)s");

        try
        {
            // -x with no --audio-format remuxes the stream into a proper container (webm -> .opus) without
            // re-encoding, so nothing is lost and the file lands with an extension the library scan indexes.
            await RunAsync([
                "-f", "bestaudio", "-x", "--no-playlist", "--no-warnings",
                "--embed-metadata", "-o", template,
                $"https://music.youtube.com/watch?v={videoId}",
            ], ct);

            var got = Staging.Files(stage, AudioExt).OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();
            return got is null ? null : Staging.MoveOut(got, destDir);
        }
        finally
        {
            Staging.Discard(stage);
        }
    }

    private static readonly string[] AudioExt = [".flac", ".mp3", ".m4a", ".ogg", ".opus", ".webm", ".wav"];

    private async Task<string> RunAsync(string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (!string.IsNullOrEmpty(cookiesPath) && File.Exists(cookiesPath))
        {
            psi.ArgumentList.Add("--cookies");
            psi.ArgumentList.Add(cookiesPath);
        }
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException($"failed to start {exe}");
        var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        if (p.ExitCode != 0)
        {
            var stderr = await stderrTask;
            throw new InvalidOperationException($"yt-dlp exit {p.ExitCode}: {stderr.Trim()}");
        }
        return stdout;
    }

    private static string? Str(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? IntOrNull(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
}
