# Contributing to Jellyboxed

Thanks for helping! Jellyboxed is a community project and every kind of contribution counts: bug reports, ideas, documentation, code, testing on different themes and Jellyfin setups.

By taking part you agree to follow our [Code of Conduct](CODE_OF_CONDUCT.md). Contributions are licensed under the project's [GPL-3.0](LICENSE) license.

## Ways to contribute (no code needed)

- **Report a bug**: open an [issue](https://github.com/YousefMohsen0/jellyboxed/issues/new/choose). Include your Jellyfin version, plugin version, browser/client, what you expected and what happened. Turn on **Debug logging** in the plugin settings and attach the relevant log lines (remove anything personal; never post cookies or passwords).
- **Suggest a feature**: open an issue first so we can talk about the idea before you spend time on it.
- **Test**: try the plugin with different themes, the *Modern* and *Legacy* web layouts, big libraries, several users, and tell us what breaks.
- **Improve the docs**: fix a typo, add a screenshot, clarify a confusing step.
- **Security problems**: please do not open a public issue; see [SECURITY.md](SECURITY.md).

## Development setup

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download), Git, and (for the browser-script checks) Node.js 20+. A Jellyfin 12.x server for manual testing is strongly recommended.

```bash
git clone https://github.com/YousefMohsen0/jellyboxed.git
cd jellyboxed
dotnet build Jellyboxed.sln
dotnet test Jellyboxed.sln
node --check Jellyfin.Plugin.Jellyboxed/Web/client.js
```

### Trying your changes in Jellyfin

1. Build: `dotnet build Jellyfin.Plugin.Jellyboxed -c Debug`.
2. Copy `Jellyfin.Plugin.Jellyboxed/bin/Debug/net10.0/Jellyfin.Plugin.Jellyboxed.dll` to `<jellyfin data dir>/plugins/Jellyboxed/` (create the folder; for a fresh install also add a `meta.json`, or simply install a release once and overwrite its DLL).
3. Restart Jellyfin, then hard-refresh the browser tab.

Changes to `client.js`, `client.css` and `configPage.html` are embedded into the DLL, so they need a rebuild too. For fast front-end iteration, set **Add Jellyboxed to the web interface** off and load `client.js` from a local dev server or a browser extension instead.

Enable **Debug logging** in the plugin settings for verbose server logs and `[Jellyboxed]` messages in the browser console.

### Project layout

```
Jellyfin.Plugin.Jellyboxed/
  Plugin.cs, PluginServiceRegistrator.cs   plugin entry point and dependency registration
  Configuration/                           settings model + the admin settings page (configPage.html)
  Api/JellyboxedController.cs              the HTTP API used by the browser script and settings page
  Letterboxd/                              everything that talks to / understands Letterboxd
    LetterboxdParser.cs                      all HTML/RSS knowledge lives here (pure functions)
    LetterboxdClient.cs                      requests, sign-in, logging, watchlist
    RequestGate.cs                           shared throttling and back-off
    HtmlLite.cs                              tiny dependency-free HTML parser + CSS-subset selectors
  Services/                                the application logic
    FilmService.cs                           per-film answers, stale-while-revalidate, badges
    SyncService.cs                           background syncing
    PlaybackWatcher.cs                       "log it?" prompts after watching
    JellyfinBridge.cs                        the only file that touches Jellyfin's library/user APIs
    Stores.cs, ClientPool.cs                 persistent caches and per-user sessions
  Data/                                    models and the atomic JSON file helper
  Web/                                     client.js, client.css, script injection middleware
Jellyfin.Plugin.Jellyboxed.Tests/          unit tests
docs/ARCHITECTURE.md                       how the pieces fit together
```

Read [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) before making larger changes.

## Making a change

1. **Fork** the repository and create a branch from `main`: `feat/short-name`, `fix/short-name`, `docs/short-name`.
2. Keep the change focused: one fix or feature per pull request.
3. Add or update **tests** when you touch logic (parsers, caching, throttling, anything in `Services/` that can be tested without a running server).
4. Run `dotnet build`, `dotnet test` and `node --check` on the script before pushing; CI runs the same.
5. Update the README or `CHANGELOG.md` (under *Unreleased*) when behaviour changes.
6. Open a pull request using the template and describe *what* and *why*. Screenshots help for UI changes.

### Commit messages

We use [Conventional Commits](https://www.conventionalcommits.org/): `feat: ...`, `fix: ...`, `docs: ...`, `refactor: ...`, `test: ...`, `chore: ...`. Keep the first line under about 70 characters and explain the *why* in the body when it is not obvious. If someone (or an AI assistant) helped substantially, credit them with a `Co-Authored-By:` trailer.

### Code guidelines

- **C#**: file-scoped namespaces, nullable reference types on, `async`/`await` with `CancellationToken` passed through. Follow `.editorconfig`.
- **Jellyfin APIs**: keep them inside `JellyfinBridge.cs` (and the controller/plugin classes where unavoidable). Jellyfin's plugin APIs change between versions, and isolating them makes upgrades small.
- **Letterboxd markup**: put selectors and parsing in `LetterboxdParser.cs` only, with a unit test using a trimmed, anonymised HTML snippet. When Letterboxd changes its pages, only that file and its tests should need to change.
- **Be gentle with Letterboxd**: every request must go through `RequestGate`. Never add background loops that poll quickly, and prefer cached data.
- **Secrets**: never log cookies, passwords or tokens, and never send them to the browser. Do not commit real credentials, cookies, API keys or `.env` files.
- **Browser script**: plain JavaScript, no build step, no dependencies. **Never use `innerHTML`** with data from Letterboxd or Jellyfin; use the `h()` helper (text nodes). Everything is namespaced `jbx-`. Keep it keyboard accessible (focusable controls, `Esc` closes dialogs, labels).
- **Settings**: a new feature gets a switch in `PluginConfiguration`, a line on the settings page, and is exposed to the browser through `/Jellyboxed/me`. Default to *off* for anything that changes the user's Jellyfin data.
- **Errors**: API errors must never be HTTP 401/403 (the Jellyfin web client treats those as "signed out"). Use the helpers in the controller.

### Tests

```bash
dotnet test Jellyboxed.sln
```

Tests live in `Jellyfin.Plugin.Jellyboxed.Tests`. Parser tests should use small fixtures embedded in the test; do not commit full copies of Letterboxd pages or any personal data.

## Maintainers: cutting a release

1. Move the *Unreleased* notes in `CHANGELOG.md` under a new `## [x.y.z] - date` heading and commit to `main`.
2. Tag and push: `git tag vX.Y.Z && git push origin vX.Y.Z`.
3. The **Release** workflow builds and tests, publishes the GitHub release with the plugin zip, and adds the version (with its checksum) to `manifest.json` on `main`, which is what Jellyfin's catalogue reads.
4. Check the release on a real Jellyfin server before announcing it.

When a new Jellyfin major version changes the plugin API, bump `TargetFramework`, the `Jellyfin.*` package version and `--abi` in `scripts/package.py`, then fix the compile errors (they should all be in `JellyfinBridge.cs` and `Plugin*.cs`).

## Questions

Open a [discussion or issue](https://github.com/YousefMohsen0/jellyboxed/issues). There are no silly questions.
