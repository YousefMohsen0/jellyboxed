# Architecture

Jellyboxed is a Jellyfin server plugin (.NET 10, Jellyfin 12) plus a small browser script that the plugin serves and injects into the web client.

```
Browser (client.js)  ──HTTP──►  JellyboxedController  ──►  FilmService / SyncService
                                                              │            │
                                                   Stores (JSON on disk)   LetterboxdClient ─► RequestGate ─► letterboxd.com
                                                              │
                                                       JellyfinBridge ─► Jellyfin library & users
```

## Request flow

1. **Injection.** `ScriptInjectionMiddleware` (registered via `IStartupFilter`) rewrites `/web/index.html` in flight, adding `client.css` and `client.js` before `</body>`. Nothing on disk changes. It is idempotent (marker attribute) and strips conditional/range/encoding headers so it can edit the body.
2. **Browser script.** `client.js` watches navigation, finds the film on the page by Jellyfin item id, and calls the plugin API with the user's Jellyfin token. All DOM is built with a small `h()` helper (text nodes only, never `innerHTML`).
3. **API.** `JellyboxedController` maps routes to services. Anonymous: script, style, image proxy. User routes need `DefaultAuthorization`; admin routes need `RequiresElevation`. Errors never use HTTP 401/403 because the Jellyfin web client treats those as sign-out.
4. **Services.** `FilmService` answers per-film questions from cache (stale-while-revalidate). `SyncService` is a background worker with a queue (`Full`, `Quick`, `Resolve`). `PlaybackWatcher` turns playback-stop events into pending "log it?" prompts.
5. **Letterboxd.** `LetterboxdClient` performs requests (always through `RequestGate`); `LetterboxdParser` is the only place that knows Letterboxd's markup.

## Data and caching

All files live in the plugin data folder and are written atomically and debounced (`DebouncedJsonFile`); a corrupt file is renamed to `.bad` and rebuilt.

| File | Contents |
| --- | --- |
| `accounts.json` | Letterboxd accounts and sessions (owner-readable only, never sent to browsers) |
| `state-<userId>.json` | Per-user ratings, watchlist, sync times, prompt snoozes |
| `slugs.json` | Shared TMDB id → Letterboxd slug map (long positive TTL, short negative TTL) |

Matching Jellyfin films (TMDB id) to Letterboxd films (slug) is the expensive part, so it is done once per film, resolving whichever side is smaller, capped per run, and remembered. A quick sync (RSS + first watchlist page) keeps data fresh between full syncs.

## Being polite to Letterboxd

`RequestGate` limits concurrency and spaces requests. On HTTP 429/503 or a bot-check it pauses *all* requests (global back-off) and callers fail fast with a `Blocked` error instead of retrying.

## Sign-in tiers

Public (username only, read-only) → Session (password sign-in, password never stored) → pasted cookies (fallback for captchas). Rotated cookies are persisted back.

## Extending

- New Letterboxd data: add a pure parser function and test, then a client method.
- New feature: add a setting in `PluginConfiguration` and `configPage.html`, expose it via `/Jellyboxed/me`, and gate it server-side.
- Jellyfin API changes: should be confined to `JellyfinBridge.cs` and `Plugin*.cs`.
