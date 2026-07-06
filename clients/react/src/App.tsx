import { memo } from 'react'
import { useSse, type Sensor } from './useSse'
import './App.css'

export default function App() {
  const s = useSse()
  const fireBurst = () => fetch('/admin/burst?count=1000', { method: 'POST' })

  return (
    <main>
      <h1>React SSE client</h1>
      <p>Native <code>EventSource</code>. Hundreds of sensors, live online/offline status. Renders throttled to ~10&nbsp;fps.</p>

      <div className="bar">
        <span>Connection:</span>
        <span className={`pill ${s.status === 'connected' ? 'ok' : s.status === 'reconnecting' ? 'bad' : 'wait'}`}>{s.status}</span>
        <span className={`pill ${s.protocol === 'h2' ? 'ok' : 'wait'}`}>protocol: {s.protocol}</span>
        <button onClick={fireBurst}>Burst 1000</button>
      </div>

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

      <div className="grid">
        {s.sensors.map(sensor => <Tile key={sensor.id} sensor={sensor} />)}
      </div>

      <div className="toasts">
        {s.toasts.map(t => (
          <div className="toast" key={t.id}>🔴 <strong>{t.sensorId}</strong> went offline</div>
        ))}
      </div>
    </main>
  )
}

// Memoized so only tiles whose value/online actually changed re-render.
const Tile = memo(function Tile({ sensor }: { sensor: Sensor }) {
  return (
    <div className={`tile ${sensor.online ? '' : 'off'}`}>
      <div className="dot" />
      <div className="id">{sensor.id}</div>
      <div className="v">{sensor.value}</div>
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
