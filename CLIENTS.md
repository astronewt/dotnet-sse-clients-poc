# Client comparison — Vanilla JS vs React vs Blazor

Three SSE clients were built against the same server, all consuming `GET /sse` (snapshot + live `sensor` events) and rendering the sensor grid with online/offline status. This compares them so the client choice for the real product is an informed one.

TL;DR:

| | **Vanilla JS** | **React (Vite)** | **Blazor WASM** |
|---|---|---|---|
| SSE mechanism | native `EventSource` | native `EventSource` | streaming `HttpClient` (C#) |
| Language | JavaScript | TypeScript/JSX | C# |
| Build toolchain | none | Node + Vite | .NET SDK |
| 3rd-party deps | **none** | React, ReactDOM, Vite | .NET runtime (as WASM) |
| Initial download | ~a few KB | ~60 KB gzip JS | **~2–10 MB** runtime |
| Startup time | instant | fast | **slowest** (WASM boot) |
| Custom auth header on stream | ❌ (EventSource limit) | ❌ (EventSource limit) | ✅ (HttpClient) |
| Auto-reconnect + Last-Event-ID | ✅ built in | ✅ built in | ⚠️ hand-rolled (we did) |
| Best for | kiosks, embeds, minimal pages | product SPAs, teams already on JS | all-.NET teams, internal tools |

---

## 1. Vanilla JS + `EventSource`

Files: [wwwroot/vanilla/index.html](src/Crw.Server/wwwroot/vanilla/index.html) — one HTML file, no build.

**Benefits**
- **Zero dependencies, zero build.** Just an HTML file the server serves. Nothing to install, patch, or audit.
- **Instant load**, tiny footprint — ideal for kiosks, status boards, embedded WebViews.
- `EventSource` gives **auto-reconnect and `Last-Event-ID` resume for free** — the browser handles it.
- Trivial to drop into any existing page.

**Pitfalls / problems**
- **Manual DOM.** We hand-write targeted tile updates. Fine for this grid; painful as UI complexity grows.
- **No type safety, no component model.** Larger apps get unwieldy fast.
- **No custom request headers** — `EventSource` can't send an `Authorization` header (see "Shared pitfall" below).
- No ecosystem for routing, forms, state — you build or import it all yourself.

**Use cases**
- Dashboards / wallboards, digital signage, a single live-status widget embedded in another app, quick demos.

**3rd-party tech needed:** none.

---

## 2. React + Vite + `EventSource`

Files: [clients/react/src/useSse.ts](clients/react/src/useSse.ts) (reusable hook), [App.tsx](clients/react/src/App.tsx).

**Benefits**
- **Component model + ecosystem** — routing, state, forms, component libraries, testing all mature and available.
- The SSE logic is a **reusable hook** (`useSse`) — clean separation, easy to reuse across views.
- **Type-safe** (TypeScript) end to end.
- Handles scale well with a bit of care: we throttle state flushes to ~10 fps and memoize tiles, so hundreds of sensors at a high event rate stay smooth.
- Huge hiring pool and community.

**Pitfalls / problems**
- **Build toolchain + `node_modules`** — Node, Vite, a dependency tree to maintain, patch, and secure.
- **Naive rendering doesn't scale** — updating React state on every event re-renders the whole grid. We had to add ref-accumulation + throttling + `React.memo`. Easy to get wrong.
- **No custom auth header on the stream** — same `EventSource` limitation.
- Bundle size and SPA hosting concerns (though small here: ~60 KB gzip).

**Use cases**
- The main product front-end, especially if the team is already on React/JS. Complex, interactive, multi-view apps.

**3rd-party tech needed:** React, ReactDOM, Vite, `@vitejs/plugin-react`, TypeScript, Node/npm to build. (Dev also uses a Vite proxy for `/sse`.)

---

## 3. Blazor WASM + streaming `HttpClient`

Files: [clients/blazor/Pages/Home.razor](clients/blazor/Pages/Home.razor) — SSE consumed in **pure C#**.

**Benefits**
- **One language across the stack.** Same C# and the *same DTOs/record shapes* as the server — no JS/TS context switch, no duplicated model definitions.
- **Full control over the request** — because it uses `HttpClient` (not `EventSource`), it **can set an `Authorization` header** and other custom headers. This is the big one for authenticated streams.
- Great for **.NET teams and internal line-of-business tools** — reuse existing C# skills, libraries, and validation.
- Strong typing and tooling from the .NET SDK.

**Pitfalls / problems**
- **Large initial download** — the .NET WASM runtime is multiple MB; **slowest cold start** of the three. Mitigable (AOT, trimming, compression) but never as light as vanilla.
- **You reimplement what `EventSource` gave for free** — reconnect loop, `Last-Event-ID` resume, and SSE line parsing are all hand-rolled (we did all three).
- **Rendering hundreds of elements is heavier in WASM** — we throttle re-renders to ~10 fps via a timer to keep it responsive.
- **A required, non-obvious gotcha:** WASM buffers the whole HTTP response by default. You *must* call `request.SetBrowserResponseStreamingEnabled(true)` + `HttpCompletionOption.ResponseHeadersRead`, or SSE never flows. (This line is WASM-specific — see note below.)
- Debugging WASM is less pleasant than JS.

**Use cases**
- Internal dashboards and admin tools for .NET shops; apps that must send auth headers on the stream; teams that want to share models/validation with the backend.

**3rd-party tech needed:** none beyond the .NET SDK and the Blazor WASM runtime (shipped by Microsoft). No npm. Requires the .NET SDK to build.

---

## Shared pitfall — `EventSource` can't authenticate the stream

The browser `EventSource` API (used by the vanilla and React clients) is **GET-only and cannot set request headers** — so no `Authorization: Bearer …` on the SSE connection. Common workarounds:
- token in a **cookie** (works, but CSRF/SameSite considerations),
- token in the **query string** (leaks into logs — avoid for secrets),
- a polyfill/library that reimplements SSE over `fetch` (adds a dependency).

The **Blazor `HttpClient` approach has no such limit** — it sets headers directly. If the real system needs authenticated streams and you stay browser-based, budget for one of the workarounds above; if you go native/desktop, it's a non-issue.

## HTTP/2 note

The browser 6-connections-per-domain limit only affects the browser clients (vanilla, React) — and HTTP/2 (via the edge) removes it by multiplexing. A native/desktop .NET client isn't subject to that browser limit at all.

## A fourth option — a Windows desktop client (not built yet)

Requirements may call for a native Windows app instead of (or alongside) the browser. There are several ways to do it; the key question is **how much UI code you want to share with the web client.**

### Option 4a — Blazor Hybrid (MAUI or WPF/WinForms `BlazorWebView`) — max reuse ⭐
Run the **same Razor components** as a native desktop app. The components render in an embedded WebView2, but — crucially — they execute on **native .NET in-process, not WASM**. So you get:
- **One UI codebase for browser and desktop.** Put the shared components (our `Home.razor`, the SSE logic, DTOs) in a **Razor Class Library (RCL)**, then reference it from both hosts: the Blazor WASM app (browser) and a MAUI/WPF Blazor Hybrid app (Windows).
- **No WASM download** — native startup is fast (unlike the WASM browser client).
- A **real native `HttpClient`**: streams by default, sets auth headers, gets HTTP/2 natively — so the `EventSource` auth limit and the WASM `SetBrowserResponseStreamingEnabled` gotcha both disappear.
- **MAUI Hybrid** extends the same approach to macOS/iOS/Android; **WPF/WinForms `BlazorWebView`** is Windows-only and lighter if you don't need cross-platform.

The one bit of glue: the SSE call needs slightly different `HttpClient` setup in WASM vs native, so abstract it behind an interface (`ISensorStream`) with two implementations — the shared component stays identical.

**Cost/caveats:** depends on the **WebView2 runtime** (evergreen on Windows 11); ships as an MSIX/installer; MAUI adds a workload to install.

### Option 4b — Pure native WPF/WinForms (no UI reuse)
A plain WPF/WinForms/console app consuming SSE with the same streaming `HttpClient` code as the Blazor client (minus the WASM-only line). **Simplest and lightest native path**, full native controls — but **no shared UI** with the web client; you rebuild the grid in XAML.

### Option 4c — Wrap the web app (Electron / Tauri)
Package the React (or vanilla) client as a desktop app. Reuses the **web** UI instead of the .NET one. Adds a non-.NET runtime (Chromium/Electron) or a Rust toolchain (Tauri). Reasonable if the web client is the primary product and desktop is secondary.

### Recommendation for the fourth client
If you want **browser + Windows from one UI codebase**, go **Blazor Hybrid with a shared Razor Class Library** (4a) — it directly answers "use Blazor so browser portability is easy." If desktop is the *only* target and reuse doesn't matter, **pure WPF** (4b) is simpler. Avoid 4c unless the web app is already the center of gravity.

Tracked as **D14 (open)** in [.claude/project-log.md](.claude/project-log.md).

---

## Recommendation

- **Public/product web UI, team on JS** → **React**.
- **Minimal embed, kiosk, or status board** → **Vanilla**.
- **Internal .NET tooling, shared models, or authenticated streams while staying in-browser** → **Blazor**.
- **Hard requirement to be a desktop app** → **native .NET client** (reuse the Blazor SSE code).
