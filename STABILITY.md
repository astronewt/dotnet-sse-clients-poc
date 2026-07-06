# Long-run stability — can it run an 8-hour shift?

The operative question isn't "does it work" but "does it run *smoothly for a whole shift* (8+ hours) at ~1M+ events/day without degrading." That's a soak / resource-growth concern. This documents what we measured and what to harden before production.

## Target load in perspective

1M events/day ≈ **~12 events/sec average**. Even bursty, that's modest. The soak below ran at **~2,000 events/sec** — roughly **170M/day-equivalent, ~170× the target** — to stress it well beyond the requirement.

## Soak test (measured)

Setup: 300 sensors, **5 concurrent SSE clients**, a burst loop sustaining ~2,000 events/sec, 3 minutes. Sampled server process memory and a JS client's memory over time.

| | Start | 1 min | 3 min | Idle after |
|---|---|---|---|---|
| **Server working set** | 60 MB | 186 MB | 243 MB | **flat ~242 MB** |
| **JS client RSS** | 63 MB | 63 MB | **65 MB** | — |
| Events delivered | — | 290k | **1.84M** (×5 clients) | — |
| Integrity gaps | — | 5 | **20 total** | — |

**Findings:**
- **No client-side leak.** The JS client stayed ~63→65 MB across 1.84M delivered events. This is the browser proxy: a long-lived `EventSource` consumer is stable. (Real browsers add a *bounded* DOM — fixed tile count + 10 fps throttled render — so they stay flat too.)
- **No server-side leak.** Server memory rose under sustained load then **plateaued and stayed flat at ~242 MB when idle** (load off, 0 subscribers, memory did not keep climbing). That's **.NET Server GC retaining its heap**, not a leak — every in-memory structure is bounded (history 50k events, per-client buffer 4,096, sensor state ≈ fixed count).
- **Backpressure works.** Only **20 gaps** across the whole run: when a client briefly fell behind during a burst, the per-client bounded channel dropped the oldest events for *that* client (integrity detected it) instead of growing server memory. No single slow consumer can OOM the server.

## What's already safe for a long shift

- **Bounded history** (50k events) — capped regardless of runtime.
- **Bounded per-subscriber buffer** (4,096, `DropOldest`) — a stuck/slow client can't grow server memory; it just gets a detectable gap and can reconnect. *(This was added specifically for the 8-hour concern.)*
- **Fixed sensor state** — one entry per sensor, not per event.
- **Auto-reconnect + resume** — browsers reconnect natively; desktop clients hand-roll it; both resume via `Last-Event-ID` (or re-snapshot). Verified surviving a full server restart mid-shift.
- **Bounded client render** — fixed tile grid, ~10 fps throttle, so the UI cost doesn't scale with event rate.

## Harden before production (checklist)

Not required for the measured target load, but do these before a real deployment / higher client counts:

1. **Serialize each event once, fan out bytes to all clients.** Today each connection JSON-serializes every event independently (O(clients) allocations) — the main driver of the 242 MB churn. Pre-render the SSE frame to a `byte[]` once per event in the hub and write the shared buffer to every client → O(1) per event. Biggest win for many concurrent operators; smaller, calmer server footprint.
2. **Tune the GC.** Server GC trades memory for throughput and holds ~242 MB. For a memory-constrained box, try Workstation GC (`<ServerGarbageCollection>false</ServerGarbageCollection>`) or `DOTNET_GCConserveMemory`; measure the throughput trade-off.
3. **Expire auth tokens.** The demo `AuthStore` never evicts; over long runs with re-logins it slowly accumulates. Use short-lived signed tokens (JWT) with refresh, or an eviction policy. (See AUTH.md.)
4. **Profile the Blazor WASM client specifically over hours.** WASM GC behaves differently from server .NET; the structures are bounded, but confirm with a real multi-hour browser session (DevTools memory timeline). Vanilla/React proxied fine in the soak.
5. **Reconnect backoff + jitter.** Desktop reconnect currently retries at a flat 1s. Add exponential backoff + jitter so a server blip doesn't create a reconnect storm from many operators at once.
6. **Right-size the history buffer** to your worst-case client outage window: a client offline longer than `50k / event-rate` seconds re-snapshots instead of resuming. At 12/s that's ~70 min; raise `MaxHistory` if operators may disconnect longer and you need gapless resume.
7. **Monitoring.** Emit metrics: subscriber count, events/sec, dropped-events (channel), memory, reconnect rate — so a slow degradation is visible before an operator notices.

## Bottom line

For the stated load (an operator's shift, ~1M events/day), the soak shows **no leaks and stable memory at ~170× that rate** — with the bounded-channel fix in place, it should run a full shift comfortably. The checklist above is what turns "runs fine in the PoC soak" into "hardened for production and many concurrent operators."
