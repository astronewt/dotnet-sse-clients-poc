import { useEffect, useRef, useState } from 'react'

export interface Sensor {
  id: string
  value: number
  online: boolean
}

export interface Toast {
  id: number
  sensorId: string
}

export interface SseState {
  status: 'connecting' | 'connected' | 'reconnecting'
  protocol: string
  sensors: Sensor[]
  online: number
  offline: number
  received: number
  lastSeq: number
  gaps: number
  dupes: number
  outOfOrder: number
  lastLatency: number | null
  toasts: Toast[]
}

interface SensorMsg { seq: number; sensorId: string; value: number; online: boolean; ts: number }
interface SnapshotMsg { seq: number; sensors: { sensorId: string; value: number; online: boolean; ts: number }[] }

/**
 * Subscribes to the SSE stream. Handles the initial `snapshot` (hundreds of sensors at once)
 * plus live `sensor` deltas. Event handling mutates refs; React state is flushed on a timer
 * (~10 fps) so a high event rate never floods rendering. seq is global + contiguous, so
 * gaps = loss, repeat = duplicate, backwards = out of order.
 */
export function useSse(url = '/sse'): SseState {
  const [state, setState] = useState<SseState>(() => ({
    status: 'connecting', protocol: '?', sensors: [], online: 0, offline: 0,
    received: 0, lastSeq: 0, gaps: 0, dupes: 0, outOfOrder: 0, lastLatency: null, toasts: [],
  }))

  const sensors = useRef(new Map<string, Sensor>())
  const toasts = useRef<{ id: number; sensorId: string; expires: number }[]>([])
  const toastId = useRef(0)
  const meta = useRef({
    status: 'connecting' as SseState['status'], protocol: '?',
    received: 0, lastSeq: 0, gaps: 0, dupes: 0, outOfOrder: 0, lastLatency: null as number | null,
  })
  const dirty = useRef(true)

  useEffect(() => {
    const es = new EventSource(url)

    es.addEventListener('open', () => { meta.current.status = 'connected'; dirty.current = true })
    es.addEventListener('error', () => { meta.current.status = 'reconnecting'; dirty.current = true })

    es.addEventListener('snapshot', (e: MessageEvent) => {
      const d: SnapshotMsg = JSON.parse(e.data)
      meta.current.lastSeq = d.seq
      const m = new Map<string, Sensor>()
      for (const s of d.sensors) m.set(s.sensorId, { id: s.sensorId, value: s.value, online: s.online })
      sensors.current = m
      dirty.current = true
    })

    es.addEventListener('sensor', (e: MessageEvent) => {
      const d: SensorMsg = JSON.parse(e.data)
      const mc = meta.current
      if (mc.lastSeq !== 0) {
        if (d.seq === mc.lastSeq) mc.dupes++
        else if (d.seq < mc.lastSeq) mc.outOfOrder++
        else if (d.seq > mc.lastSeq + 1) mc.gaps += d.seq - mc.lastSeq - 1
      }
      mc.lastSeq = Math.max(mc.lastSeq, d.seq)
      mc.received++
      mc.lastLatency = Date.now() - d.ts
      const prev = sensors.current.get(d.sensorId)
      if (prev && prev.online && !d.online) {
        toasts.current.push({ id: ++toastId.current, sensorId: d.sensorId, expires: Date.now() + 4000 })
        if (toasts.current.length > 5) toasts.current.splice(0, toasts.current.length - 5)
      }
      sensors.current.set(d.sensorId, { id: d.sensorId, value: d.value, online: d.online })
      dirty.current = true
    })

    // Report the negotiated HTTP protocol for the SSE request.
    const protoTimer = setTimeout(() => {
      const entry = performance.getEntriesByType('resource')
        .find(r => r.name.includes('/sse')) as PerformanceResourceTiming | undefined
      meta.current.protocol = entry?.nextHopProtocol || '(unknown)'
      dirty.current = true
    }, 1500)

    // Flush refs -> state at ~10 fps.
    const flush = setInterval(() => {
      const now = Date.now()
      const before = toasts.current.length
      toasts.current = toasts.current.filter(t => t.expires > now)
      const toastsChanged = toasts.current.length !== before
      if (!dirty.current && !toastsChanged) return
      dirty.current = false
      const list = Array.from(sensors.current.values()).sort((a, b) => a.id.localeCompare(b.id))
      const online = list.reduce((n, s) => n + (s.online ? 1 : 0), 0)
      const toastList = toasts.current.map(t => ({ id: t.id, sensorId: t.sensorId }))
      setState({ ...meta.current, sensors: list, online, offline: list.length - online, toasts: toastList })
    }, 100)

    return () => { clearTimeout(protoTimer); clearInterval(flush); es.close() }
  }, [url])

  return state
}
