# Crate

**Keep a music library automatically filled from your YouTube / YouTube Music playlists — downloaded in real quality (FLAC) from the Soulseek network, with a web dashboard so you can see and control everything.**

You point Crate at a playlist. It reads the track list, figures out what you *don't* already
have in your library, downloads the missing tracks from Soulseek, checks that each file is
actually the right song (not a wrong/live version), and tags it. A dashboard shows every track's
status; a scheduler can run it automatically.

It's built around three well-known tools (all bundled in the Docker image — you don't install them):

- **yt-dlp** – reads the playlist and each track's clean metadata (artist / title / album).
- **sldl / sockseek** – the actual downloader for the Soulseek peer-to-peer network.
- **fpcalc + ffmpeg** – verify downloads by duration, tags, and acoustic fingerprint.

---

## What you need before starting

1. **A Soulseek account.** Soulseek is a peer-to-peer network for sharing music. If you don't have
   an account, just pick a **username** and **password** — Soulseek registers it automatically the
   first time it connects. (You can also create one with the free Soulseek desktop app from
   [slsknet.org](https://www.slsknet.org/).) These become `SLDL_USER` / `SLDL_PASS`.
2. **Docker** (with Docker Compose) or **Portainer** on the machine that will run Crate.
3. **Two folders** on that machine:
   - your **existing music library** (read-only — Crate only reads it to know what you already own),
   - an **inbox** folder where new downloads will land.

That's it. Everything else (the downloader, yt-dlp, ffmpeg…) is inside the image.

---

## Quick start (Docker Compose)

```bash
git clone <this-repo> crate && cd crate
cp .env.example .env
```

Open `.env` and fill in the four things that matter (see the table below):

```ini
SLDL_USER=your-soulseek-username
SLDL_PASS=your-soulseek-password
MUSIC_LIB=/path/to/your/music/library      # your existing library (read-only)
INBOX=/path/to/your/download/inbox         # where new downloads go
```

Then start it:

```bash
docker compose up -d --build
```

Open the dashboard at **http://SERVER-IP:8080** (or whatever `CRATE_PORT` you set).

### Portainer

Create a **Stack → from Git repository**, point it at this repo (it uses `docker-compose.yml`),
and add the same variables (`SLDL_USER`, `SLDL_PASS`, `MUSIC_LIB`, `INBOX`, optionally `CRATE_PORT`)
in the stack's **Environment variables** section. Deploy.

---

## Environment variables — what they are and where to get them

**You only need to set these four:**

| Variable | What it is | Where to get it / example |
|---|---|---|
| `SLDL_USER` | Your **Soulseek username** | Your Soulseek account (pick any name; it registers on first connect) |
| `SLDL_PASS` | Your **Soulseek password** | Same account |
| `MUSIC_LIB` | Full path to your **existing music library** on the host (mounted read-only) | e.g. `/mnt/storage/Music` |
| `INBOX` | Full path where **new downloads** are placed | e.g. `/mnt/storage/Downloads` |

**Optional:**

| Variable | What it is | Default |
|---|---|---|
| `CRATE_PORT` | Host port for the dashboard (change if the port is already used) | `8080` |
| `ACOUSTID_KEY` | Free key from [acoustid.org](https://acoustid.org/new-application) for extra fingerprint checks. **You can leave this empty** — verification still works via duration, tags, and the YouTube reference. | empty |
| `VerifyYtFingerprint` | Set to `false` to skip the YouTube-reference fingerprint check (it downloads ~90s of the source for *suspicious* downloads only). | on |
| `VerifyBerThreshold` | Fingerprint match strictness (lower = stricter). | `0.35` |

**Leave these alone** — they're set automatically by `docker-compose.yml` and describe paths
*inside* the container, not things you configure: `DbPath`, `SldlPath`, `MusicLibDir`, `SldlIndexPath`.

> The database, cookies, and index live in the `./data` folder (a Docker volume), so they survive
> restarts and redeploys. Your music files live in your own folders and are never modified there.

---

## First run (using the dashboard)

1. **Add a source.** In *“Add source (playlist)”* paste your playlist URL, give it a name, choose a
   quality (FLAC by default) and a schedule (e.g. *Twice a day*), and click **Add**.
2. **Sync now.** In the source's row click **Sync now** — Crate fetches the playlist and lists its
   tracks as `Pending`.
3. **Scan library.** Click **Scan library** (top-right card). Crate scans your `MUSIC_LIB`, and any
   track you already own flips to `Manual` (it won't be downloaded again).
4. **Download.** Click **Download ×25** (or **⬇ all**). Crate downloads the missing tracks, verifies
   each, and marks them `Verified`. Bad matches go to `Mismatch` for you to review.
5. **Curate.** New files land in your `INBOX` (container path `/library/inbox`). Tag/organize them
   into your library however you like (e.g. with MusicBrainz Picard). Run **Scan library** again and
   they become `Manual`.

**Once a schedule is set, steps 2 & 4 happen automatically** — you don't need to press buttons.

**Track statuses:** `Pending` (queued to find) · `Downloading` · `Verified` (downloaded & checked) ·
`Manual` (already in your library) · `Mismatch` (downloaded but looks wrong — review it) ·
`Failed` (not found) · `Blacklisted` (you rejected it).

Click any track row to expand it: play it in the browser, edit its tags, find candidates in your
library, or **Retry download** at a lower quality if it's stuck.

---

## Troubleshooting

- **“port is already allocated”** — another app uses that port. Set `CRATE_PORT` to a free one
  (e.g. `8095`) and redeploy.
- **Playlist sync is unreliable / “Sign in to confirm you're not a bot”** — YouTube is rate-limiting
  you. Upload a `cookies.txt` in the dashboard's *YouTube cookies* card (export it with a
  “Get cookies.txt” browser extension while logged in to YouTube). This also enables the strongest
  fingerprint verification.
- **A track won't download** — open its row: fix the tags if the artist is wrong, click
  **Retry download → Any** to accept any quality, or **Find in library** to match it manually. If it's
  simply not on Soulseek right now, leave it `Pending` (the scheduler keeps trying) or grab it in a
  desktop Soulseek client and drop it into your inbox.

---

## Run locally (development)

Backend (port 8080):

```bash
dotnet run --project backend
```

Frontend (port 5173, proxies the API to 8080):

```bash
npm --prefix frontend install
npm --prefix frontend run dev
```

Open http://localhost:5173. Stack: ASP.NET Core Minimal API (.NET 10) + SQLite + Dapper;
React + TypeScript (Vite). Design notes and roadmap are kept locally in `PLAN.md`.
