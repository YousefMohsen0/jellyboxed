# Changelog

All notable changes are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

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

[Unreleased]: https://github.com/YousefMohsen0/jellyboxed/compare/v1.1.2...HEAD
[1.1.2]: https://github.com/YousefMohsen0/jellyboxed/compare/v1.1.1...v1.1.2
[1.1.1]: https://github.com/YousefMohsen0/jellyboxed/compare/v1.1.0...v1.1.1
[1.1.0]: https://github.com/YousefMohsen0/jellyboxed/compare/v1.0.2...v1.1.0
[1.0.2]: https://github.com/YousefMohsen0/jellyboxed/compare/v1.0.1...v1.0.2
[1.0.1]: https://github.com/YousefMohsen0/jellyboxed/compare/v1.0.0...v1.0.1
[1.0.0]: https://github.com/YousefMohsen0/jellyboxed/releases/tag/v1.0.0
