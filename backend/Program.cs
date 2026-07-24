using Dapper;
using Crate.Api;

var builder = WebApplication.CreateBuilder(args);

// Dev frontend (vite) runs on a different origin — allow only it.
builder.Services.AddCors(o => o.AddPolicy("dev", p => p
    .WithOrigins("http://localhost:5173", "http://127.0.0.1:5173")
    .AllowAnyHeader()
    .AllowAnyMethod()));

var app = builder.Build();

// SQLite path: env/config "DbPath" (/data/crate.db in the container), else ./data next to the app.
var dbPath = app.Configuration["DbPath"]
             ?? Path.Combine(app.Environment.ContentRootPath, "data", "crate.db");
var db = new Db(dbPath);
db.Init();

var dataDir = Path.GetDirectoryName(Path.GetFullPath(dbPath))!;
var cookiesPath = app.Configuration["CookiesPath"] ?? Path.Combine(dataDir, "cookies.txt");

var lf = app.Services.GetRequiredService<ILoggerFactory>();
var ytdlp = new YtDlp(app.Configuration["YtDlpPath"] ?? "yt-dlp", cookiesPath);
var sync = new SyncService(db, ytdlp, lf.CreateLogger<SyncService>());
var sldl = new SldlRunner(app.Configuration, lf.CreateLogger<SldlRunner>());
var verifier = new Verifier(app.Configuration, lf.CreateLogger<Verifier>());
var tagger = new Tagger(app.Configuration, lf.CreateLogger<Tagger>());
var downloader = new Downloader(db, ytdlp, sldl, verifier, tagger, lf.CreateLogger<Downloader>());
var reconcile = new ReconcileService(db, app.Configuration, lf.CreateLogger<ReconcileService>());
var scheduler = new SchedulerService(db, sync, downloader, app.Configuration, lf.CreateLogger<SchedulerService>());
scheduler.Start(app.Lifetime.ApplicationStopping);

app.UseCors("dev");

// Serve the built SPA (wwwroot). In dev the folder is empty — harmless, vite serves the frontend.
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    db = File.Exists(dbPath),
    time = DateTimeOffset.UtcNow
}));

app.MapGet("/api/stats", () =>
{
    using var c = db.Open();
    var sources = c.ExecuteScalar<long>("SELECT COUNT(*) FROM sources");
    var tracks = c.ExecuteScalar<long>("SELECT COUNT(*) FROM tracks");
    var byState = c.Query("SELECT state, COUNT(*) AS count FROM tracks GROUP BY state")
        .ToDictionary(r => (string)r.state, r => (long)r.count);
    return Results.Ok(new { sources, tracks, byState });
});

app.MapGet("/api/sources", () =>
{
    using var c = db.Open();
    return Results.Ok(c.Query<Source>("SELECT * FROM sources ORDER BY id"));
});

app.MapPost("/api/sources", (SourceInput input) =>
{
    using var c = db.Open();
    var id = c.ExecuteScalar<long>(@"
INSERT INTO sources (kind, url, name, dest_dir, cond, pref, min_format, upgrade_lower_quality, schedule_cron, profile, enabled)
VALUES (@Kind, @Url, @Name, @DestDir, @Cond, @Pref, @MinFormat, @UpgradeLowerQuality, @ScheduleCron, @Profile, @Enabled);
SELECT last_insert_rowid();", input);
    return Results.Created($"/api/sources/{id}", new { id });
});

app.MapPut("/api/sources/{id:long}", (long id, SourceInput input) =>
{
    using var c = db.Open();
    var n = c.Execute(@"
UPDATE sources SET
    name=@Name, url=@Url, dest_dir=@DestDir, cond=@Cond, pref=@Pref,
    min_format=@MinFormat, upgrade_lower_quality=@UpgradeLowerQuality,
    schedule_cron=@ScheduleCron, profile=@Profile, enabled=@Enabled
WHERE id=@id",
        new { input.Name, input.Url, input.DestDir, input.Cond, input.Pref, input.MinFormat,
              input.UpgradeLowerQuality, input.ScheduleCron, input.Profile, input.Enabled, id });
    return n > 0 ? Results.Ok(new { id }) : Results.NotFound(new { error = "source not found" });
});

app.MapDelete("/api/sources/{id:long}", (long id) =>
{
    using var c = db.Open();
    var n = c.Execute("DELETE FROM sources WHERE id=@id", new { id });
    return n > 0 ? Results.Ok(new { id }) : Results.NotFound(new { error = "source not found" });
});

app.MapPost("/api/sources/{id:long}/sync", async (long id) =>
{
    var res = await sync.RunAsync(id);
    return res.Ok ? Results.Ok(res) : Results.NotFound(new { error = res.Error });
});

// Enrich with authoritative metadata — on demand and capped (not the whole library).
// In M3 it is applied per-track for the missing tracks right before download.
app.MapPost("/api/sources/{id:long}/enrich", async (long id, int? limit) =>
{
    var n = await sync.EnrichAsync(id, Math.Clamp(limit ?? 25, 1, 500));
    return Results.Ok(new { enriched = n });
});

app.MapPost("/api/sources/{id:long}/download", (long id, int? limit) =>
{
    var n = downloader.Queue(id, Math.Clamp(limit ?? 10, 1, 5000), out var error);
    return error is null
        ? Results.Ok(new { queued = n, sldlConfigured = sldl.IsConfigured })
        : Results.NotFound(new { error });
});

app.MapPost("/api/reconcile", () => Results.Ok(new { started = reconcile.Start(), running = reconcile.Running }));

app.MapGet("/api/reconcile/status", () =>
{
    using var c = db.Open();
    var files = c.ExecuteScalar<long>("SELECT COUNT(*) FROM library_files");
    var matched = c.ExecuteScalar<long>("SELECT COUNT(*) FROM library_files WHERE matched_track_id IS NOT NULL");
    return Results.Ok(new { running = reconcile.Running, libraryFiles = files, matched, last = reconcile.LastResult });
});

// Review-queue actions: confirm a Mismatch as OK, reject (blacklist), or retry a Failed/Mismatch.
app.MapPost("/api/tracks/{id:long}/{action}", (long id, string action) =>
{
    var newState = action switch
    {
        "confirm" => "Verified",
        "reject" => "Blacklisted",
        "retry" => "Pending",
        _ => null,
    };
    if (newState is null) return Results.BadRequest(new { error = "unknown action" });
    using var c = db.Open();
    var n = c.Execute("UPDATE tracks SET state=@s, updated_at=datetime('now') WHERE id=@id", new { s = newState, id });
    return n > 0 ? Results.Ok(new { id, state = newState }) : Results.NotFound(new { error = "track not found" });
});

// Manually fix a track's metadata (e.g. artist wrongly = channel name). Marks enriched so
// a later download won't overwrite the manual fix.
app.MapPut("/api/tracks/{id:long}", (long id, TrackEdit e) =>
{
    using var c = db.Open();
    var n = c.Execute(
        "UPDATE tracks SET artist=@Artist, title=@Title, album=@Album, enriched=1, updated_at=datetime('now') WHERE id=@id",
        new { e.Artist, e.Title, e.Album, id });
    return n > 0 ? Results.Ok(new { id }) : Results.NotFound(new { error = "track not found" });
});

// Track detail: the track plus its last download attempt (for failure reason / path).
app.MapGet("/api/tracks/{id:long}", (long id) =>
{
    using var c = db.Open();
    var t = c.QuerySingleOrDefault<Track>("SELECT * FROM tracks WHERE id=@id", new { id });
    if (t is null) return Results.NotFound();
    var attempt = c.QueryFirstOrDefault(
        "SELECT started_at, finished_at, result, failure_reason FROM download_attempts WHERE track_id=@id ORDER BY id DESC LIMIT 1",
        new { id });
    return Results.Ok(new { track = t, lastAttempt = attempt });
});

// Bulk: requeue all Failed tracks of a source.
app.MapPost("/api/sources/{id:long}/retry-failed", (long id) =>
{
    using var c = db.Open();
    var n = c.Execute("UPDATE tracks SET state='Pending', updated_at=datetime('now') WHERE source_id=@id AND state='Failed'", new { id });
    return Results.Ok(new { requeued = n });
});

List<string> AllowedRoots()
{
    var roots = new List<string>();
    if (app.Configuration["MusicLibDir"] is { Length: > 0 } lib) roots.Add(Path.GetFullPath(lib));
    using var cc = db.Open();
    roots.AddRange(cc.Query<string>("SELECT DISTINCT dest_dir FROM sources WHERE dest_dir IS NOT NULL")
        .Where(d => !string.IsNullOrWhiteSpace(d)).Select(Path.GetFullPath));
    return roots;
}
static string AudioCt(string p) => Path.GetExtension(p).ToLowerInvariant() switch
{
    ".flac" => "audio/flac",
    ".mp3" => "audio/mpeg",
    ".m4a" => "audio/mp4",
    ".ogg" or ".opus" => "audio/ogg",
    ".wav" => "audio/wav",
    _ => "application/octet-stream",
};

// Stream an audio file for in-browser playback — only files under the library/inbox roots.
app.MapGet("/api/audio", (string path) =>
{
    string full;
    try { full = Path.GetFullPath(path); } catch { return Results.BadRequest(); }
    var ok = AllowedRoots().Any(r => full == r || full.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    if (!ok || !File.Exists(full)) return Results.NotFound();
    return Results.File(full, AudioCt(full), enableRangeProcessing: true);
});

// Fuzzy library candidates for a track (for the manual "find in library" picker).
app.MapGet("/api/tracks/{id:long}/candidates", (long id) => Results.Ok(reconcile.Candidates(id)));

// Manually link a track to a chosen library file (moves it to Manual).
app.MapPost("/api/tracks/{id:long}/match", (long id, MatchInput m) =>
{
    if (string.IsNullOrWhiteSpace(m.Path)) return Results.BadRequest(new { error = "path required" });
    using var c = db.Open();
    var n = c.Execute("UPDATE tracks SET state='Manual', file_path=@p, updated_at=datetime('now') WHERE id=@id", new { p = m.Path, id });
    c.Execute("UPDATE library_files SET matched_track_id=@id WHERE path=@p", new { id, p = m.Path });
    return n > 0 ? Results.Ok(new { id, state = "Manual" }) : Results.NotFound();
});

// Re-run auto-match for one track (used right after editing its tags).
app.MapPost("/api/tracks/{id:long}/rematch", (long id) => Results.Ok(new { matched = reconcile.RematchOne(id) }));

app.MapGet("/api/cookies/status", () =>
{
    var fi = new FileInfo(cookiesPath);
    return Results.Ok(new { present = fi.Exists, updatedAt = fi.Exists ? fi.LastWriteTimeUtc : (DateTime?)null });
});

app.MapPost("/api/cookies", async (HttpRequest req) =>
{
    using var reader = new StreamReader(req.Body);
    var content = await reader.ReadToEndAsync();
    if (string.IsNullOrWhiteSpace(content) || !content.Contains("youtube", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "That does not look like a YouTube cookies.txt file." });
    Directory.CreateDirectory(dataDir);
    await File.WriteAllTextAsync(cookiesPath, content);
    return Results.Ok(new { present = true });
});

app.MapGet("/api/tracks", (int? limit, int? offset, long? sourceId, string? state, string? q, string? sort) =>
{
    using var c = db.Open();
    var lim = Math.Clamp(limit ?? 50, 1, 500);
    var off = Math.Max(offset ?? 0, 0);

    var where = new List<string>();
    if (sourceId is not null) where.Add("source_id=@sourceId");
    if (!string.IsNullOrWhiteSpace(state)) where.Add("state=@state");
    if (!string.IsNullOrWhiteSpace(q)) where.Add("(artist LIKE @q OR title LIKE @q OR album LIKE @q)");
    var wsql = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";
    var order = sort switch
    {
        "artist" => "artist COLLATE NOCASE",
        "title" => "title COLLATE NOCASE",
        "album" => "album COLLATE NOCASE",
        "state" => "state COLLATE NOCASE",
        _ => "updated_at DESC",
    };
    var pars = new { sourceId, state, q = "%" + (q ?? "") + "%", lim, off };

    var total = c.ExecuteScalar<long>($"SELECT COUNT(*) FROM tracks {wsql}", pars);
    var items = c.Query<Track>(
        $"SELECT * FROM tracks {wsql} ORDER BY {order} LIMIT @lim OFFSET @off", pars);
    return Results.Ok(new { total, items });
});

// SPA fallback — only matters when a built wwwroot/index.html exists (prod).
app.MapFallbackToFile("index.html");

app.Run();
