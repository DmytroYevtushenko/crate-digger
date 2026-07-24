using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Crate.Api;

public enum VerifyOutcome { Verified, Mismatch }
public record VerifyResult(VerifyOutcome Outcome, string Detail);

/// <summary>
/// Verifies a downloaded file against the target track. Fully local by default:
///   1) duration via ffprobe;
///   2) tags (artist/title) via ffprobe vs the target — catches "completely wrong song";
///   3) optional acoustic fingerprint via fpcalc + AcoustID (only if ACOUSTID_KEY is set).
/// A YouTube-reference fingerprint (API-free) would need yt-dlp cookies (YouTube now gates audio
/// behind a bot check), so it is not enabled here.
/// </summary>
public sealed class Verifier(IConfiguration cfg, ILogger<Verifier> log)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private string Ffprobe => cfg["FfprobePath"] ?? "ffprobe";
    private string Fpcalc => cfg["FpcalcPath"] ?? "fpcalc";
    private string? AcoustIdKey => cfg["ACOUSTID_KEY"];
    private int DurationTol => int.TryParse(cfg["VerifyDurationTolSec"], out var v) ? v : 7;

    public async Task<VerifyResult> VerifyAsync(string path, Track t, CancellationToken ct = default)
    {
        if (!File.Exists(path)) return new VerifyResult(VerifyOutcome.Mismatch, "file missing");

        var (dur, fArtist, fTitle) = await ProbeAsync(path, ct);

        // 1) duration
        var expected = t.ExpectedLenSec ?? t.DurationSec;
        if (dur is not null && expected is not null && Math.Abs(dur.Value - expected.Value) > DurationTol)
            return new VerifyResult(VerifyOutcome.Mismatch, $"duration {dur}s vs expected {expected}s");

        // 2) tags (local): the file's title/artist vs the target
        if (Conflict(fTitle, t.Title))
            return new VerifyResult(VerifyOutcome.Mismatch, $"title tag '{fTitle}' != target '{t.Title}'");
        if (Conflict(fArtist, t.Artist))
            return new VerifyResult(VerifyOutcome.Mismatch, $"artist tag '{fArtist}' != target '{t.Artist}'");

        // Confidence gate: skip the network-costly YouTube fingerprint when metadata is already strong
        // (file artist AND title match the target and duration is near-exact). Only "suspicious" downloads
        // — weak/missing tags, or duration a few seconds off — get fingerprinted, so YouTube is hit rarely.
        var strongTags = Matches(fArtist, t.Artist) && Matches(fTitle, t.Title);
        var tightDur = expected is not null && dur is not null && Math.Abs(dur.Value - expected.Value) <= 2;
        var confident = strongTags && tightDur;

        // 3) acoustic fingerprint vs the YouTube source (API-free) — only for the ambiguous ones.
        if (!confident && !string.Equals(cfg["VerifyYtFingerprint"], "false", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var yt = await YtFingerprintAsync(path, t, ct);
                if (yt is not null) return yt;
            }
            catch (Exception ex) { log.LogWarning("YT fingerprint failed: {Msg}", ex.Message); }
        }

        // 4) acoustic fingerprint via AcoustID (optional, if a key is set)
        if (!string.IsNullOrWhiteSpace(AcoustIdKey))
        {
            try
            {
                var fp = await FingerprintVerifyAsync(path, t, ct);
                if (fp is not null) return fp;
            }
            catch (Exception ex) { log.LogWarning("fingerprint verify failed: {Msg}", ex.Message); }
        }

        return new VerifyResult(VerifyOutcome.Verified, "duration + tags ok");
    }

    // Two strings conflict if both are non-empty and neither contains the other (normalized).
    private static bool Conflict(string? a, string? b)
    {
        var na = Norm(a);
        var nb = Norm(b);
        if (na.Length == 0 || nb.Length == 0) return false; // can't judge
        return !(na == nb || na.Contains(nb) || nb.Contains(na));
    }

    // Positive match: both present and one contains the other (normalized).
    private static bool Matches(string? a, string? b)
    {
        var na = Norm(a);
        var nb = Norm(b);
        return na.Length > 0 && nb.Length > 0 && (na == nb || na.Contains(nb) || nb.Contains(na));
    }

    private static string Norm(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder();
        foreach (var ch in s.ToLowerInvariant())
            sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private async Task<(int?, string?, string?)> ProbeAsync(string path, CancellationToken ct)
    {
        try
        {
            var (code, outp, _) = await Run(Ffprobe,
                ["-v", "error", "-show_entries", "format=duration:format_tags=artist,title", "-of", "json", path], ct);
            if (code != 0) return (null, null, null);
            using var doc = JsonDocument.Parse(outp);
            var fmt = doc.RootElement.GetProperty("format");
            int? dur = fmt.TryGetProperty("duration", out var dEl)
                       && double.TryParse(dEl.GetString(), CultureInfo.InvariantCulture, out var dd)
                ? (int)Math.Round(dd) : null;
            string? artist = null, title = null;
            if (fmt.TryGetProperty("tags", out var tags))
            {
                foreach (var p in tags.EnumerateObject())
                {
                    if (string.Equals(p.Name, "artist", StringComparison.OrdinalIgnoreCase)) artist = p.Value.GetString();
                    else if (string.Equals(p.Name, "title", StringComparison.OrdinalIgnoreCase)) title = p.Value.GetString();
                }
            }
            return (dur, artist, title);
        }
        catch (Exception ex) { log.LogWarning("ffprobe failed: {Msg}", ex.Message); return (null, null, null); }
    }

    // fpcalc -json -> {duration, fingerprint}; AcoustID lookup -> compare recording to the target.
    private async Task<VerifyResult?> FingerprintVerifyAsync(string path, Track t, CancellationToken ct)
    {
        var (code, outp, _) = await Run(Fpcalc, ["-json", path], ct);
        if (code != 0) return null;

        using var fdoc = JsonDocument.Parse(outp);
        var fr = fdoc.RootElement;
        if (!fr.TryGetProperty("fingerprint", out var fpEl) || !fr.TryGetProperty("duration", out var durEl))
            return null;
        var fingerprint = fpEl.GetString();
        if (fingerprint is null) return null;
        var duration = (int)Math.Round(durEl.GetDouble());

        var url = $"https://api.acoustid.org/v2/lookup?client={AcoustIdKey}&duration={duration}"
                + $"&fingerprint={Uri.EscapeDataString(fingerprint)}&meta=recordings";
        var json = await Http.GetStringAsync(url, ct);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
            return null; // AcoustID doesn't know it — inconclusive, let duration/tags decide

        var targetTitle = Norm(t.Title);
        var targetMbid = t.MbRecordingId;
        foreach (var res in results.EnumerateArray())
        {
            if (!res.TryGetProperty("recordings", out var recs)) continue;
            foreach (var rec in recs.EnumerateArray())
            {
                var recId = rec.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                var recTitle = Norm(rec.TryGetProperty("title", out var tEl) ? tEl.GetString() : null);
                if (targetMbid is not null && recId == targetMbid)
                    return new VerifyResult(VerifyOutcome.Verified, "fingerprint matched MBID");
                if (targetTitle.Length > 0 && recTitle.Length > 0 &&
                    (recTitle.Contains(targetTitle) || targetTitle.Contains(recTitle)))
                    return new VerifyResult(VerifyOutcome.Verified, "fingerprint matched title");
            }
        }
        return new VerifyResult(VerifyOutcome.Mismatch, "fingerprint matched a different recording");
    }

    // Compare the downloaded file against a short sample of the YouTube source recording.
    // Same recording -> low bit-error-rate (BER); a different recording (live/cover) -> high BER.
    private async Task<VerifyResult?> YtFingerprintAsync(string filePath, Track t, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(t.ExternalId)) return null;
        var ytdlp = cfg["YtDlpPath"] ?? "yt-dlp";
        var cookies = cfg["CookiesPath"];
        var seconds = int.TryParse(cfg["VerifySampleSec"], out var ss) ? ss : 90;

        var tmpDir = Path.Combine(Path.GetTempPath(), "crate-ytref-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        try
        {
            var args = new List<string>
            {
                "-x", "--audio-format", "wav", "--no-playlist", "--no-warnings",
                "--download-sections", $"*0-{seconds}", "-o", Path.Combine(tmpDir, "ref.%(ext)s"),
            };
            if (!string.IsNullOrEmpty(cookies) && File.Exists(cookies)) { args.Add("--cookies"); args.Add(cookies); }
            args.Add($"https://music.youtube.com/watch?v={t.ExternalId}");

            var (code, _, err) = await Run(ytdlp, args.ToArray(), ct);
            var refFile = Directory.EnumerateFiles(tmpDir).FirstOrDefault();
            if (code != 0 || refFile is null)
            {
                log.LogWarning("YT reference download failed for {Id}: {Err}", t.ExternalId, err.Trim());
                return null; // inconclusive — let tags/duration stand
            }

            var fpA = await FpcalcRawAsync(filePath, seconds, ct);
            var fpB = await FpcalcRawAsync(refFile, seconds, ct);
            if (fpA is null || fpB is null) return null;

            var ber = BestBer(fpA, fpB);
            var threshold = double.TryParse(cfg["VerifyBerThreshold"], NumberStyles.Float, CultureInfo.InvariantCulture, out var th) ? th : 0.35;
            log.LogInformation("YT fingerprint track {Id}: BER {Ber:F3} (threshold {Th:F2})", t.Id, ber, threshold);
            return ber <= threshold
                ? new VerifyResult(VerifyOutcome.Verified, $"fingerprint matches YouTube (BER {ber:F2})")
                : new VerifyResult(VerifyOutcome.Mismatch, $"different recording from YouTube (BER {ber:F2})");
        }
        finally
        {
            try { Directory.Delete(tmpDir, true); } catch { /* ignore */ }
        }
    }

    private async Task<uint[]?> FpcalcRawAsync(string path, int seconds, CancellationToken ct)
    {
        var (code, outp, _) = await Run(Fpcalc, ["-raw", "-json", "-length", seconds.ToString(), path], ct);
        if (code != 0) return null;
        using var doc = JsonDocument.Parse(outp);
        if (!doc.RootElement.TryGetProperty("fingerprint", out var fp) || fp.ValueKind != JsonValueKind.Array) return null;
        var arr = new uint[fp.GetArrayLength()];
        var i = 0;
        foreach (var el in fp.EnumerateArray()) arr[i++] = (uint)el.GetInt64();
        return arr;
    }

    // Best bit-error-rate over a small alignment offset search.
    private static double BestBer(uint[] a, uint[] b)
    {
        var best = 1.0;
        for (var off = -20; off <= 20; off++)
        {
            int ai = Math.Max(0, off), bi = Math.Max(0, -off);
            var n = Math.Min(a.Length - ai, b.Length - bi);
            if (n < 50) continue;
            long bits = 0;
            for (var i = 0; i < n; i++)
                bits += System.Numerics.BitOperations.PopCount(a[ai + i] ^ b[bi + i]);
            var ber = bits / (double)(n * 32);
            if (ber < best) best = ber;
        }
        return best;
    }

    private static async Task<(int, string, string)> Run(string exe, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"cannot start {exe}");
        var o = p.StandardOutput.ReadToEndAsync(ct);
        var e = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return (p.ExitCode, await o, await e);
    }
}
