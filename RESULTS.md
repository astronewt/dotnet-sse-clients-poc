# SCRUM-5 PoC — Results

Verdict: **SSE is suitable** for propagating real-time updates from the central server to the front-end, under normal and burst load, and HTTP/2 removes the browser 6-connection-per-domain limit.

All checks below were run against the ASP.NET Core server with the simulated in-server sensor source.

## Scope checks (SCRUM-5)

| Requirement | Result |
|---|---|
| SSE endpoint on the central server | ✅ `GET /sse`, `text/event-stream` |
| SSE client in the front-end | ✅ React + Vanilla JS (Blazor optional) |
| Transmit sensor updates to the FE | ✅ simulated source → hub → stream |
| Events in correct order, displayed | ✅ global seq strictly increasing + contiguous |
| Render only on real change (D7) | ✅ hub skips unchanged; 30 skips observed while emitting 1025 |
| HTTP/2 (not capped at 6 connections) | ✅ ALPN negotiated `h2`; 10 concurrent streams on one connection |

## Load & concurrency (burst = 1000 events)

| Check | Result |
|---|---|
| All changes forwarded, no loss | ✅ 1000/1000 received |
| No duplication | ✅ 1000 unique seqs |
| Correct order | ✅ values 0..999 in order; seq contiguous |
| No race / deadlock / inconsistent state | ✅ single-lock hub; clean counters |
| FE responsive during stream | ✅ per-event flush; UI updates live |

## HTTP/2 proof

- `curl` in this environment lacks HTTP/2, so verified with Node's `http2` client.
- ALPN negotiated **h2** over TLS; SSE streamed with HTTP 200.
- **10 concurrent SSE streams multiplexed over one TCP/h2 connection**, all live (48 events each).
- Under HTTP/1.1 a browser would stall the 7th stream to the same origin. HTTP/2 removes that ceiling.

## Resume (Last-Event-ID)

- Reconnect with `Last-Event-ID` (or `?lastEventId=`) replays only missed events, then continues live.
- Verified: from seq 1064 → replayed 1065,1066,1067 → continued at 1068. No gap, no dup.

## How it was measured

- Live capture of `/sse` while firing `POST /admin/burst?count=1000`, analyzed for seq contiguity/duplicates/order (Node).
- `GET /admin/stats` for emitted vs skipped counts.
- Node `http2` client for ALPN + multiplexing.

## Notes / limits (PoC)

- Single-instance only. Multi-instance fan-out (Redis/backplane) is out of scope.
- No auth, no persistence. History buffer is in-memory and bounded (20k events).
- Latency shown is client-clock minus server-timestamp; meaningful on one machine, subject to clock skew across machines.
