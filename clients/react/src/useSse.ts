import { useEffect, useRef, useState } from 'react'

export interface SensorEvent {
  seq: number
  sensorId: string
  value: number
  ts: number
}

export interface SensorRow {
  value: number
  seq: number
  latency: number
  count: number
}

export interface SseState {
  status: 'connecting' | 'connected' | 'reconnecting'
  protocol: string
  received: number
  lastSeq: number
  gaps: number
  dupes: number
  outOfOrder: number
  lastLatency: number | null
  sensors: Record<string, SensorRow>
}

const initial: SseState = {
  status: 'connecting',
  protocol: '?',
  received: 0,
  lastSeq: 0,
  gaps: 0,
  dupes: 0,
  outOfOrder: 0,
  lastLatency: null,
  sensors: {},
}

/**
 * Subscribes to the SSE stream and tracks integrity live.
 * seq is a global, contiguous id (every emitted event increments it by 1),
 * so gaps = loss, seq === lastSeq = duplicate, seq < lastSeq = out of order.
 */
export function useSse(url = '/sse'): SseState {
  const [state, setState] = useState<SseState>(initial)
  const esRef = useRef<EventSource | null>(null)

  useEffect(() => {
    const es = new EventSource(url)
    esRef.current = es

    es.addEventListener('open', () =>
      setState(s => ({ ...s, status: 'connected' })),
    )
    es.addEventListener('error', () =>
      setState(s => ({ ...s, status: 'reconnecting' })),
    )
    es.addEventListener('sensor', (e: MessageEvent) => {
      const d: SensorEvent = JSON.parse(e.data)
      setState(s => {
        let { gaps, dupes, outOfOrder } = s
        if (s.lastSeq !== 0) {
          if (d.seq === s.lastSeq) dupes++
          else if (d.seq < s.lastSeq) outOfOrder++
          else if (d.seq > s.lastSeq + 1) gaps += d.seq - s.lastSeq - 1
        }
        const latency = Date.now() - d.ts
        const prev = s.sensors[d.sensorId]
        return {
          ...s,
          received: s.received + 1,
          lastSeq: Math.max(s.lastSeq, d.seq),
          gaps,
          dupes,
          outOfOrder,
          lastLatency: latency,
          sensors: {
            ...s.sensors,
            [d.sensorId]: { value: d.value, seq: d.seq, latency, count: (prev?.count ?? 0) + 1 },
          },
        }
      })
    })

    // Report the negotiated HTTP protocol for the SSE request (h2 vs http/1.1).
    const t = setTimeout(() => {
      const entry = performance
        .getEntriesByType('resource')
        .find(r => r.name.includes('/sse')) as PerformanceResourceTiming | undefined
      setState(s => ({ ...s, protocol: entry?.nextHopProtocol || '(unknown)' }))
    }, 1500)

    return () => {
      clearTimeout(t)
      es.close()
    }
  }, [url])

  return state
}
