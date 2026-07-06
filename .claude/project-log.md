# Crw — Project Log (decisions & testing)

Living log for the SCRUM-5 SSE proof of concept. Append as work continues.
Companion docs: [PLAN.md](../PLAN.md) (plan + decision log), [RESULTS.md](../RESULTS.md), [README.md](../README.md).

---

## Task

**SCRUM-5** (corewiser JIRA): PoC — server-to-front-end communication using Server-Sent Events (SSE).
Goal: prove SSE reliably delivers central-server updates to the FE, low latency, correct order, holding up under burst load; and that HTTP/2 avoids the browser 6-connections-per-domain limit.

> Note: the stored JIRA_API_TOKEN does NOT authenticate on corewiser.atlassian.net (401). Ticket content was pasted by the user.

---

## Final decisions

| # | Decision | Choice | Why |
|---|----------|--------|-----|
| D1 | Backend | ASP.NET Core Minimal API, .NET 10, Kestrel | Matches the user's .NET stack; native SSE via IAsyncEnumerable/Channels. |
| D2 | Data source | Simulated in-server BackgroundService | Self-contained; generates normal + burst traffic. |
| D3 | Front-end #1 | React 19 + Vite 8, native EventSource | Primary. Reusable `useSse` hook. |
| D4 | Front-end #2 | Vanilla JS + EventSource, single HTML | Fallback; zero deps; also the HTTP/2 test page. |
| D5 | Front-end #3 | Blazor WASM, SSE via streaming HttpClient (pure C#) | Optional; all-.NET path. |
| D6 | Transport | HTTP/2 over TLS | Beats the browser 6-connection-per-domain limit. |
| D7 | Change filtering | Hub tracks last-sent value per sensor; skips unchanged | FE renders only on real change. |
| D8 | Ordering | Global, contiguous monotonic `seq` per emitted event | Client detects loss (gap), dup (repeat), reorder. |
| D9 | Reconnect/resume | SSE `id:` + `Last-Event-ID`, bounded in-memory history (20k) | Resume after drop with no loss/dup. |
| D10 | Scaling | Single-instance for the PoC (no Redis/backplane) | Prove the mechanism first; multi-instance is a later step. |
| D11 | Containerization | Single nginx edge terminates TLS + HTTP/2, reverse-proxies SSE; server stays cleartext http behind it | One origin for all clients → makes the h2-multiplexing demo the real app; avoids dev-cert-in-container pain. |
| D12 | Scale + liveness | Hundreds of sensors (default 200, configurable via `Sensors:*`). Each has value + online/offline. Sensor is offline when silent > `OfflineAfterMs` (4s); a liveness sweep emits offline/online transitions. | Real deployments have hundreds of sensors that need at least an online/offline indicator. |
| D13 | Snapshot on connect | New clients get a full-state `snapshot` SSE event (all sensors) before live deltas; it carries the current seq, is NOT part of the seq stream, so integrity tracking stays contiguous. Reconnects still use Last-Event-ID backlog. | Hundreds of sensors must render immediately, not trickle in as each changes. |
| D14 (open) | Windows-app client | Possible native Windows client instead of / alongside the browser. Options researched (see [CLIENTS.md](../CLIENTS.md)): **4a Blazor Hybrid** (MAUI or WPF `BlazorWebView`) — recommended for max reuse; **4b** pure WPF/WinForms; **4c** Electron/Tauri wrapping the web app. Not built yet. | Requirement raised 2026-07-06; pending confirmation of desktop vs browser and reuse needs. |

| D15 | Desktop client + code reuse | Built a **native WPF client** via `BlazorWebView`, and extracted the dashboard into a shared **Razor Class Library** (`clients/shared/Crw.ClientShared`) consumed by BOTH the browser WASM app and the WPF app. SSE consumption is one shared `SseSensorClient`; only the request-config differs (WASM enables browser streaming, desktop is native). Chose WPF over MAUI to avoid the MAUI workload install; pattern is identical if MAUI/cross-platform is needed later. | Proves the "one UI codebase for browser + Windows" story (answers the D14 question). |
| D16 | Offline toast | All clients show a self-dismissing toast when a sensor transitions online→offline (max 5, ~4s). Implemented once in the shared Blazor component (covers WASM + WPF) and once each in React and vanilla. | Requested UX for at-a-glance offline awareness. |
| D17 | Robust resume ✅ | On reconnect the server sends a **backlog only if every event after `Last-Event-ID` is still buffered** (`lastId <= seq && lastId >= oldestHistorySeq-1`). Otherwise (new client, aged-out resume point, or server restart where seq reset) it sends a **fresh snapshot**. Confirmed the right call by the user. | Closes the earlier known limitation: a long-disconnected client no longer resumes into a silent gap. |
| D18 | Chaos / resilience simulation | Backend can simulate **signal loss** (`POST /admin/chaos/drop` — force-drop all connections) and **downtime** (`POST /admin/chaos/outage?seconds=N` — `/sse` returns 503 + Retry-After, then recovers). Opt-in **random auto-chaos** via `Chaos:Enabled` (env `Chaos__Enabled=true`), interval `Chaos:MinIntervalSec`/`MaxIntervalSec`. Default OFF. | User asked the backend to randomly trigger signal loss / restart / downtime to exercise client reconnect/resume. |
| D19 | Docker scope | `docker compose up -d --build` runs the server + all **three web clients** via the single nginx edge (one origin). **WPF is intentionally excluded** — it's a native Windows desktop GUI, not a web app. Edge Dockerfile fixed to include the shared RCL in the Blazor build. | User wanted every client to start from one command; clarified web vs native boundary. |

## Project structure (after D15)

```
src/Crw.Server          server (unchanged)
clients/shared          Crw.ClientShared — RCL: Dtos, SseSensorClient, SensorDashboard.razor (+scoped css)
clients/blazor          Crw.BlazorClient — WASM host; Home.razor renders <SensorDashboard/>; registers SseSensorClient w/ SetBrowserResponseStreamingEnabled
clients/wpf             Crw.WpfClient — WPF + BlazorWebView; Main.razor renders <SensorDashboard/>; native HttpClient (BaseAddress http://localhost:5020)
clients/react           React (own implementation; not shared with .NET)
Crw.slnx                solution (note: .slnx XML format, not .sln)
```

Reuse note: WASM + WPF share the *same* `SensorDashboard` component and SSE client via the RCL. React/vanilla are separate JS implementations by nature. All four build clean; WPF runtime needs a desktop session + WebView2 (evergreen on Win11) + the server running — not verified headlessly this session.

## Research notes

### Client comparison (2026-07-06)
Full write-up in [CLIENTS.md](../CLIENTS.md). Key findings:
- **`EventSource` (vanilla + React) cannot set request headers** → no `Authorization` header on the stream. Workarounds: cookie auth, token in query (leaks), or a fetch-based SSE polyfill. **Blazor's `HttpClient` has no such limit.** This is a decisive factor if authenticated streams are required while staying in-browser.
- WASM Blazor **requires** `request.SetBrowserResponseStreamingEnabled(true)` + `ResponseHeadersRead` or SSE never flows; this line is WASM-only (native/desktop `HttpClient` streams by default).
- Rendering hundreds of tiles: React and Blazor need render throttling (~10 fps) to stay smooth under high event rates; vanilla uses targeted DOM updates.

### Chaos + robust resume validation (2026-07-06)
Validated against local server:
- **Signal loss:** `POST /admin/chaos/drop` closed 2 live streams (`{dropped:2}`); server-side connection ended → clients would auto-reconnect.
- **Downtime:** `POST /admin/chaos/outage?seconds=4` → `GET /sse` returned **503, Retry-After: 4** during the window; after recovery `/sse` served a `snapshot`.
- **Robust resume (D17):** `GET /sse?lastEventId=999999999` (client ahead of server, i.e. simulated restart) returned a fresh **`event: snapshot`** rather than an empty/gappy backlog.

### SSE connection longevity & resilience (2026-07-06)
- **No inherent time limit** in the SSE spec — connections are meant to stay open indefinitely. Real limits come from intermediaries (proxy/LB idle timeouts, Kestrel min data rate).
- **Our mitigations:** server sends `: keepalive` comments every 15s (traffic never idles → proxies don't reap it); nginx edge uses `proxy_buffering off` + `proxy_read_timeout 3600s`; `X-Accel-Buffering: no`. Cloud LBs (Azure App GW ~4min, AWS ALB) idle-timeout well above 15s, so keepalives keep it alive.
- **Auto-reconnect differs by client:**
  - Browser `EventSource` (vanilla, React): **automatic** reconnect + `Last-Event-ID` resend, no code. Server can tune the delay via a `retry:` field.
  - Native `HttpClient` (Blazor, WPF): **no free reconnect** — we hand-rolled the loop in `SseSensorClient` (retry 1s, resends Last-Event-ID). More control (backoff/jitter/auth refresh) but explicit.
  - Both resume against the server's history buffer with no loss (validated: resume from 1064 → 1065.. contiguous).
- **~~Known limitation~~ → RESOLVED (D17):** history buffer is bounded (50k events). If a client's resume point ages out (or the server restarts), reconnect now sends a **fresh snapshot** instead of a gappy backlog. Implemented + validated 2026-07-06.
- **Which client to worry about:** browsers are the most battle-tested for reconnect; native gives more control. Neither is fundamentally less resilient here. Browsers still need HTTP/2 for many concurrent streams (6-per-domain); native isn't subject to that.

### Fourth client / Blazor for desktop (2026-07-06)
- **Blazor Hybrid** runs the *same* Razor components as native in-process .NET inside a **WebView2** (NOT WASM) → fast startup, native `HttpClient` (auth headers + HTTP/2 + streaming by default).
- **Max-reuse structure:** shared **Razor Class Library (RCL)** holding the components + SSE logic + DTOs, referenced by both the Blazor WASM host (browser) and a MAUI/WPF Blazor Hybrid host (Windows). Abstract the SSE call behind an interface (`ISensorStream`) since WASM vs native `HttpClient` setup differs slightly; components stay identical.
- Dependencies/caveats: WebView2 runtime (evergreen on Win11); MSIX/installer packaging; MAUI needs its workload installed. MAUI Hybrid also reaches macOS/iOS/Android; WPF `BlazorWebView` is lighter but Windows-only.
- **Leaning decision (unconfirmed):** if browser + Windows from one UI codebase is wanted → Blazor Hybrid + shared RCL. If desktop-only and no reuse → pure WPF.

---

## What's built

```
src/Crw.Server            ASP.NET Core Minimal API
  SensorHub.cs            change tracker + fan-out (Channels), history, seq
  SensorSimulator.cs      BackgroundService, 5 sensors @500ms, ~30% no-op ticks
  Program.cs              /sse, /admin/burst, /admin/stats; Kestrel Http1AndHttp2; dev CORS
  wwwroot/index.html      landing
  wwwroot/vanilla/        vanilla EventSource client + HTTP/2 protocol pill
clients/react             React 19 + Vite 8; useSse.ts hook; dev proxy /sse,/admin -> :5020
clients/blazor            Blazor WASM; Pages/Home.razor streams /sse in C#
docker/server.Dockerfile  publish server (http:8080)
docker/edge.Dockerfile    build React+Blazor static, nginx + openssl self-signed cert
docker/nginx.conf         routes: /react/ /blazor/ static; /sse (buffering off) /admin / -> server
docker-compose.yml        server + edge; edge 8443:443, server 5020:8080
```

### Key endpoints
- `GET /sse` — text/event-stream; honors `Last-Event-ID` and `?lastEventId=`.
- `POST /admin/burst?count=1000` — burst of strictly-increasing values (all real changes).
- `GET /admin/stats` — emitted / skipped / subscribers.

---

## Tests run & results (2026-07-06, local, http endpoint)

All PASSED.

- **Order + integrity**: captured live /sse during a burst → global seq strictly increasing AND fully contiguous. No loss, no dupes. (analyzed with Node)
- **Burst 1000**: 1000/1000 received, values 0..999 in order, 1000 unique seqs.
- **Change filtering (D7)**: 30 skips observed while emitting 1025 events (unchanged readings dropped).
- **Resume (D9)**: request `?lastEventId=1064` → replayed 1065,1066,1067 → continued live at 1068. No gap/dup.
- **HTTP/2 (D6)**: over TLS (https profile), Node http2 client → ALPN negotiated `h2`, SSE streamed, HTTP 200.
- **HTTP/2 multiplexing**: **10 concurrent SSE streams over ONE h2 connection**, all live (48 events each). Proves the 6-per-domain limit is bypassed.
- **Builds**: server, React (`npm run build`), Blazor (`dotnet build`) all clean. React dev proxy streams end-to-end. Blazor hosts + loads WASM.

> Not yet done: rendering React/Blazor UIs in a real browser (no browser-driving tool in-session). Data paths verified at HTTP level.

---

## Gotchas learned

- `dotnet run` applies the launchSettings profile and overrides `ASPNETCORE_URLS`. Use `--launch-profile https` to bind HTTPS (needed for h2).
- Kestrel warns "HTTP/2 not enabled" on cleartext http endpoints — expected; h2 needs TLS/ALPN.
- The bundled `curl` (Schannel build) lacks HTTP/2 — verify h2 with Node's `http2` module instead.
- Python is absent on this machine (and user prefers no Python) — use Node for analysis scripts.
- Blazor WASM must call `request.SetBrowserResponseStreamingEnabled(true)` + `HttpCompletionOption.ResponseHeadersRead`, else the response buffers and SSE never flows.
- Blazor base href set to `/blazor/` (sed in edge Dockerfile) for subpath serving; React uses `base: './'`.

---

## Session 2 (2026-07-06) — scale to hundreds of sensors + online/offline

- Reworked `SensorHub` to per-sensor state (value, online, lastSeen); emits on value OR status change; liveness `Sweep()` flips silent sensors offline. Snapshot-on-connect added.
- `SensorSimulator` scaled to 200 sensors (configurable). Asymmetric sleep (`SleepChance` 0.01 / `WakeChance` 0.20) keeps offline a realistic minority (~5/200 observed).
- All three clients reworked to a **sensor grid** with green/red online-offline dots + online/offline/total counts. React and Blazor throttle rendering to ~10 fps (refs/accumulate → flush) so high event rates don't flood the UI; vanilla does targeted per-tile DOM updates.
- Config knobs (appsettings / env): `Sensors:Count`, `Sensors:TickMs`, `Sensors:OfflineAfterMs`, `Sensors:ChangeChance`, `Sensors:SleepChance`, `Sensors:WakeChance`.

**Validated (local http):**
- 200 sensors; snapshot on connect carries all 200 + current seq.
- Live sensor events contiguous from `snapshot.seq + 1` — no gap/dup across the snapshot→live seam.
- Offline/online transitions observed (23 offline + 250 online in a 9s window pre-tuning).
- After sleep-tuning: steady state ~195 online / ~5 offline.
- Server, React, Blazor all build clean.

## Status / next steps

- [ ] Docker stack: `docker compose build` initiated earlier; images not yet verified. Rebuild needed (server + clients changed). Then `docker compose up`, re-verify SSE + h2 + sensor grid through the edge (https://localhost:8443).
- [ ] Browser smoke test of all three dashboards with the sensor grid.
- [ ] **D14**: confirm whether a native Windows client is needed; if so, build a WPF/console SSE client (HttpClient streaming).
- [ ] Decide if/when to add multi-instance fan-out (D10 revisit).
- [ ] `git init` + first commit (user asked earlier; pending).
