# Crw — SSE PoC (SCRUM-5)

Proof of concept: real-time server-to-front-end updates over Server-Sent Events.
See [PLAN.md](PLAN.md) for the plan and decision log, [RESULTS.md](RESULTS.md) for validation results.

## Layout

```
src/Crw.Server        ASP.NET Core Minimal API — SSE endpoint, simulated sensors, burst hook
  wwwroot/vanilla     Vanilla JS EventSource client (also the HTTP/2 test page)
clients/react         React + Vite EventSource client
```

## Run

### Server (with HTTPS for HTTP/2)

```bash
dotnet run --project src/Crw.Server --launch-profile https
```

- Landing page + vanilla client: https://localhost:7015/
- HTTP endpoint (for the React proxy): http://localhost:5020
- First time only: `dotnet dev-certs https --trust`

### React client

```bash
cd clients/react
npm install
npm run dev        # http://localhost:5173, proxies /sse and /admin to the server
```

## Endpoints

| Endpoint | Purpose |
|---|---|
| `GET /sse` | Event stream (`text/event-stream`). Honors `Last-Event-ID` / `?lastEventId=`. |
| `POST /admin/burst?count=1000` | Fire a burst of change events. |
| `GET /admin/stats` | `emitted` / `skipped` / `subscribers`. |

## Key design points

- **Change filtering** — the hub tracks the last-sent value per sensor and skips unchanged readings, so the FE only renders real changes.
- **Ordering / integrity** — every emitted event carries a global, contiguous `seq`; clients detect loss (gap), duplication (repeat) and reordering from it.
- **Resume** — bounded in-memory history replays missed events on reconnect via `Last-Event-ID`.
- **HTTP/2** — Kestrel negotiates `h2` over TLS; many SSE streams multiplex on one connection, bypassing the browser 6-per-domain limit.
