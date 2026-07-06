# SCRUM-5 PoC — Server-to-Front-End Communication Using SSE

## Objective
Prove that Server-Sent Events reliably deliver central-server updates to the front-end. Low latency. Correct order. Holds up under burst load.

## Final choices (decision log)

| # | Decision | Choice | Why |
|---|----------|--------|-----|
| D1 | Backend | ASP.NET Core Minimal API (.NET 9, Kestrel) | Matches your existing .NET stack. Native SSE via `IAsyncEnumerable`. |
| D2 | Data source | Simulated in-server background service | Self-contained. No external deps. Generates normal + burst traffic. |
| D3 | Front-end #1 | React (Vite) + native `EventSource` | Primary. Closest to a real product FE. |
| D4 | Front-end #2 | Vanilla JS + `EventSource`, single HTML page | Fallback. Zero deps. Baseline sanity check. |
| D5 | Front-end #3 | Blazor (WASM) | Optional. Only if time allows. Proves all-.NET path. |
| D6 | Transport | HTTP/2 over TLS (HTTPS) | Required to beat the browser 6-connection-per-domain limit. |
| D7 | Change filtering | Server tracks last-sent version per field; skips unchanged | Reduces FE load. FE renders only on real change. |
| D8 | Event ordering | Monotonic sequence id on every event | Lets FE detect gaps, dupes, out-of-order. |
| D9 | Reconnect | SSE `id:` + `Last-Event-ID` header | Client resumes without loss after drop. |

## Architecture (one line)
`Simulated sensor service → in-memory change tracker → SSE endpoint (/sse) → EventSource client (React/Vanilla/Blazor)`

## Build steps

### 1. Backend — SSE endpoint
- Minimal API project. Enable HTTP/2 + HTTPS in Kestrel.
- `GET /sse`: sets `Content-Type: text/event-stream`, streams via `IAsyncEnumerable`.
- Each event: `id: <seq>`, `event: sensor`, `data: <json>`.
- Honor `Last-Event-ID` on reconnect.

### 2. Backend — simulated data + change tracking
- Hosted `BackgroundService` produces sensor readings.
- Change tracker holds last-sent value per sensor. Emit only on change (D7).
- Test hook to fire a burst of 1000 events fast (D2).

### 3. Front-end #1 — React
- Vite app. `EventSource('/sse')`.
- Render only on changed values. Show seq id, latency, connection state.

### 4. Front-end #2 — Vanilla JS
- One HTML file. Same `EventSource` logic. No build.

### 5. Front-end #3 — Blazor (optional)
- WASM app. Consume SSE. Only if 1–4 done.

## Validation

### Functional
- Events arrive in order. UI shows the latest value. (Scope bullets 1–4)
- Unchanged values produce no event / no render. (D7)

### Load & concurrency (burst = 1000 events, short interval)
- No loss. No duplication. (check seq id continuity)
- Correct order. (seq monotonic on FE)
- No race / deadlock / inconsistent state.
- FE stays responsive during the stream.

### HTTP/2
- Confirm the connection negotiates h2 (DevTools → Protocol column).
- Open >6 tabs on same domain. All keep a live stream. (D6)

## Done when
- One FE (React) shows live, ordered, low-latency updates.
- Burst test passes: no loss, no dupes, ordered, responsive.
- HTTP/2 confirmed and beats the 6-connection limit.
- Verdict recorded: is SSE suitable for real-time central→FE propagation.

## Out of scope
- Real device driver. Auth. Persistence.

## D10 — Scaling
- Choice: single-instance for the PoC. No Redis/backplane fan-out yet.
- Why: proves the SSE mechanism first. Multi-instance is a later step if the PoC lands.
