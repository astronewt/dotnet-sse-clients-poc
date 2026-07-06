import { useSse } from './useSse'
import './App.css'

export default function App() {
  const s = useSse()
  const clean = s.gaps === 0 && s.dupes === 0 && s.outOfOrder === 0
  const rows = Object.entries(s.sensors).sort(([a], [b]) => a.localeCompare(b))

  const fireBurst = async () => {
    const r = await fetch('/admin/burst?count=1000', { method: 'POST' })
    console.log('burst', await r.json())
  }

  return (
    <main>
      <h1>React SSE client</h1>
      <p>Native <code>EventSource</code> via a reusable hook. Renders only on change; verifies order, loss and duplication live.</p>

      <div className="bar">
        <span>Connection:</span>
        <span className={`pill ${s.status === 'connected' ? 'ok' : s.status === 'reconnecting' ? 'bad' : 'wait'}`}>
          {s.status}
        </span>
        <span className={`pill ${s.protocol === 'h2' ? 'ok' : 'wait'}`}>protocol: {s.protocol}</span>
        <button onClick={fireBurst}>Burst 1000</button>
      </div>

      <div className="grid">
        <Card n={s.received} label="events received" />
        <Card n={s.lastSeq} label="last seq" />
        <Card n={s.gaps} label="gaps (loss)" bad={s.gaps > 0} />
        <Card n={s.dupes} label="duplicates" bad={s.dupes > 0} />
        <Card n={s.outOfOrder} label="out of order" bad={s.outOfOrder > 0} />
        <Card n={s.lastLatency ?? '–'} label="latency ms (last)" />
      </div>

      <table>
        <thead>
          <tr><th>Sensor</th><th>Value</th><th>Seq</th><th>Latency ms</th><th>Updates</th></tr>
        </thead>
        <tbody>
          {rows.map(([id, r]) => (
            <tr key={id}>
              <td>{id}</td><td>{r.value}</td><td>{r.seq}</td><td>{r.latency}</td><td>{r.count}×</td>
            </tr>
          ))}
        </tbody>
      </table>

      {s.received > 0 && (
        <p className="verdict">
          {clean
            ? '✅ Contiguous stream: no loss, no duplicates, correct order.'
            : '❌ Integrity problem detected — see counters above.'}
        </p>
      )}
    </main>
  )
}

function Card({ n, label, bad }: { n: number | string; label: string; bad?: boolean }) {
  return (
    <div className="card">
      <div className={`n ${bad ? 'bad-text' : ''}`}>{n}</div>
      <div className="l">{label}</div>
    </div>
  )
}
