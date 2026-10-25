# Changelog

All notable changes are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

## [1.1.7] - 2026-10-25

### Fixed
- **Poster badges (and film matching) found no movies in the library.** The library list came back without TMDB ids on Jellyfin 12, so nothing could be matched to your Letterboxd data. The library is now read with full item details, with a second look at any film that still has no id, and the log says how many movies were found.

## [1.1.6] - 2026-10-25

### Fixed
- Poster badges are only placed on the poster card itself. Before, the buttons and links inside a card (which also carry the film's id) could receive a badge too.
- When a film's verified Letterboxd match has no rating or watchlist entry, the title-and-year guess is now tried too (before, a film with a stale match never got a badge).
- The log and the browser-console check (`__jellyboxed.diagnose()`) now report how many library films, ratings and watchlist films were seen and how many were matched, so missing badges can be traced.

## [1.1.5] - 2026-10-25

### Fixed
- Poster badges now sit on the poster picture itself (they could end up on the outer card, under the poster, and be hidden by some themes).
- Poster badges (stars, loved, watchlist) now also appear for films that have not been matched yet: the plugin guesses Letterboxd's address from the title and year until the verified match is known.
- The Letterboxd panel, floating button and badges no longer appear on shows, seasons or episodes. If Jellyfin can't tell what kind of item a page is, nothing is shown and it checks again shortly.

### Added
- **Hide badge on hover** setting: the poster badge fades out while the pointer is over the poster.

## [1.1.4] - 2026-10-25

### Fixed
- Opening a film no longer waits on the bot-check solver (those requests timed out and also held up the sync). Only background syncs read pages through the solver now.
- A page loaded through the solver is no longer wrongly rejected as a bot check just because it mentions Cloudflare. Only a real challenge page (its title) counts, and the log now shows the page title and size when one is rejected.

## [1.1.3] - 2026-10-25

### Fixed
- **Syncing now works behind Cloudflare's bot check when a solver (FlareSolverr / Byparr) is set up.** If Letterboxd still refuses the server's own requests even with a fresh pass, Jellyboxed now loads the pages through the solver's browser instead (for 30 minutes, then it tries directly again).
- Reconnecting or disconnecting an account during a sync no longer ends in an error; the sync restarts cleanly.
- The log now shows the browser identity the solver used.

## [1.1.2] - 2026-10-25

### Fixed
- When the bot-check solver (FlareSolverr / Byparr) fails, the log now says what it answered (HTTP status and message) instead of a JSON parse error. Also sends Byparr's `max_timeout` setting.

## [1.1.1] - 2026-10-25

### Added
- **Optional FlareSolverr / Byparr support.** When Letterboxd shows Cloudflare's bot check, Jellyboxed asks the solver for a fresh `cf_clearance` cookie and browser identity, then retries. Configure it under **Settings > Advanced > FlareSolverr address**. The settings page shows when a pass was last obtained.

## [1.1.0] - 2026-10-24

### Changed
- **New film panel design:** a card with a large star rating, an "Edit rating" / "Rate & log" button, a watchlist toggle, and friend cards with avatar, name and rating.
- **New poster badges:** compact capsules with a star icon, rating, heart and watchlist marker.
- New poster badge positions: top/bottom centre and middle left/right.

### Fixed
- Watchlist add/remove no longer fails with "Could not find Letterboxd's film id" (it now uses the film slug; the numeric id is only a fallback).
- Disconnect on the settings page works: a background sync can no longer re-create a removed account.
- A stuck "Loading Letterboxd..." panel after navigating between films.
- Poster badges refresh as films get matched and when you navigate.

## [1.0.2] - 2026-10-24

### Fixed
- Every user-facing API call failed with HTTP 500 on Jellyfin 12 because the authorization policy `DefaultAuthorization` no longer exists. The API now uses the standard login check.

## [1.0.1] - 2026-10-24

### Added
- Floating **Letterboxd** button that opens the film panel in a dialog when a theme or layout hides the normal panel.

### Fixed
- Checking pasted cookies now reports Letterboxd's bot check clearly instead of saying the cookies are invalid.
- Release workflow can reuse an existing release and can be started manually.

## [1.0.0] - 2026-10-24

First release: a complete Jellyfin 12 plugin rebuilt from the original proxy-and-script prototype.

### Added
- Jellyfin 12.x plugin (.NET 10) installable from a plugin repository manifest.
- Admin settings page: every feature has a switch, plus appearance, caching, sync and advanced options.
- Per-user Letterboxd accounts with three sign-in levels: username only (public, read-only), password sign-in (password never stored) and pasted cookies as a fallback.
- Poster badges (rating, loved, watchlist), "My Letterboxd" panel, friends' activity and reviews.
- Log and review dialog with half-star picker, rewatch, spoilers and date.
- Watchlist add/remove with optional mirroring to Jellyfin favourites.
- Server-side "log it?" prompt after watching, from any client.
- Background sync with persistent, atomic caches and stale-while-revalidate.
- Shared request gate with spacing, concurrency limit and automatic back-off.
- Automatic web interface injection without modifying files on disk.
- GPL-3.0 license, contributing guide, security policy, CI and release workflows.

### Fixed (compared with the prototype)
- No more unauthenticated open proxy; all endpoints use Jellyfin authentication.
- No HTML injection from Letterboxd content.
- Cookies rotated by Letterboxd are saved instead of lost.
- Transient failures are no longer cached as "not found".

[Unreleased]: https://github.com/YousefMohsen0/jellyboxed/compare/v1.1.7...HEAD
[1.1.7]: https://github.com/YousefMohsen0/jellyboxed/compare/v1.1.6...v1.1.7
[1.1.6]: https://github.com/YousefMohsen0/jellyboxed/compare/v1.1.5...v1.1.6
[1.1.5]: https://github.com/YousefMohsen0/jellyboxed/compare/v1.1.4...v1.1.5
[1.1.4]: https://github.com/YousefMohsen0/jellyboxed/compare/v1.1.3...v1.1.4
[1.1.3]: https://github.com/YousefMohsen0/jellyboxed/compare/v1.1.2...v1.1.3
[1.1.2]: https://github.com/YousefMohsen0/jellyboxed/compare/v1.1.1...v1.1.2
[1.1.1]: https://github.com/YousefMohsen0/jellyboxed/compare/v1.1.0...v1.1.1
[1.1.0]: https://github.com/YousefMohsen0/jellyboxed/compare/v1.0.2...v1.1.0
[1.0.2]: https://github.com/YousefMohsen0/jellyboxed/compare/v1.0.1...v1.0.2
[1.0.1]: https://github.com/YousefMohsen0/jellyboxed/compare/v1.0.0...v1.0.1
[1.0.0]: https://github.com/YousefMohsen0/jellyboxed/releases/tag/v1.0.0
