# SSE PoC (SCRUM-5)

Proof of concept: real-time server-to-front-end updates over Server-Sent Events, with hundreds of sensors and live online/offline status.

Docs: [PLAN.md](PLAN.md) (plan + decision log) · [RESULTS.md](RESULTS.md) (validation) · [CLIENTS.md](CLIENTS.md) (client comparison) · [AUTH.md](AUTH.md) (authenticating the stream — browser vs desktop) · [.claude/project-log.md](.claude/project-log.md) (running log).

## Layout

```
src/Sse.Server        ASP.NET Core Minimal API — SSE endpoint, simulated sensors, chaos hooks
  wwwroot/vanilla     Vanilla JS EventSource client
clients/shared        Sse.ClientShared — RCL: DTOs, SseSensorClient, SensorDashboard (shared by WASM + WPF)
clients/blazor        Blazor WASM host (browser)
clients/wpf           WPF + BlazorWebView host (native Windows) — reuses clients/shared
clients/react         React + Vite client
```

## Run everything in Docker (one command)

```bash
docker compose up -d --build
```

Then open the clients (self-signed cert → accept the browser warning once):

| Client | URL |
|---|---|
| 🏠 Landing | [https://localhost:8443/](https://localhost:8443/) |
| 🟨 Vanilla JS | [https://localhost:8443/vanilla/](https://localhost:8443/vanilla/) |
| ⚛️ React | [https://localhost:8443/react/](https://localhost:8443/react/) |
| 🟣 Blazor WASM | [https://localhost:8443/blazor/](https://localhost:8443/blazor/) |
| 🔧 Server API (direct) | [http://localhost:5020/admin/stats](http://localhost:5020/admin/stats) |

The **WPF desktop client** is native Windows (not containerized): `dotnet run --project clients/wpf` (needs the server running).

Stop the stack: `docker compose down`.

## Run without Docker (dev)

```bash
# Server (HTTPS enables HTTP/2)
dotnet run --project src/Sse.Server --launch-profile https
```

| Client | URL / command |
|---|---|
| Landing + Vanilla | [https://localhost:7015/](https://localhost:7015/) · [https://localhost:7015/vanilla/](https://localhost:7015/vanilla/) |
| React | `cd clients/react && npm install && npm run dev` → [http://localhost:5173/](http://localhost:5173/) |
| Blazor WASM | `dotnet run --project clients/blazor` |
| WPF desktop | `dotnet run --project clients/wpf` |

First time for HTTPS: `dotnet dev-certs https --trust`.

## Endpoints

| Endpoint | Purpose |
|---|---|
| `GET /sse` | Event stream (`text/event-stream`). Emits a `snapshot` then live `sensor` events. Honors `Last-Event-ID` / `?lastEventId=`. |
| `GET /admin/stats` | `emitted` / `skipped` / `subscribers` / `total` / `online` / `offline`. |
| `POST /sensors/{id}/ack` | **Client action** — toggle acknowledged; broadcast to all clients. |
| `POST /sensors/{id}/toggle` | **Client action** — enable/disable a sensor (disabled stops reporting). |
| `POST /sensors/{id}/name?name=…` | **Client action** — set a friendly name; broadcast to all clients. |
| `POST /admin/burst?count=1000` | Fire a burst of change events (load test). |
| `POST /admin/chaos/drop` | **Signal loss** — drop all live connections; clients reconnect + resume. |
| `POST /admin/chaos/outage?seconds=10` | **Downtime** — `/sse` returns 503 for the window, then recovers. |

Config (env vars):
- `Chaos__Enabled=true` — server randomly drops connections / triggers outages on its own.
- `Latency__Enabled=true`, `Latency__StreamJitterMaxMs=40` — simulate a real network: base delay + occasional spikes on requests, plus bounded per-batch stream jitter. **On by default in Docker**, off in local dev.
- `Sensors__Count=200`, `Sensors__OfflineAfterMs=4000`, … — tune the simulated sensor fleet.

## Key design points

- **Hundreds of sensors + liveness** — a sensor goes offline when it stops reporting; clients get a full **snapshot** on connect, then live deltas + online/offline transitions. All clients show a toast when a sensor drops.
- **Bidirectional** — click any tile to **acknowledge** it. The action `POST`s to the server, which flips the state and broadcasts it, so every connected client (browser or desktop) converges instantly. Acked sensors show an amber ✓ and suppress the offline toast.
- **Change filtering** — the hub skips unchanged readings, so clients render only real changes.
- **Ordering / integrity** — every event carries a global, contiguous `seq`; clients detect loss (gap), duplication (repeat), reordering.
- **Resilient resume** — reconnects replay missed events via `Last-Event-ID`; if the resume point has aged out (or the server restarted), the client is re-seeded with a fresh snapshot instead of a gappy backlog.
- **HTTP/2** — the nginx edge negotiates `h2` over TLS; many SSE streams multiplex on one connection, bypassing the browser 6-per-domain limit.
