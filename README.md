<div align="center">
  <img src="images/jellynext_logo_full.png" alt="JellyNext Logo" width="600"/>

  <h3>Trakt-Powered Discovery for Jellyfin</h3>
</div>

JellyNext is a Jellyfin plugin that turns each user's Trakt history into browsable Jellyfin libraries: personalized movie and show recommendations, and the next season of shows they are watching. Playing an item from these libraries requests it through Radarr/Sonarr, Jellyseerr or a custom webhook — so users discover and request content without leaving Jellyfin.

It is aimed at Jellyfin server admins who already use (or want to use) Trakt, and optionally an *arr stack or Jellyseerr.

## Features

- **Per-user Trakt linking** — each Jellyfin user connects their own Trakt account, or borrows the official Trakt plugin's connection to stay within Trakt's free-tier app limit.
- **Virtual libraries** — per-user *Movie Recommendations*, *Show Recommendations* and *Next Seasons* libraries, plus an optional server-wide *Trending Movies* library. Items look like normal library items, with Jellyfin's own metadata and artwork.
- **Request by pressing Play** — playing a placeholder item sends a request to the configured backend and stops playback. Works in every Jellyfin client.
- **Three download backends** — Radarr/Sonarr (native), Jellyseerr (with per-user attribution and approval workflows), or custom HTTP webhooks.
- **Watchlist auto-add** — optionally sends a user's Trakt watchlist to the download backend every hour.
- **New season emails** — optional per-user email digest when a new season of a watched show is released.
- **New Seasons home-screen widget** — optional row on the web client home screen with a **Request** button per season; can also register as a [Modular Home](https://github.com/IAmParadox27/jellyfin-plugin-home-sections) section.
- **Item marking** — optional Jellyfin tag on all JellyNext items, plus a badge and download icon in the web client.

## Requirements

| Requirement | Notes |
|---|---|
| Jellyfin **12.0.0 or newer** | The plugin repository only publishes builds for Jellyfin 12.x. For 10.11.x see [Jellyfin 10.11](#jellyfin-1011). |
| A [Trakt](https://trakt.tv) account per user | Free accounts work. Free accounts allow only one connected app — see [Shared Trakt connection](docs/usage.md#shared-trakt-connection-free-trakt-accounts) if you also use the official Trakt plugin. |
| *(Optional)* Radarr + Sonarr, Jellyseerr, or an HTTP endpoint | Needed only for downloads. Without one, the libraries still work for browsing. |
| *(Optional)* SMTP server | Only for new-season emails. STARTTLS (587) or plain (25); port 465 is not supported. |
| *(Optional)* Write access to Jellyfin's web directory | Only for the widget, badge and download icon, which are added to the web client's `index.html`. |

## Installation

### 1. Add the plugin repository

1. In Jellyfin, open **Dashboard → Plugins** and go to the repository list (**Repositories** / **Manage Repositories**, depending on your Jellyfin version).
2. Add a repository named `JellyNext` with this URL:

```text
https://raw.githubusercontent.com/TIGamingTV/jellynext/main/manifest.json
```

3. Save.

### 2. Install the plugin

1. Open the plugin **Catalog**, find **JellyNext** and click **Install**.
2. Restart Jellyfin.
3. Verify: **Dashboard → Plugins** lists **JellyNext** as active, and clicking it opens the JellyNext settings page with the tabs *General, Trakt, Trending, Downloads, Notifications, Widget, Library*.

> The upstream project (`luall0/jellynext`) publishes its own repository with a different plugin GUID. Install JellyNext from **one** repository only.

### Manual installation

1. Download `jellynext-v<version>.zip` from [Releases](https://github.com/TIGamingTV/jellynext/releases).
2. Extract it into a new folder inside Jellyfin's `plugins` directory, e.g. `plugins/JellyNext_2.6.0.0/`. Common locations of the `plugins` directory:
   - Linux packages: `/var/lib/jellyfin/plugins/`
   - Official Docker image: `/config/plugins/`
   - linuxserver.io Docker image: `/config/data/plugins/`
3. Restart Jellyfin.

### Jellyfin 10.11

The last build for Jellyfin 10.11.x (`net9.0`) is **v2.3.0.0**. Install `jellynext-v2.3.0.0.zip` (not the `-jf12` file) from the [v2.3.0.0 release](https://github.com/TIGamingTV/jellynext/releases/tag/v2.3.0.0) manually. It receives no further updates; upgrade to Jellyfin 12 to use the repository.

## Quick start

All settings live in **Dashboard → Plugins → JellyNext**. Settings are saved with **Save Configuration** at the bottom of the page.

1. **Link Trakt** — *Trakt* tab → select a user → **Link Trakt Account**. Open <https://trakt.tv/activate>, enter the displayed code and approve. The page switches to "This user has authorized their Trakt account" and shows the per-user sync options.
2. **Run the first sync** — **Dashboard → Scheduled Tasks → JellyNext → Sync Trakt Content** → run. (It also runs at every server start and every 6 hours.)
3. **Find the library folders** — the folders are created when the user is linked. Their exact paths are written to the Jellyfin log at startup under `JellyNext Virtual Libraries Initialized` (restart Jellyfin once after linking a user to see them). They look like:

   ```text
   <plugins dir>/Jellyfin.Plugin.JellyNext/jellynext-virtual/<jellyfin-user-id>/movies_recommendations
   <plugins dir>/Jellyfin.Plugin.JellyNext/jellynext-virtual/<jellyfin-user-id>/shows_recommendations
   <plugins dir>/Jellyfin.Plugin.JellyNext/jellynext-virtual/<jellyfin-user-id>/shows_nextseasons
   ```

4. **Add the libraries** — **Dashboard → Libraries → Add Media Library**, one library per folder: content type **Movies** for `movies_recommendations`, **Shows** for the two `shows_*` folders. Use the path from the log (inside a container, use the container's path). Give each user access only to their own libraries.
5. **Run Sync Trakt Content again** — it fills the folders and scans the JellyNext libraries. Recommendations and next seasons now appear.
6. **Configure downloads** *(optional)* — *Downloads* tab: pick **Native (Radarr/Sonarr)**, **Jellyseerr** or **Webhooks**, enter URL + API key, press **Test Connection**, select profiles/folders, save.
7. **Request something** — play any item in a JellyNext library. A "JellyNext Download" message appears, playback stops after ~2 seconds, and the movie or season is added to your backend.

Also exclude the JellyNext libraries in the official Trakt plugin, if you use it, so placeholder playback is never scrobbled to Trakt.

## Documentation

| Guide | Contents |
|---|---|
| [Usage](docs/usage.md) | How each feature works: libraries, requesting, watchlist sync, trending, emails, widget, Modular Home, tags/badges, shared Trakt connection |
| [Configuration reference](docs/configuration.md) | Every setting, per tab, with defaults and ranges |
| [Webhooks](docs/webhooks.md) | Webhook mode: placeholders, payloads, examples, limitations |
| [Troubleshooting & FAQ](docs/troubleshooting.md) | Common problems, log messages to look for, FAQ |
| [Development](docs/development.md) | Building, debugging with Docker, releasing |
| [Changelog](CHANGELOG.md) | Release history |

## Updating and uninstalling

- **Update**: when installed from the repository, Jellyfin offers updates in **Dashboard → Plugins**; restart after updating. Settings are kept in `plugins/configurations/Jellyfin.Plugin.JellyNext.xml` — back this file up before major upgrades.
- **Uninstall**: **Dashboard → Plugins → JellyNext → Uninstall**, then restart. This removes the web client script tag. Delete the JellyNext libraries in **Dashboard → Libraries** yourself; the placeholder folders stay on disk until you delete them.

## License

GNU General Public License v3.0 — see [LICENSE](LICENSE). Copyright (C) 2025 luall0.

## Acknowledgments

[Trakt](https://trakt.tv), [Jellyfin](https://jellyfin.org), [jellyfin-plugin-trakt](https://github.com/jellyfin/jellyfin-plugin-trakt) (OAuth reference), [Jellyseerr](https://github.com/Fallenbagel/jellyseerr), [Radarr](https://radarr.video), [Sonarr](https://sonarr.tv), and [@medallyon](https://github.com/medallyon) for watchlist sync.
