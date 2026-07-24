import { Fragment, useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react'
import type { ChangeEvent, FormEvent } from 'react'
import './dashboard.css'

type Health = { status: string; db: boolean }
type Stats = { sources: number; tracks: number; byState: Record<string, number> }
type Source = {
  id: number
  name: string
  url: string
  destDir: string
  cond: string | null
  pref: string | null
  scheduleCron: string | null
  enabled: boolean
  lastRunAt: string | null
}
type Track = {
  id: number
  sourceId: number
  externalId: string | null
  artist: string | null
  title: string | null
  album: string | null
  durationSec: number | null
  state: string
  enriched: boolean
  filePath: string | null
}
type LibStatus = { running: boolean; libraryFiles: number; matched: number; last: string | null }
type CookieStatus = { present: boolean; updatedAt: string | null }
type TracksPage = { total: number; items: Track[] }
type Attempt = { result?: string; failure_reason?: string; finished_at?: string }
type Cand = { path: string; artist: string | null; title: string | null; durationSec: number | null; score: number }

function audioUrl(path: string): string {
  return `/api/audio?path=${encodeURIComponent(path)}`
}

const QUALITY: Record<string, { label: string; cond: string; pref: string }> = {
  flac: { label: 'FLAC (lossless)', cond: 'format == flac, bitrate >= 600', pref: 'flac' },
  gte320: { label: '≥ 320 kbps', cond: 'bitrate >= 320', pref: 'mp3' },
  lte320: { label: '≤ 320 kbps', cond: 'bitrate <= 320', pref: 'mp3' },
  lt320: { label: '< 320 kbps', cond: 'bitrate < 320', pref: 'mp3' },
  any: { label: 'Any', cond: '', pref: 'flac' },
}

const SCHEDULES: { label: string; cron: string }[] = [
  { label: 'Manual (no schedule)', cron: '' },
  { label: 'Hourly', cron: '0 * * * *' },
  { label: 'Every 6 hours', cron: '0 */6 * * *' },
  { label: 'Twice a day (03:00 & 15:00)', cron: '0 3,15 * * *' },
  { label: 'Daily (03:00)', cron: '0 3 * * *' },
  { label: 'Weekly (Sun 03:00)', cron: '0 3 * * 0' },
]

const PAGE_SIZE = 50

async function api<T>(path: string, opts?: RequestInit): Promise<T> {
  const r = await fetch(path, { headers: { 'Content-Type': 'application/json' }, ...opts })
  if (!r.ok) throw new Error(`${r.status} ${await r.text()}`)
  return r.json() as Promise<T>
}

function fmtDur(s: number | null): string {
  if (!s) return '—'
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`
}

function qualityFromCond(cond: string | null): string {
  const found = Object.entries(QUALITY).find(([, v]) => v.cond === (cond ?? ''))
  return found ? found[0] : 'flac'
}

type EditForm = { name: string; url: string; destDir: string; quality: string; schedule: string; enabled: boolean }

export default function App() {
  const [health, setHealth] = useState<Health | null>(null)
  const [stats, setStats] = useState<Stats | null>(null)
  const [sources, setSources] = useState<Source[]>([])
  const [cookies, setCookies] = useState<CookieStatus | null>(null)
  const [lib, setLib] = useState<LibStatus | null>(null)

  const [tracks, setTracks] = useState<Track[]>([])
  const [tracksTotal, setTracksTotal] = useState(0)
  const [filter, setFilter] = useState('')
  const [q, setQ] = useState('')
  const [page, setPage] = useState(0)
  const [sort, setSort] = useState('')
  const [sourceFilter, setSourceFilter] = useState<number | ''>('')

  const [openId, setOpenId] = useState<number | null>(null)
  const [edit, setEdit] = useState({ artist: '', title: '', album: '' })
  const [detail, setDetail] = useState<Attempt | null>(null)
  const [cands, setCands] = useState<Cand[]>([])
  const preserveScroll = useRef<number | null>(null)

  const [busy, setBusy] = useState<string | null>(null)
  const [err, setErr] = useState<string | null>(null)
  const [note, setNote] = useState<string | null>(null)
  const [form, setForm] = useState({ name: '', url: '', destDir: '/library/inbox', quality: 'flac', schedule: '' })
  const [editing, setEditing] = useState<number | null>(null)
  const [editForm, setEditForm] = useState<EditForm>({ name: '', url: '', destDir: '', quality: 'flac', schedule: '', enabled: true })

  const refreshMeta = useCallback(async () => {
    try {
      const [h, s, src, ck, ls] = await Promise.all([
        api<Health>('/health'),
        api<Stats>('/api/stats'),
        api<Source[]>('/api/sources'),
        api<CookieStatus>('/api/cookies/status'),
        api<LibStatus>('/api/reconcile/status'),
      ])
      setHealth(h); setStats(s); setSources(src); setCookies(ck); setLib(ls); setErr(null)
    } catch (e) {
      setErr(String(e))
    }
  }, [])

  const loadTracks = useCallback(async () => {
    const p = new URLSearchParams({ limit: String(PAGE_SIZE), offset: String(page * PAGE_SIZE) })
    if (filter) p.set('state', filter)
    if (q.trim()) p.set('q', q.trim())
    if (sort) p.set('sort', sort)
    if (sourceFilter !== '') p.set('sourceId', String(sourceFilter))
    try {
      const r = await api<TracksPage>(`/api/tracks?${p}`)
      setTracks(r.items); setTracksTotal(r.total)
    } catch (e) {
      setErr(String(e))
    }
  }, [filter, q, page, sort, sourceFilter])

  useEffect(() => { void refreshMeta() }, [refreshMeta])

  // Keep the scroll position across track reloads (edit / actions / pagination) instead of jumping.
  useLayoutEffect(() => {
    if (preserveScroll.current != null) {
      window.scrollTo(0, preserveScroll.current)
      preserveScroll.current = null
    }
  }, [tracks])
  useEffect(() => {
    const h = setTimeout(() => void loadTracks(), 250)
    return () => clearTimeout(h)
  }, [loadTracks])

  const active = lib?.running === true || tracks.some((t) => t.state === 'Queued' || t.state === 'Downloading')
  useEffect(() => {
    if (!active) return
    const h = setInterval(() => { void refreshMeta(); void loadTracks() }, 2500)
    return () => clearInterval(h)
  }, [active, refreshMeta, loadTracks])

  const onField = (k: keyof typeof form) => (e: ChangeEvent<HTMLInputElement>) =>
    setForm((f) => ({ ...f, [k]: e.target.value }))

  async function addSource(e: FormEvent) {
    e.preventDefault()
    if (!form.name || !form.url) return
    setBusy('add')
    try {
      await api('/api/sources', {
        method: 'POST',
        body: JSON.stringify({
          name: form.name, url: form.url, destDir: form.destDir,
          cond: QUALITY[form.quality]?.cond ?? '', pref: QUALITY[form.quality]?.pref ?? 'flac',
          scheduleCron: form.schedule,
        }),
      })
      setForm((f) => ({ ...f, name: '', url: '' }))
      await refreshMeta()
    } catch (e) { setErr(String(e)) } finally { setBusy(null) }
  }

  async function run(id: number, action: 'sync' | 'enrich' | 'download') {
    setBusy(`${action}-${id}`)
    try {
      const path =
        action === 'enrich' ? `/api/sources/${id}/enrich?limit=50`
          : action === 'download' ? `/api/sources/${id}/download?limit=25`
            : `/api/sources/${id}/sync`
      const res = await api<{ sldlConfigured?: boolean }>(path, { method: 'POST' })
      if (action === 'download' && res.sldlConfigured === false)
        setNote('sldl has no Soulseek credentials (SLDL_USER / SLDL_PASS) — real downloads will not run.')
      await Promise.all([refreshMeta(), loadTracks()])
    } catch (e) { setErr(String(e)) } finally { setBusy(null) }
  }

  async function bulk(id: number, kind: 'download-all' | 'retry-failed') {
    setBusy(`bulk-${kind}-${id}`)
    try {
      if (kind === 'download-all') await api(`/api/sources/${id}/download?limit=5000`, { method: 'POST' })
      else await api(`/api/sources/${id}/retry-failed`, { method: 'POST' })
      await Promise.all([refreshMeta(), loadTracks()])
    } catch (e) { setErr(String(e)) } finally { setBusy(null) }
  }

  async function reconcileNow() {
    setBusy('reconcile')
    try { await api('/api/reconcile', { method: 'POST' }); await refreshMeta() }
    catch (e) { setErr(String(e)) } finally { setBusy(null) }
  }

  async function trackAction(id: number, action: 'confirm' | 'reject' | 'retry') {
    setBusy(`t-${action}-${id}`)
    preserveScroll.current = window.scrollY
    try { await api(`/api/tracks/${id}/${action}`, { method: 'POST' }); await Promise.all([refreshMeta(), loadTracks()]) }
    catch (e) { setErr(String(e)) } finally { setBusy(null) }
  }

  async function openTrack(t: Track) {
    if (openId === t.id) { setOpenId(null); return }
    setOpenId(t.id)
    setEdit({ artist: t.artist ?? '', title: t.title ?? '', album: t.album ?? '' })
    setDetail(null)
    setCands([])
    try {
      const d = await api<{ lastAttempt: Attempt | null }>(`/api/tracks/${t.id}`)
      setDetail(d.lastAttempt)
    } catch { /* ignore */ }
  }

  async function saveTrackEdit(id: number) {
    setBusy(`edit-${id}`)
    preserveScroll.current = window.scrollY
    try {
      await api(`/api/tracks/${id}`, { method: 'PUT', body: JSON.stringify(edit) })
      const r = await api<{ matched: boolean }>(`/api/tracks/${id}/rematch`, { method: 'POST' })
      setNote(r.matched ? 'Matched to your library → moved to Manual.' : 'Saved. No library match yet — still Pending.')
      setOpenId(null)
      await Promise.all([refreshMeta(), loadTracks()])
    } catch (e) { setErr(String(e)) } finally { setBusy(null) }
  }

  async function loadCandidates(id: number) {
    setBusy(`cand-${id}`)
    try { setCands(await api<Cand[]>(`/api/tracks/${id}/candidates`)) }
    catch (e) { setErr(String(e)) } finally { setBusy(null) }
  }
  async function useCandidate(id: number, path: string) {
    setBusy(`use-${id}`)
    preserveScroll.current = window.scrollY
    try {
      await api(`/api/tracks/${id}/match`, { method: 'POST', body: JSON.stringify({ path }) })
      setOpenId(null); await Promise.all([refreshMeta(), loadTracks()])
    } catch (e) { setErr(String(e)) } finally { setBusy(null) }
  }

  function toggleSort(col: string) { setSort((s) => (s === col ? '' : col)); setPage(0) }
  const arrow = (c: string) => (sort === c ? ' ▲' : '')

  function startEdit(s: Source) {
    setEditing(s.id)
    setEditForm({ name: s.name, url: s.url, destDir: s.destDir, quality: qualityFromCond(s.cond), schedule: s.scheduleCron ?? '', enabled: s.enabled })
  }
  async function saveEdit(id: number) {
    setBusy(`save-${id}`)
    try {
      await api(`/api/sources/${id}`, {
        method: 'PUT',
        body: JSON.stringify({
          name: editForm.name, url: editForm.url, destDir: editForm.destDir,
          cond: QUALITY[editForm.quality]?.cond ?? '', pref: QUALITY[editForm.quality]?.pref ?? 'flac',
          scheduleCron: editForm.schedule, enabled: editForm.enabled,
        }),
      })
      setEditing(null); await refreshMeta()
    } catch (e) { setErr(String(e)) } finally { setBusy(null) }
  }
  async function deleteSource(id: number) {
    if (!confirm('Delete this source? Tracks stay in the DB.')) return
    setBusy(`del-${id}`)
    try { await api(`/api/sources/${id}`, { method: 'DELETE' }); if (editing === id) setEditing(null); await refreshMeta() }
    catch (e) { setErr(String(e)) } finally { setBusy(null) }
  }

  async function onCookieFile(e: ChangeEvent<HTMLInputElement>) {
    const file = e.target.files?.[0]
    if (!file) return
    setBusy('cookies')
    try {
      const text = await file.text()
      await api('/api/cookies', { method: 'POST', headers: { 'Content-Type': 'text/plain' }, body: text })
      setNote('YouTube cookies uploaded.')
      await refreshMeta()
    } catch (err) { setErr(String(err)) } finally { setBusy(null); e.target.value = '' }
  }

  const pages = Math.max(1, Math.ceil(tracksTotal / PAGE_SIZE))

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
          <div className={`val ${health?.status === 'ok' ? 'ok' : 'bad'}`}>{health?.status === 'ok' ? '● online' : '○ offline'}</div>
          <div className="muted">DB: {health?.db ? 'ok' : '—'}</div>
        </div>
        <div className="card">
          <div className="label">Sources</div>
          <div className="val">{stats?.sources ?? '—'}</div>
        </div>
        <div className="card">
          <div className="label">Tracks</div>
          <div className="val">{stats?.tracks ?? '—'}</div>
        </div>
        <div className="card">
          <div className="label">Library {lib?.running && <span className="muted">· scanning…</span>}</div>
          <div className="val">{lib?.libraryFiles ?? '—'}</div>
          <button className="ghost" style={{ marginTop: 8 }} disabled={busy === 'reconcile' || lib?.running === true} onClick={reconcileNow}
            title="Scan your music folders and mark tracks you already have, so they aren't downloaded again">
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
            {Object.entries(QUALITY).map(([k, v]) => <option key={k} value={k}>{v.label}</option>)}
          </select>
          <select defaultValue="" onChange={(e) => setForm((f) => ({ ...f, schedule: e.target.value }))} title="Schedule preset">
            {SCHEDULES.map((s) => <option key={s.label} value={s.cron}>{s.label}</option>)}
          </select>
          <input placeholder="cron (optional)" value={form.schedule} onChange={onField('schedule')} />
          <button type="submit" disabled={busy === 'add'}>{busy === 'add' ? '…' : 'Add'}</button>
        </form>
      </div>

      <div className="card">
        <div className="label">
          YouTube cookies{' '}
          {cookies?.present ? <span className="muted">· uploaded {cookies.updatedAt ?? ''}</span> : <span className="muted">· not set (optional)</span>}
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
            Sync: fetch new playlist tracks · Download: grab missing on Soulseek · Enrich: clean artist/album · ⬇ all: download every missing track · ↻ failed: requeue failures
          </div>
          <ul className="srclist">
            {sources.map((s) => (
              <li key={s.id}>
                {editing === s.id ? (
                  <div className="editform">
                    <input value={editForm.name} onChange={(e) => setEditForm((f) => ({ ...f, name: e.target.value }))} placeholder="Name" />
                    <input value={editForm.url} onChange={(e) => setEditForm((f) => ({ ...f, url: e.target.value }))} placeholder="URL" />
                    <input value={editForm.destDir} onChange={(e) => setEditForm((f) => ({ ...f, destDir: e.target.value }))} placeholder="Folder" />
                    <select value={editForm.quality} onChange={(e) => setEditForm((f) => ({ ...f, quality: e.target.value }))}>
                      {Object.entries(QUALITY).map(([k, v]) => <option key={k} value={k}>{v.label}</option>)}
                    </select>
                    <select value="" onChange={(e) => setEditForm((f) => ({ ...f, schedule: e.target.value }))} title="Schedule preset">
                      <option value="">— preset —</option>
                      {SCHEDULES.map((sc) => <option key={sc.label} value={sc.cron}>{sc.label}</option>)}
                    </select>
                    <input value={editForm.schedule} onChange={(e) => setEditForm((f) => ({ ...f, schedule: e.target.value }))} placeholder="cron" />
                    <label className="chk"><input type="checkbox" checked={editForm.enabled} onChange={(e) => setEditForm((f) => ({ ...f, enabled: e.target.checked }))} /> enabled</label>
                    <button disabled={busy === `save-${s.id}`} onClick={() => saveEdit(s.id)}>Save</button>
                    <button className="ghost" onClick={() => setEditing(null)}>Cancel</button>
                  </div>
                ) : (
                  <>
                    <div>
                      <b>{s.name}</b>
                      {!s.enabled && <span className="badge s-blacklisted"> paused</span>}
                      <span className="muted"> · {s.destDir}</span>
                      <div className="muted small">schedule: {s.scheduleCron || 'manual'} · last sync: {s.lastRunAt ?? 'never'}</div>
                    </div>
                    <div className="actions">
                      <button title="Fetch the playlist and add new tracks (no download yet)" disabled={busy === `sync-${s.id}`} onClick={() => run(s.id, 'sync')}>{busy === `sync-${s.id}` ? '…' : 'Sync now'}</button>
                      <button title="Download up to 25 missing tracks" disabled={busy === `download-${s.id}`} onClick={() => run(s.id, 'download')}>{busy === `download-${s.id}` ? '…' : 'Download ×25'}</button>
                      <button title="Download ALL missing tracks" disabled={busy === `bulk-download-all-${s.id}`} onClick={() => bulk(s.id, 'download-all')}>{busy === `bulk-download-all-${s.id}` ? '…' : '⬇ all'}</button>
                      <button className="ghost" title="Requeue all Failed tracks" disabled={busy === `bulk-retry-failed-${s.id}`} onClick={() => bulk(s.id, 'retry-failed')}>↻ failed</button>
                      <button className="ghost" title="Fetch clean metadata for up to 50 tracks" disabled={busy === `enrich-${s.id}`} onClick={() => run(s.id, 'enrich')}>{busy === `enrich-${s.id}` ? '…' : 'Enrich ×50'}</button>
                      <button className="ghost" title="Edit source" onClick={() => startEdit(s)}>Edit</button>
                      <button className="ghost" title="Delete source" onClick={() => deleteSource(s.id)}>✕</button>
                    </div>
                  </>
                )}
              </li>
            ))}
          </ul>
        </div>
      )}

      <div className="card">
        <div className="label">Tracks {active && <span className="muted">· updating…</span>}</div>

        <div className="chips">
          <button className={`chip ${filter === '' ? 'on' : ''}`} onClick={() => { setFilter(''); setPage(0) }}>All {stats?.tracks ?? 0}</button>
          {stats && Object.entries(stats.byState).map(([s, n]) => (
            <button key={s} className={`chip ${filter === s ? 'on' : ''}`} onClick={() => { setFilter(s); setPage(0) }}>{s} {n}</button>
          ))}
        </div>

        <div className="trackbar">
          <input className="search" placeholder="Search artist / title / album…" value={q} onChange={(e) => { setQ(e.target.value); setPage(0) }} />
          {sources.length > 1 && (
            <select value={sourceFilter} onChange={(e) => { setSourceFilter(e.target.value === '' ? '' : Number(e.target.value)); setPage(0) }}>
              <option value="">All sources</option>
              {sources.map((s) => <option key={s.id} value={s.id}>{s.name}</option>)}
            </select>
          )}
        </div>

        {tracks.length === 0 ? (
          <p className="muted">Nothing here. Add a source and hit “Sync now”.</p>
        ) : (
          <>
            <div className="tablewrap">
              <table>
                <thead>
                  <tr>
                    <th className="sortable" onClick={() => toggleSort('artist')}>Artist{arrow('artist')}</th>
                    <th className="sortable" onClick={() => toggleSort('title')}>Track{arrow('title')}</th>
                    <th className="sortable" onClick={() => toggleSort('album')}>Album{arrow('album')}</th>
                    <th>Length</th>
                    <th className="sortable" onClick={() => toggleSort('state')}>State{arrow('state')}</th>
                    <th></th>
                  </tr>
                </thead>
                <tbody>
                  {tracks.map((t) => (
                    <Fragment key={t.id}>
                      <tr className="trow" onClick={() => openTrack(t)}>
                        <td>{t.artist ?? '—'}</td>
                        <td>{t.title ?? '—'}</td>
                        <td>{t.album ?? (t.enriched ? '—' : <span className="muted">…</span>)}</td>
                        <td>{fmtDur(t.durationSec)}</td>
                        <td><span className={`badge s-${t.state.toLowerCase()}`}>{t.state}</span></td>
                        <td onClick={(e) => e.stopPropagation()}>
                          {t.state === 'Mismatch' && (
                            <>
                              <button className="mini" title="Correct — mark Verified" onClick={() => trackAction(t.id, 'confirm')}>✓</button>
                              <button className="mini ghost" title="Wrong — blacklist" onClick={() => trackAction(t.id, 'reject')}>✕</button>
                            </>
                          )}
                          {(t.state === 'Failed' || t.state === 'Blacklisted') && (
                            <button className="mini" title="Retry" onClick={() => trackAction(t.id, 'retry')}>↻</button>
                          )}
                        </td>
                      </tr>
                      {openId === t.id && (
                        <tr className="detailrow">
                          <td colSpan={6}>
                            <div className="editrow">
                              <input value={edit.artist} onChange={(e) => setEdit((f) => ({ ...f, artist: e.target.value }))} placeholder="Artist" />
                              <input value={edit.title} onChange={(e) => setEdit((f) => ({ ...f, title: e.target.value }))} placeholder="Title" />
                              <input value={edit.album} onChange={(e) => setEdit((f) => ({ ...f, album: e.target.value }))} placeholder="Album" />
                              <button disabled={busy === `edit-${t.id}`} onClick={() => saveTrackEdit(t.id)}>Save tags</button>
                            </div>
                            <div className="muted small" style={{ marginTop: 6 }}>
                              {t.externalId && (
                                <a href={`https://music.youtube.com/watch?v=${t.externalId}`} target="_blank" rel="noreferrer">▶ open on YouTube</a>
                              )}
                              {t.filePath && <> · file: <code>{t.filePath}</code></>}
                              {detail?.failure_reason && <> · last error: {detail.failure_reason}</>}
                            </div>
                            {t.filePath && (
                              <audio className="player" controls preload="none" src={audioUrl(t.filePath)} />
                            )}
                            <div style={{ marginTop: 8 }}>
                              <button className="mini" disabled={busy === `cand-${t.id}`} onClick={() => loadCandidates(t.id)}>
                                {busy === `cand-${t.id}` ? '…' : 'Find in library'}
                              </button>
                            </div>
                            {cands.length > 0 && (
                              <ul className="cands">
                                {cands.map((cnd) => (
                                  <li key={cnd.path}>
                                    <div className="small">
                                      <b>{cnd.artist ?? '—'}</b> — {cnd.title ?? '—'}{' '}
                                      <span className="muted">({fmtDur(cnd.durationSec)} · score {cnd.score})</span>
                                    </div>
                                    <audio className="player" controls preload="none" src={audioUrl(cnd.path)} />
                                    <button className="mini" disabled={busy === `use-${t.id}`} onClick={() => useCandidate(t.id, cnd.path)}>
                                      Use this
                                    </button>
                                  </li>
                                ))}
                              </ul>
                            )}
                          </td>
                        </tr>
                      )}
                    </Fragment>
                  ))}
                </tbody>
              </table>
            </div>

            <div className="pager">
              <button className="ghost" disabled={page <= 0} onClick={() => { preserveScroll.current = window.scrollY; setPage((p) => p - 1) }}>← Prev</button>
              <span className="muted">{tracksTotal === 0 ? '0' : `${page * PAGE_SIZE + 1}–${Math.min((page + 1) * PAGE_SIZE, tracksTotal)} of ${tracksTotal}`}</span>
              <button className="ghost" disabled={page + 1 >= pages} onClick={() => { preserveScroll.current = window.scrollY; setPage((p) => p + 1) }}>Next →</button>
            </div>
          </>
        )}
      </div>

      <footer className="muted">Crate · playlist → download → verify → reconcile</footer>
    </div>
  )
}
