using System.Diagnostics;
using System.Text.Json;

namespace Crate.Api;

public record PlaylistEntry(string Id, string? Title, string? Channel, int? Duration);
public record TrackMeta(string Id, string? Artist, string? Track, string? Album, int? Duration, string? RawTitle);

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
