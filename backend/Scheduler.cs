using System.Globalization;
using Cronos;
using Dapper;

namespace Crate.Api;

/// <summary>
/// Simple in-process scheduler: every SchedulerIntervalSec it checks each enabled source that has a
/// cron expression and, when due, runs sync + queues downloads of the missing tracks.
/// No external scheduler; cron parsing via Cronos.
/// </summary>
public sealed class SchedulerService(Db db, SyncService sync, Downloader downloader, IConfiguration cfg, ILogger<SchedulerService> log)
{
    private int IntervalSec => int.TryParse(cfg["SchedulerIntervalSec"], out var v) && v > 0 ? v : 60;
    private int DownloadCap => int.TryParse(cfg["SchedulerDownloadCap"], out var v) && v > 0 ? v : 100;

    public void Start(CancellationToken ct) => _ = Task.Run(() => LoopAsync(ct), ct);

    private async Task LoopAsync(CancellationToken ct)
    {
        log.LogInformation("Scheduler started (interval {Sec}s)", IntervalSec);
        while (!ct.IsCancellationRequested)
        {
            try { await TickAsync(ct); }
            catch (Exception ex) { log.LogError(ex, "scheduler tick failed"); }
            try { await Task.Delay(TimeSpan.FromSeconds(IntervalSec), ct); }
            catch (TaskCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        List<Source> sources;
        using (var c = db.Open())
            sources = c.Query<Source>(
                "SELECT * FROM sources WHERE enabled=1 AND schedule_cron IS NOT NULL AND schedule_cron<>''").ToList();

        var now = DateTime.UtcNow;
        foreach (var s in sources)
        {
            if (ct.IsCancellationRequested) break;

            CronExpression cron;
            try { cron = CronExpression.Parse(s.ScheduleCron!); }
            catch { log.LogWarning("bad cron '{Cron}' for source {Id}", s.ScheduleCron, s.Id); continue; }

            var last = ParseUtc(s.LastRunAt) ?? now.AddMinutes(-1);
            var next = cron.GetNextOccurrence(last, inclusive: false);
            if (next is null || now < next.Value) continue;

            log.LogInformation("Scheduler: source {Id} '{Name}' due (cron {Cron})", s.Id, s.Name, s.ScheduleCron);
            try
            {
                await sync.RunAsync(s.Id, ct);
                downloader.Queue(s.Id, DownloadCap, out _);
            }
            catch (Exception ex) { log.LogWarning("scheduled run failed for source {Id}: {Msg}", s.Id, ex.Message); }
            finally
            {
                // Always advance last_run_at so a failing source doesn't re-fire every tick.
                using var c = db.Open();
                c.Execute("UPDATE sources SET last_run_at=datetime('now') WHERE id=@id", new { id = s.Id });
            }
        }
    }

    private static DateTime? ParseUtc(string? s) =>
        DateTime.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) ? d : null;
}
