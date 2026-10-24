# Jellyboxed

**Letterboxd inside Jellyfin.** See your Letterboxd ratings on your library's posters, rate and review films without leaving Jellyfin, check what your friends thought, and keep your watchlist in sync.

> Works with **Jellyfin 12.x** (.NET 10). Everything is optional: every feature has a switch in the plugin settings.

## What you get

| Feature | What it does | Needs |
| --- | --- | --- |
| **Poster badges** | Your star rating, "loved" heart and watchlist marker on library posters | A username |
| **My Letterboxd** on film pages | Your rating and like, right under the film's details | A username |
| **Log & review** | Rate (half stars), review, mark as rewatch / spoilers, pick the watched date, and save it to your Letterboxd diary | Signed in |
| **After-watching prompt** | A small "Log it on Letterboxd?" card once you have watched most of a movie (works from any client, not just the web) | Signed in |
| **Watchlist** | Add or remove a film from your Letterboxd watchlist; optionally mirror it onto Jellyfin favourites | Signed in |
| **Activity from friends** | Your friends' ratings and reviews for the film | Signed in |

Every Jellyfin user connects **their own** Letterboxd account. Nobody can see or use anybody else's.

## Install

1. In Jellyfin go to **Dashboard → Plugins → Repositories → Add** and enter:

   ```
   https://raw.githubusercontent.com/YousefMohsen0/jellyboxed/main/manifest.json
   ```

2. Open the **Catalogue**, find **Jellyboxed** under *General*, and install it.
3. **Restart Jellyfin.**
4. Open **Dashboard → Plugins → Jellyboxed** to review the settings (the defaults work).
5. Open any movie in the web interface (hard-refresh the page once) and click **Connect Letterboxd**.

No other plugin is required. Jellyboxed adds its script to the web interface by itself, and nothing on disk is modified, so uninstalling the plugin removes it completely.

<details>
<summary>Manual install</summary>

Download `jellyboxed_<version>.zip` from the [Releases](https://github.com/YousefMohsen0/jellyboxed/releases) page, unzip it into a folder named `Jellyboxed` inside Jellyfin's `plugins` directory, and restart Jellyfin.

</details>

## Connecting your Letterboxd account

Letterboxd has no public API for personal use, so Jellyboxed reads Letterboxd's website on your behalf. You can choose how much access to give:

| Option | Gives you | How |
| --- | --- | --- |
| **Username only** | Poster badges and "My Letterboxd" (read-only) from your *public* profile | Type your username. Nothing secret is involved. |
| **Sign in** | Everything: logging, watchlist edits, friends' activity | Username/email + password. The password is used **once** to sign in and is **never stored**; only the resulting session is kept on the server. |
| **Cookies** (fallback) | Same as *Sign in* | Use this if Letterboxd shows a captcha when signing in. Log in at letterboxd.com in your browser, copy your cookies (dev tools → Application/Storage → Cookies, or a cookie-export extension) and paste them. Treat them like a password. |

Users connect from any film page (**Connect Letterboxd**). Administrators can also connect accounts for users under **Dashboard → Plugins → Jellyboxed → Letterboxd accounts**, and can disable self-service or any sign-in method.

After connecting, Jellyboxed does a first sync in the background (a few minutes for big libraries). Badges appear gradually as films are matched.

## Settings (administrators)

**Dashboard → Plugins → Jellyboxed**

- **Features**: turn off any part of the plugin for everyone (friends, logging, watchlist, badges, prompt, ...).
- **Sign-in options**: allow or forbid self-service, password sign-in, cookie sign-in.
- **Appearance**: accent colour, badge position and contents, number of friends shown.
- **Caching & syncing**: how often to sync, how long to trust cached data, how gently to talk to Letterboxd.
- **Advanced**: custom CSS selectors if a theme moves things around, custom User-Agent, debug logging.
- **Accounts**: see who is connected, trigger a sync, connect or disconnect users.

Each user also has a small **Account** dialog (on any film page) with personal options, such as mirroring their watchlist to Jellyfin favourites (off by default; it only ever adds favourites) or switching off the after-watching prompt.

## How it works

- A background service reads each connected account's rated films, likes and watchlist (a handful of requests), then keeps up with new activity through the profile's RSS feed.
- Letterboxd lists films by *slug* and Jellyfin by *TMDB id*. Jellyboxed matches them once, remembers the match, and only ever does the difference afterwards.
- Everything is **cached on the server** (in the plugin's data folder) and shown instantly; stale data is refreshed in the background.
- All requests to Letterboxd go through one gentle queue: limited concurrency, spaced out, and if Letterboxd pushes back (rate limit or bot check) Jellyboxed **pauses all requests** instead of hammering the site.
- Friends' avatars are served through your Jellyfin server, so browsers never contact third parties.

## Privacy & security

- Sessions and cookies are stored only on your server (`accounts.json` in the plugin data folder, owner-readable only) and are **never sent to browsers**.
- Everything coming from Letterboxd is shown as plain text, never as HTML.
- The plugin's API requires a signed-in Jellyfin user; administrators-only actions require an administrator.
- Jellyboxed is **unofficial** and not affiliated with Letterboxd or Jellyfin. It reads the website at human-scale volume for personal use. Please respect [Letterboxd's terms](https://letterboxd.com/legal/terms-of-use/) and do not raise the request speed without a reason.

## Troubleshooting

| Problem | Try |
| --- | --- |
| Nothing shows up in the web UI | Hard-refresh the page (Ctrl+F5). Check *Add Jellyboxed to the Jellyfin web interface* is on. View the page source of `/web/index.html`: it should contain `Jellyboxed/client.js`. |
| The panel is missing on film pages but badges work | A theme or the new "Modern" layout may use different markup. Set **Advanced → Where to place the film panel** to a CSS selector of your page (right-click the details area → Inspect). |
| Posters have no badges | Cards are found with `[data-id]`; set **Advanced → Which elements are library poster cards** if your theme differs. Also check the Accounts table: the first sync may still be matching films. |
| "Letterboxd requests are paused" | Letterboxd rate-limited or showed a bot check to your server. Wait it out, or use *Resume* on the settings page. Lower the request speed in settings if it keeps happening. |
| Sign-in says it needs a captcha | Use the **Cookies** option instead. |
| "Your session has expired" | Sign in again from the Account dialog. |
| A film cannot be matched | It has no TMDB id in Jellyfin, or Letterboxd does not know it. Refresh its metadata in Jellyfin. |
| Something else | Turn on **Debug logging**, reproduce, and open an [issue](https://github.com/YousefMohsen0/jellyboxed/issues) with the log (remove personal data). |

### Loading the script another way

If you would rather not let the plugin touch `index.html`, turn off **Add Jellyboxed to the Jellyfin web interface** and load these two tags yourself (for example with the *File Transformation* or *JavaScript Injector* plugins, or your reverse proxy):

```html
<link rel="stylesheet" href="/Jellyboxed/client.css">
<script defer src="/Jellyboxed/client.js"></script>
```

## Build from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet build Jellyboxed.sln -c Release
dotnet test Jellyboxed.sln
```

Copy `Jellyfin.Plugin.Jellyboxed/bin/Release/net10.0/Jellyfin.Plugin.Jellyboxed.dll` into `<jellyfin data>/plugins/Jellyboxed/` and restart Jellyfin. See [CONTRIBUTING.md](CONTRIBUTING.md) for the full development guide and [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for how it is put together.

## Contributing

Contributions are very welcome: bug reports, ideas, fixes, translations, documentation. Start with [CONTRIBUTING.md](CONTRIBUTING.md).

## Credits

Created by [Yousef Mohsen](https://github.com/YousefMohsen0), built together with [Claude](https://claude.ai) (Anthropic). It began as a Python proxy plus a script for the *JavaScript Injector* plugin and grew into this plugin.

Letterboxd is a trademark of its owners. Jellyfin is a trademark of the Jellyfin project.

## License

[GPL-3.0-or-later](LICENSE). Jellyfin's plugin libraries are GPL licensed, so plugins that use them are too.
