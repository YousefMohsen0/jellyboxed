# Security Policy

## Supported versions

Only the latest release receives security fixes.

## Reporting a vulnerability

Please **do not open a public issue**. Report privately through GitHub:
**Security → Report a vulnerability** on the [repository page](https://github.com/YousefMohsen0/jellyboxed/security/advisories/new).

Include what you found, how to reproduce it, and the plugin and Jellyfin versions. You will get a reply as soon as possible, and we will credit you in the release notes if you wish.

## Scope

Things we want to hear about:

- Letterboxd passwords, cookies or sessions being exposed to browsers, logs or other users
- Bypassing authentication or administrator checks on the plugin's API
- Cross-site scripting or injection through data from Letterboxd or Jellyfin
- The image proxy being usable to reach internal or arbitrary hosts (SSRF)
- Anything that lets one Jellyfin user read or act on another user's Letterboxd data

Out of scope: Letterboxd or Jellyfin vulnerabilities themselves, and issues requiring an already-compromised administrator account or server.

## If you posted credentials by accident

Rotate them right away: change your Letterboxd password (this invalidates sessions), and regenerate any Jellyfin API key.
