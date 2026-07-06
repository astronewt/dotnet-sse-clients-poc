import { memo, useEffect, useState } from 'react'
import { useSse, type Sensor } from './useSse'
import './App.css'

const post = (url: string) => fetch(url, { method: 'POST' })

export default function App() {
  const [authRequired, setAuthRequired] = useState(true)
  const [authed, setAuthed] = useState(false)
  const [pw, setPw] = useState('demo')
  const show = authed || !authRequired

  const s = useSse('/sse', show)
  const [selected, setSelected] = useState<string | null>(null)
  const [rename, setRename] = useState('')

  const sel = selected ? s.sensors.find(x => x.id === selected) : undefined
  // keep the rename box in sync when the selection changes
  useEffect(() => { setRename(sel?.name ?? '') }, [selected])

  // check auth on load
  useEffect(() => {
    fetch('/auth/me').then(r => r.json()).then(me => { setAuthRequired(me.authRequired); setAuthed(me.authenticated) })
  }, [])

  const login = async () => {
    // A1: same-origin login sets an HttpOnly cookie the EventSource then sends automatically.
    const r = await fetch('/auth/login?password=' + encodeURIComponent(pw), { method: 'POST' })
    if (r.ok) setAuthed(true)
  }
  const logout = async () => { await fetch('/auth/logout', { method: 'POST' }); setAuthed(false); setSelected(null) }

  const enc = encodeURIComponent

  return (
    <main>
      <h1>React SSE client</h1>
      <p>Native <code>EventSource</code>. Hundreds of sensors, live online/offline status. Renders throttled to ~10&nbsp;fps.</p>

      <div className="bar">
        <span>Connection:</span>
        <span className={`pill ${s.status === 'connected' ? 'ok' : s.status === 'reconnecting' ? 'bad' : 'wait'}`}>{s.status}</span>
        <span className={`pill ${s.protocol === 'h2' ? 'ok' : 'wait'}`}>protocol: {s.protocol}</span>
        <button onClick={() => post('/admin/burst?count=1000')}>Burst 1000</button>
        <span className="sep" />
        <button className="chaos" onClick={() => post('/admin/chaos/outage?seconds=10')}>Simulate outage (10s)</button>
        <button className="chaos" onClick={() => post('/admin/chaos/drop')}>Drop connections</button>
      </div>

      {authRequired && (
        <div className="bar">
          {authed ? (
            <>
              <span className="pill ok">🔒 signed in as <strong>operator</strong> (cookie)</span>
              <button onClick={logout}>Log out</button>
            </>
          ) : (
            <>
              🔒 <span>Stream is protected.</span>
              <input type="password" value={pw} onChange={e => setPw(e.target.value)}
                     style={{ padding: '.35rem .5rem', border: '1px solid #cbd5e1', borderRadius: 6, userSelect: 'text' }} />
              <button onClick={login}>Log in</button>
            </>
          )}
        </div>
      )}

      {!show ? (
        <p style={{ color: '#64748b' }}>Sign in to view the sensor stream (password: <code>demo</code>).</p>
      ) : (
      <>
      <div className="stats">
        <Stat n={s.sensors.length} label="sensors" />
        <Stat n={s.online} label="online" color="#166534" />
        <Stat n={s.offline} label="offline" color="#991b1b" />
        <Stat n={s.received} label="events" />
        <Stat n={s.lastLatency ?? '–'} label="latency ms" />
        <Stat n={s.gaps} label="gaps" bad={s.gaps > 0} />
        <Stat n={s.dupes} label="dupes" bad={s.dupes > 0} />
        <Stat n={s.outOfOrder} label="out of order" bad={s.outOfOrder > 0} />
      </div>

      {sel && (
        <div className="panel">
          <div className="panel-title">
            <strong>{sel.name || sel.id}</strong>
            <span className="muted">
              {sel.id} · value {sel.value} · {!sel.enabled ? 'disabled' : sel.online ? 'online' : 'offline'}
              {sel.acknowledged ? ' · acknowledged' : ''}
            </span>
          </div>
          <div className="panel-actions">
            <button onClick={() => post(`/sensors/${enc(sel.id)}/ack`)}>{sel.acknowledged ? 'Un-acknowledge' : 'Acknowledge'}</button>
            <button onClick={() => post(`/sensors/${enc(sel.id)}/toggle`)}>{sel.enabled ? 'Disable' : 'Enable'}</button>
            <input value={rename} onChange={e => setRename(e.target.value)} placeholder="new name" />
            <button onClick={() => post(`/sensors/${enc(sel.id)}/name?name=${enc(rename)}`)}>Rename</button>
            <button className="ghost" onClick={() => setSelected(null)}>Close</button>
          </div>
        </div>
      )}

      <div className="grid">
        {s.sensors.map(sensor => (
          <Tile key={sensor.id} sensor={sensor} selected={sensor.id === selected} onSelect={setSelected} />
        ))}
      </div>

      <div className="toasts">
        {s.toasts.map(t => (
          <div className="toast" key={t.id}>🔴 <strong>{t.sensorId}</strong> went offline</div>
        ))}
      </div>
      </>
      )}
    </main>
  )
}

// Memoized so only tiles whose state or selection actually changed re-render.
const Tile = memo(function Tile({ sensor, selected, onSelect }: { sensor: Sensor; selected: boolean; onSelect: (id: string) => void }) {
  const cls = `tile ${sensor.online ? '' : 'off'} ${sensor.enabled ? '' : 'disabled'} ${sensor.acknowledged ? 'ack' : ''} ${selected ? 'selected' : ''}`
  return (
    <div className={cls} onClick={() => onSelect(sensor.id)} title="Click to select">
      <div className="dot" />
      <div className="id">{sensor.name || sensor.id}</div>
      <div className="v">{sensor.value}</div>
      {sensor.acknowledged && <div className="ackbadge">✓</div>}
    </div>
  )
})

function Stat({ n, label, color, bad }: { n: number | string; label: string; color?: string; bad?: boolean }) {
  return (
    <div className="stat">
      <b style={{ color: bad ? '#991b1b' : color }}>{n}</b>
      <span className="l">{label}</span>
    </div>
  )
}
