import { useCallback, useEffect, useState } from 'react'
import type { ChangeEvent, FormEvent } from 'react'
import './dashboard.css'

type Health = { status: string; db: boolean }
type Stats = { sources: number; tracks: number; byState: Record<string, number> }
type Source = {
  id: number
  name: string
  url: string
  destDir: string
  scheduleCron: string | null
  lastRunAt: string | null
  enabled: boolean
}
type Track = {
  id: number
  sourceId: number
  artist: string | null
  title: string | null
  album: string | null
  durationSec: number | null
  state: string
  enriched: boolean
}
type LibStatus = { running: boolean; libraryFiles: number; matched: number; last: string | null }
type CookieStatus = { present: boolean; updatedAt: string | null }

// Quality presets -> sldl conditions (--cond / --pref-format).
const QUALITY: Record<string, { label: string; cond: string; pref: string }> = {
  flac: { label: 'FLAC (lossless)', cond: 'format == flac, bitrate >= 600', pref: 'flac' },
  gte320: { label: '≥ 320 kbps', cond: 'bitrate >= 320', pref: 'mp3' },
  lte320: { label: '≤ 320 kbps', cond: 'bitrate <= 320', pref: 'mp3' },
  lt320: { label: '< 320 kbps', cond: 'bitrate < 320', pref: 'mp3' },
  any: { label: 'Any', cond: '', pref: 'flac' },
}

// Schedule presets -> cron (fill the cron field; user can still tweak).
const SCHEDULES: { label: string; cron: string }[] = [
  { label: 'Manual (no schedule)', cron: '' },
  { label: 'Hourly', cron: '0 * * * *' },
  { label: 'Every 6 hours', cron: '0 */6 * * *' },
  { label: 'Twice a day (03:00 & 15:00)', cron: '0 3,15 * * *' },
  { label: 'Daily (03:00)', cron: '0 3 * * *' },
  { label: 'Weekly (Sun 03:00)', cron: '0 3 * * 0' },
]

async function api<T>(path: string, opts?: RequestInit): Promise<T> {
  const r = await fetch(path, { headers: { 'Content-Type': 'application/json' }, ...opts })
  if (!r.ok) throw new Error(`${r.status} ${await r.text()}`)
  return r.json() as Promise<T>
}

function fmtDur(s: number | null): string {
  if (!s) return '—'
  const m = Math.floor(s / 60)
  const ss = String(s % 60).padStart(2, '0')
  return `${m}:${ss}`
}

export default function App() {
  const [health, setHealth] = useState<Health | null>(null)
  const [stats, setStats] = useState<Stats | null>(null)
  const [sources, setSources] = useState<Source[]>([])
  const [tracks, setTracks] = useState<Track[]>([])
  const [lib, setLib] = useState<LibStatus | null>(null)
  const [cookies, setCookies] = useState<CookieStatus | null>(null)
  const [busy, setBusy] = useState<string | null>(null)
  const [err, setErr] = useState<string | null>(null)
  const [note, setNote] = useState<string | null>(null)
  const [form, setForm] = useState({
    name: '',
    url: '',
    destDir: '/library/inbox',
    quality: 'flac',
    schedule: '',
  })

  const refresh = useCallback(async () => {
    try {
      const [h, s, src, tr, ls, ck] = await Promise.all([
        api<Health>('/health'),
        api<Stats>('/api/stats'),
        api<Source[]>('/api/sources'),
        api<Track[]>('/api/tracks?limit=200'),
        api<LibStatus>('/api/reconcile/status'),
        api<CookieStatus>('/api/cookies/status'),
      ])
      setHealth(h)
      setStats(s)
      setSources(src)
      setTracks(tr)
      setLib(ls)
      setCookies(ck)
      setErr(null)
    } catch (e) {
      setErr(String(e))
    }
  }, [])

  useEffect(() => {
    void refresh()
  }, [refresh])

  // Live polling while any track is in progress (Queued/Downloading).
  const active = lib?.running === true || tracks.some((t) => t.state === 'Queued' || t.state === 'Downloading')
  useEffect(() => {
    if (!active) return
    const h = setInterval(() => void refresh(), 2500)
    return () => clearInterval(h)
  }, [active, refresh])

  const onField = (k: keyof typeof form) => (e: ChangeEvent<HTMLInputElement>) =>
    setForm((f) => ({ ...f, [k]: e.target.value }))

  async function addSource(e: FormEvent) {
    e.preventDefault()
    if (!form.name || !form.url) return
    setBusy('add')
    const body = {
      name: form.name,
      url: form.url,
      destDir: form.destDir,
      cond: QUALITY[form.quality]?.cond ?? '',
      pref: QUALITY[form.quality]?.pref ?? 'flac',
      scheduleCron: form.schedule || null,
    }
    try {
      await api('/api/sources', { method: 'POST', body: JSON.stringify(body) })
      setForm((f) => ({ ...f, name: '', url: '' }))
      await refresh()
    } catch (e) {
      setErr(String(e))
    } finally {
      setBusy(null)
    }
  }

  async function run(id: number, action: 'sync' | 'enrich' | 'download') {
    setBusy(`${action}-${id}`)
    try {
      const path =
        action === 'enrich'
          ? `/api/sources/${id}/enrich?limit=50`
          : action === 'download'
            ? `/api/sources/${id}/download?limit=25`
            : `/api/sources/${id}/sync`
      const res = await api<{ sldlConfigured?: boolean }>(path, { method: 'POST' })
      if (action === 'download' && res.sldlConfigured === false)
        setNote('sldl has no Soulseek credentials (SLDL_USER / SLDL_PASS) — real downloads will not run.')
      await refresh()
    } catch (e) {
      setErr(String(e))
    } finally {
      setBusy(null)
    }
  }

  async function reconcileNow() {
    setBusy('reconcile')
    try {
      await api('/api/reconcile', { method: 'POST' })
      await refresh()
    } catch (e) {
      setErr(String(e))
    } finally {
      setBusy(null)
    }
  }

  async function trackAction(id: number, action: 'confirm' | 'reject' | 'retry') {
    setBusy(`t-${action}-${id}`)
    try {
      await api(`/api/tracks/${id}/${action}`, { method: 'POST' })
      await refresh()
    } catch (e) {
      setErr(String(e))
    } finally {
      setBusy(null)
    }
  }

  async function onCookieFile(e: ChangeEvent<HTMLInputElement>) {
    const file = e.target.files?.[0]
    if (!file) return
    setBusy('cookies')
    try {
      const text = await file.text()
      await api('/api/cookies', { method: 'POST', headers: { 'Content-Type': 'text/plain' }, body: text })
      setNote('YouTube cookies uploaded.')
      await refresh()
    } catch (err) {
      setErr(String(err))
    } finally {
      setBusy(null)
      e.target.value = ''
    }
  }

  return (
    <div className="wrap">
      <header>
        <h1>Crate</h1>
        <span className="sub">YouTube → Soulseek · download dashboard</span>
      </header>

      {err && <div className="card err">API error: {err}</div>}
      {note && <div className="card warn">{note}</div>}

      <div className="grid">
        <div className="card">
          <div className="label">API</div>
          <div className={`val ${health?.status === 'ok' ? 'ok' : 'bad'}`}>
            {health?.status === 'ok' ? '● online' : '○ offline'}
          </div>
          <div className="muted">DB: {health?.db ? 'ok' : '—'}</div>
        </div>
        <div className="card">
          <div className="label">Sources</div>
          <div className="val">{stats?.sources ?? '—'}</div>
        </div>
        <div className="card">
          <div className="label">Tracks</div>
          <div className="val">{stats?.tracks ?? '—'}</div>
          <div className="muted">
            {stats
              ? Object.entries(stats.byState)
                  .map(([s, n]) => `${s}: ${n}`)
                  .join(' · ')
              : ''}
          </div>
        </div>
        <div className="card">
          <div className="label">Library</div>
          <div className="val">{lib?.libraryFiles ?? '—'}</div>
          <div className="muted">
            matched: {lib?.matched ?? 0}
            {lib?.running ? ' · scanning…' : ''}
          </div>
          <button
            className="ghost"
            style={{ marginTop: 8 }}
            disabled={busy === 'reconcile' || lib?.running === true}
            onClick={reconcileNow}
            title="Scan your music folders and mark tracks you already have, so they aren't downloaded again"
          >
            {lib?.running ? '…' : 'Scan library'}
          </button>
        </div>
      </div>

      <div className="card">
        <div className="label">Add source (playlist)</div>
        <form className="srcform" onSubmit={addSource}>
          <input placeholder="Name" value={form.name} onChange={onField('name')} />
          <input placeholder="Playlist URL" value={form.url} onChange={onField('url')} />
          <input placeholder="Download folder" value={form.destDir} onChange={onField('destDir')} />
          <select value={form.quality} onChange={(e) => setForm((f) => ({ ...f, quality: e.target.value }))} title="Quality">
            {Object.entries(QUALITY).map(([k, v]) => (
              <option key={k} value={k}>
                {v.label}
              </option>
            ))}
          </select>
          <select
            defaultValue=""
            onChange={(e) => setForm((f) => ({ ...f, schedule: e.target.value }))}
            title="Schedule preset (fills the cron field)"
          >
            {SCHEDULES.map((s) => (
              <option key={s.label} value={s.cron}>
                {s.label}
              </option>
            ))}
          </select>
          <input placeholder="cron, e.g. 0 3,15 * * * (optional)" value={form.schedule} onChange={onField('schedule')} />
          <button type="submit" disabled={busy === 'add'}>
            {busy === 'add' ? '…' : 'Add'}
          </button>
        </form>
      </div>

      <div className="card">
        <div className="label">
          YouTube cookies{' '}
          {cookies?.present ? (
            <span className="muted">· uploaded {cookies.updatedAt ?? ''}</span>
          ) : (
            <span className="muted">· not set (optional)</span>
          )}
        </div>
        <div className="muted small">
          Improves playlist-sync reliability and enables fingerprint verification (YouTube gates audio behind a login).
          Install a “Get cookies.txt” browser extension, log in to YouTube, export <code>cookies.txt</code>, and upload it here.
        </div>
        <input type="file" accept=".txt" disabled={busy === 'cookies'} onChange={onCookieFile} style={{ marginTop: 8 }} />
      </div>

      {sources.length > 0 && (
        <div className="card">
          <div className="label">Sources</div>
          <div className="muted small">
            Sync now: fetch new playlist tracks · Download: find & grab missing tracks on Soulseek · Enrich: pull clean artist/album
          </div>
          <ul className="srclist">
            {sources.map((s) => (
              <li key={s.id}>
                <div>
                  <b>{s.name}</b>
                  <span className="muted"> · {s.destDir}</span>
                  <div className="muted small">
                    schedule: {s.scheduleCron ?? 'manual'} · last sync: {s.lastRunAt ?? 'never'}
                  </div>
                </div>
                <div className="actions">
                  <button
                    title="Fetch the playlist and add any new tracks to the list (no download yet)"
                    disabled={busy === `sync-${s.id}`}
                    onClick={() => run(s.id, 'sync')}
                  >
                    {busy === `sync-${s.id}` ? '…' : 'Sync now'}
                  </button>
                  <button
                    disabled={busy === `download-${s.id}`}
                    onClick={() => run(s.id, 'download')}
                    title="Search Soulseek and download up to 25 tracks you don't have yet"
                  >
                    {busy === `download-${s.id}` ? '…' : 'Download ×25'}
                  </button>
                  <button
                    className="ghost"
                    disabled={busy === `enrich-${s.id}`}
                    onClick={() => run(s.id, 'enrich')}
                    title="Fetch clean artist / track / album metadata for up to 50 tracks"
                  >
                    {busy === `enrich-${s.id}` ? '…' : 'Enrich ×50'}
                  </button>
                </div>
              </li>
            ))}
          </ul>
        </div>
      )}

      <div className="card">
        <div className="label">
          Tracks {tracks.length > 0 && <span className="muted">(showing {tracks.length})</span>}
          {active && <span className="muted"> · updating…</span>}
        </div>
        {tracks.length === 0 ? (
          <p className="muted">Empty. Add a source and hit “Sync now”.</p>
        ) : (
          <div className="tablewrap">
            <table>
              <thead>
                <tr>
                  <th>Artist</th>
                  <th>Track</th>
                  <th>Album</th>
                  <th>Length</th>
                  <th>State</th>
                  <th></th>
                </tr>
              </thead>
              <tbody>
                {tracks.map((t) => (
                  <tr key={t.id}>
                    <td>{t.artist ?? '—'}</td>
                    <td>{t.title ?? '—'}</td>
                    <td>{t.album ?? (t.enriched ? '—' : <span className="muted">…</span>)}</td>
                    <td>{fmtDur(t.durationSec)}</td>
                    <td>
                      <span className={`badge s-${t.state.toLowerCase()}`}>{t.state}</span>
                    </td>
                    <td>
                      {t.state === 'Mismatch' && (
                        <>
                          <button
                            className="mini"
                            title="This match is actually correct — mark as Verified"
                            onClick={() => trackAction(t.id, 'confirm')}
                          >
                            ✓ ok
                          </button>
                          <button
                            className="mini ghost"
                            title="Wrong track — blacklist so it isn't retried"
                            onClick={() => trackAction(t.id, 'reject')}
                          >
                            ✕ no
                          </button>
                        </>
                      )}
                      {(t.state === 'Failed' || t.state === 'Blacklisted') && (
                        <button
                          className="mini"
                          title="Queue this track again for download"
                          onClick={() => trackAction(t.id, 'retry')}
                        >
                          ↻ retry
                        </button>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>

      <footer className="muted">Crate · playlist → download → verify → reconcile</footer>
    </div>
  )
}
