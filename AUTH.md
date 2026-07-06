# Authenticating the SSE stream — browser vs desktop

Authentication is the single biggest factor that splits the client choices, because **how you attach credentials to a long-lived SSE connection depends entirely on the client technology**. This document analyses both deployment types the product may take — **browser-based** and **desktop-based** — since the answer is materially different for each.

The crux, stated once:

> The browser **`EventSource`** API (vanilla JS, React) can only issue a plain `GET` and **cannot set request headers** — so it cannot send `Authorization: Bearer …` on the stream. Anything built on **`HttpClient`** (Blazor, WPF, any native .NET) sets headers freely, so bearer/OAuth "just works" on the stream.

There's also an asymmetry inside a single browser app worth calling out up front:
- The **action POSTs** in this PoC (`/sensors/{id}/ack`, `/toggle`, `/name`, the chaos hooks) use `fetch`, which **can** send an `Authorization` header.
- The **stream** (`/sse`) uses `EventSource`, which **cannot**.

So a naive "bearer token in a header" design authenticates the actions but silently leaves the **stream** unauthenticated. Pick a scheme that covers both.

---

## Deployment A — Browser-based (vanilla JS / React)

`EventSource` is the constraint. Four viable schemes, best first:

### A1. Cookie-based session auth  ⭐ (recommended for browsers)
The browser automatically attaches cookies to the `EventSource` `GET`, so the stream is authenticated with **zero client code** and no header needed.

- **How:** issue an `HttpOnly`, `Secure`, `SameSite=Lax` (or `Strict`) session cookie at login. `EventSource('/sse')` sends it automatically (same-origin). Cross-origin needs `new EventSource(url, { withCredentials: true })` **and** server CORS `Access-Control-Allow-Credentials: true` with an explicit origin (not `*`).
- **Pros:** works natively with `EventSource` *and* the `fetch` action POSTs; `HttpOnly` keeps the token out of reach of XSS; battle-tested.
- **Cons / must-dos:**
  - **CSRF**: the action POSTs (ack/toggle/rename) are state-changing and cookie-borne, so they're CSRF-exposed. Mitigate with `SameSite=Lax/Strict` **and** a CSRF token (double-submit or header) on those POSTs. The `GET /sse` is safe from CSRF (no state change) but still verify `Origin`/`Referer` if paranoid.
  - Cross-origin cookies increasingly blocked by browsers (third-party cookie phase-out) — prefer **same-origin** (serve app + API from one origin, as our nginx edge does).

### A2. `fetch`-based SSE polyfill (bearer on the stream)
Libraries like `@microsoft/fetch-event-source` re-implement SSE over `fetch`, so you **can** set `Authorization: Bearer …` on the stream.

- **Pros:** proper OAuth/bearer on the stream; fits token-based SPAs; you also control reconnect/backoff explicitly.
- **Cons:** adds a dependency; you **lose native `EventSource` auto-reconnect** (the library supplies its own — verify it re-sends `Last-Event-ID`); slightly more code. This is the go-to when the org is committed to bearer tokens and can't use cookies.

### A3. Token in the query string  ⚠️ (last resort)
`new EventSource('/sse?access_token=…')`.

- **Pros:** trivial; works with plain `EventSource`.
- **Cons:** the token **leaks** into server access logs, proxy logs, browser history, and `Referer` headers. Only acceptable with **short-lived, single-purpose stream tokens** (mint a 60-second token from an authenticated endpoint, use it once to open the stream). Never put a long-lived JWT or refresh token here.

### A4. BFF (backend-for-frontend)
An auth-aware server component holds the real tokens and exposes a same-origin, cookie-secured `/sse` to the browser. Combines A1's browser ergonomics with upstream OAuth. Heavier infra; the cleanest enterprise answer for token-based systems that must stay in-browser.

**Token expiry on a long stream (browsers):** SSE connections live for hours. With cookies (A1), rotate/refresh the session cookie server-side. With A2/A3, when the token expires the server should close the stream (401) → the client reconnects, fetching a fresh token first. Design the reconnect path to re-acquire credentials.

---

## Deployment B — Desktop-based (WPF / native .NET; Blazor Hybrid)

Here the stream is consumed with **`HttpClient`**, which removes every limitation above.

- **Bearer/OAuth on the stream:** set it directly.
  ```csharp
  request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
  ```
  In this codebase that's the **exact purpose of the `SseRequestConfigurator` hook** in `SseSensorClient` — the WASM host uses it to enable response streaming; a desktop host uses it to attach the token. No new plumbing needed.
- **Transparent refresh:** attach a `DelegatingHandler` that adds the current access token and, on `401`, refreshes and retries. Because our client already **hand-rolls the reconnect loop**, a 401 → refresh → reconnect flow is natural.
- **Richer options unavailable to browsers:** client certificates / **mTLS**, Windows Integrated Auth (Negotiate/Kerberos) for intranet deployments, OS credential stores (DPAPI/Credential Manager) for token storage.
- **No CORS, no cookies, no CSRF** — a native app isn't a browser origin, so that whole class of concerns disappears.

**Blazor nuance (important):**
- **Blazor WASM** runs in the browser but consumes SSE via `HttpClient` (over browser `fetch`), so — unlike vanilla/React — it **can** send an `Authorization` header on the stream. It's still subject to CORS and browser storage limits (store tokens in memory, not `localStorage`).
- **Blazor Hybrid** (WPF/MAUI `BlazorWebView`) executes on **native .NET**, so it gets the full desktop story (mTLS, Windows auth, native token stores) while reusing the same UI component.

---

## Recommendation by scenario

| Scenario | Recommended scheme |
|---|---|
| Browser SPA, same-origin, session-based | **Cookie auth (A1)** — `HttpOnly`+`SameSite`, CSRF token on the action POSTs |
| Browser SPA, OAuth/bearer, can't use cookies | **fetch-based SSE polyfill (A2)** or **BFF (A4)** |
| Browser SPA, minimal effort, low sensitivity | Short-lived **stream token in query (A3)** — never a long-lived token |
| Desktop / native (WPF, service, CLI) | **`HttpClient` + bearer/OAuth**, refresh via `DelegatingHandler`; consider mTLS / Windows auth |
| Both browser + desktop from one codebase | Shared UI (RCL), but **abstract the auth wiring** per host — `EventSource`+cookie in the browser build, `HttpClient`+bearer in the desktop build (mirrors our `SseRequestConfigurator` split) |

## How it maps to this codebase

- **Desktop/Blazor path is ready:** `SseSensorClient(HttpClient, SseRequestConfigurator?)` — pass a configurator that sets `request.Headers.Authorization`. One line per host.
- **Browser path (vanilla/React)** would move from `EventSource('/sse')` to either cookie auth (no code change beyond login + `withCredentials` if cross-origin) or a fetch-based SSE reader if bearer-on-stream is mandated.
- **Action POSTs** already go through `fetch`/`HttpClient`, so they can carry a header today — but remember the stream needs its own scheme (the asymmetry above).

## Non-negotiables (both deployments)

- **TLS everywhere** — SSE auth over plaintext is meaningless; tokens/cookies must ride HTTPS (HTTP/2 in our edge already enforces TLS).
- **Long-lived connection ⇒ plan for token expiry** — close on expiry, reconnect with fresh credentials; never assume the token that opened the stream is still valid an hour later.
- **Least-privilege stream tokens** — the SSE token should grant read-on-this-stream only, not full API access, especially for A3.
