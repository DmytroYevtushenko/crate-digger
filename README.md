# Crate

Auto-downloader for music from YouTube playlists via Soulseek (`sldl`/`sockseek`), with honest
state tracking and a web dashboard.

## Stack
- **Backend:** ASP.NET Core Minimal API (.NET 10) + SQLite + Dapper (no EF/DDD — deliberately simple).
- **Frontend:** React + TypeScript (Vite).
- **Download engine:** `sldl` as a subprocess.
- **Deploy:** Docker / docker-compose (Portainer).

## Status
- **M0** — server recon: done.
- **M1** — scaffold (API + SQLite + SPA + Docker): done.
- **M2** — playlist import (`yt-dlp`): done (986 tracks verified).
- **M3** — download via `sldl`: logic done, verified on a mock; `sldl` bundled (sockseek v3.0.4), real downloads pending credentials on the server.
- **M4** — verification (ffprobe duration + optional fpcalc/AcoustID): done (verified on mock).
- **M5** — library reconcile (scan + tag/duration match, recognizes manual additions): done (verified on mock).
- **M6** — scheduler (cron) + multi-source + per-source quality/schedule UI: done (verified).
- **M7** — polish: auto-tag downloads (Picard-lite), review-queue actions, cookies upload, tooltips: done (verified). Deploy is the user's step.

## Run in dev
Backend (port 8080, set in `launchSettings.json`):
```bash
dotnet run --project backend
```
Frontend (port 5173, proxies `/api` and `/health` to 8080):
```bash
npm --prefix frontend install
npm --prefix frontend run dev
```
Open http://localhost:5173

## Run via Docker (as in prod)
```bash
docker compose up --build
```
Open http://localhost:8080 (SPA + API in one container, DB in the `./data` volume).

## Configuration (env)

| Variable | Purpose | Default |
|---|---|---|
| `DbPath` | SQLite path | `/data/crate.db` (container) |
| `YtDlpPath` | yt-dlp binary | `yt-dlp` (in PATH) |
| `SldlPath` | sldl/sockseek binary | `sldl` |
| `MusicLibDir` | master library for `--skip-music-dir` | — |
| `SldlIndexPath` | sldl index | — |
| `SLDL_USER` / `SLDL_PASS` | Soulseek credentials (env only, never in code) | — |
| `ACOUSTID_KEY` | AcoustID key for verification (optional) | — |
| `CookiesPath` | yt-dlp `cookies.txt` (uploadable from the dashboard) | `<data>/cookies.txt` |
| `VerifyDurationTolSec` | allowed duration diff (s) when verifying | `7` |

## Real downloads (M3)

Download logic is done and verified against a mock `sldl`. `sldl` is bundled in the image
(pinned `sockseek` v3.0.4, all required flags verified). For real downloads:
1. Create `.env` from `.env.example`, set `SLDL_USER` / `SLDL_PASS`.
2. When creating a source, set `destDir` to the **path inside the container** (`/library/inbox`), not the host path.
3. The dashboard’s “Download ×25” button queues the missing tracks; states
   `Downloaded` / `Manual` (already in library) / `Failed` update live.

To use your own sldl binary instead of the bundled one, mount it and set `SldlPath` (see `docker-compose.yml`).

> ⚠️ Success is detected by a new audio file appearing in `destDir` plus stdout markers.
> On the first real run, double-check the sldl output — the parsing may need a small tweak per version.

## Deploy (Portainer)

The repo is self-contained — the image bundles `sldl`/`yt-dlp`/`fpcalc`/`ffmpeg`. In Portainer, create a
**Stack** from this git repository (it uses `docker-compose.yml`), set `SLDL_USER` / `SLDL_PASS` (and the
optional `ACOUSTID_KEY`) as stack environment variables, and deploy. Pin/upgrade the downloader via the
`SOCKSEEK_VERSION` build arg.
