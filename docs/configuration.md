# Configuration Reference

All settings are in **Dashboard → Plugins → JellyNext**, split across seven tabs. Changes take effect when you press **Save Configuration** at the bottom of the page; this also saves the per-user settings of the user currently selected on the *Trakt* tab.

Settings are stored in Jellyfin's `plugins/configurations/Jellyfin.Plugin.JellyNext.xml`. API keys and the SMTP password are stored there in plain text, like other Jellyfin plugin credentials.

No setting requires a Jellyfin restart. Settings that affect the web client (widget, badge, icon) need a browser reload (Ctrl+F5).

## General

| Setting | Default | Range | Description |
|---|---|---|---|
| Content Cache Expiration (hours) | 6 | 1–168 | How long synced content stays valid. Playback requests and the widget look items up in this cache, so keep it **at least as long as the *Sync Trakt Content* task interval**, or requests made late in the interval fail with "not found in cache" in the log. |
| Use Short Dummy Video (2 seconds) | on | — | Placeholder items use a 2-second video, so playback ends on its own on clients that ignore the stop command. Off: a 1-hour video, which never reaches Jellyfin's "watched" threshold but must be stopped manually on such clients. Changing it rebuilds all placeholders on the next sync. |
| Playback Stop Delay (seconds) | 2 | 0–30 | Wait before JellyNext tells the client to stop playing a placeholder. Increase if a client ignores an early stop. |

The sync schedule itself is not a plugin setting: change it in **Dashboard → Scheduled Tasks → JellyNext**.

| Scheduled task | Default triggers | What it does |
|---|---|---|
| Sync Trakt Content | At startup, every 6 hours | Fetches recommendations/next seasons/trending, rewrites placeholders, scans JellyNext libraries, applies tags, sends new-season emails |
| Sync Trakt Watchlists | Every hour | Sends new watchlist entries to the download backend (only for users with watchlist sync on) |

## Trakt

### Trakt Connection

| Setting | Default | Description |
|---|---|---|
| Authorization Mode | Standalone | **Standalone** — JellyNext's own Trakt app and tokens. **Share the Trakt plugin's token** — uses the token the official Trakt plugin holds for the same Jellyfin user (recommended for free Trakt accounts running both plugins). **Own token, Trakt plugin's client ID** — experimental. See [Shared Trakt connection](usage.md#shared-trakt-connection-free-trakt-accounts). |
| Let JellyNext refresh the shared token | on | Shared-token mode only. On: JellyNext refreshes an expired token and writes the new pair back into the Trakt plugin. Off: JellyNext skips syncs until the Trakt plugin refreshes it. |

### Per-user settings

Select a user under **Trakt Account Linking**. Once linked, these options appear. They are saved with **Save Configuration**.

| Setting | Default | Range | Description |
|---|---|---|---|
| Sync Movie Recommendations | on | — | Fill this user's Movie Recommendations library |
| Sync Show Recommendations | on | — | Fill this user's Show Recommendations library |
| Sync Next Seasons | on | — | Fill this user's Next Seasons library. Also required for the widget, Modular Home section and new-season emails. |
| Auto-Add Watchlisted Movies | off | — | Send Trakt watchlist movies to the download backend (hourly) |
| Auto-Add Watchlisted Shows | off | — | Send Trakt watchlist shows (season 1) to the download backend (hourly) |
| Only Newly Released Seasons | off | — | Next Seasons only lists a season released within the window below |
| New Release Window (days) | 90 | 1–3650 | Measured from the season's first air date |
| Email Me About New Seasons | off | — | Requires the Notifications tab master switch and SMTP settings |
| Email Address | empty | — | Where this user's notifications go |
| Movie Recommendations Limit | 50 | 1–100 | Number of movie recommendations kept (after filtering) |
| Show Recommendations Limit | 50 | 1–100 | Number of show recommendations kept (after filtering) |
| Ignore Collected Items | on | — | Hide titles in the user's Trakt collection **or already in Jellyfin**. Also applies to Trending if this user is the Trending source user. |
| Ignore Watchlisted Items | off | — | Hide titles on the user's Trakt watchlist from recommendations |
| Limit Shows to Season 1 Only | on | — | Show recommendations get one placeholder (season 1). Off: one placeholder per aired season (10 if the count is unknown), which makes library scans slower. |

Changing *Sync Next Seasons*, *Only Newly Released Seasons*, *New Release Window*, *Ignore Collected Items* or *Ignore Watchlisted Items* queues a content sync immediately.

**Unlink Trakt Account** removes the user's JellyNext Trakt link.

## Trending

| Setting | Default | Range | Description |
|---|---|---|---|
| Enable Trending Movies | off | — | Maintain a server-wide Trending Movies library |
| Source User | none | — | A linked user whose Trakt access is used. Required. |
| Trending Movies Limit | 50 | 1–100 | Number of trending movies |

Library folder: `jellynext-virtual/global/movies_trending` (content type **Movies**). Disabling the feature empties the folder on the next sync.

## Downloads

Select one integration. Only the selected one is used.

### Native (Radarr/Sonarr)

| Setting | Required | Description |
|---|---|---|
| Radarr URL / Radarr API Key | for movies | e.g. `http://radarr:7878`. API key: Radarr → Settings → General. |
| Quality Profile / Root Folder (Radarr) | for movies | Loaded after **Test Connection** succeeds |
| Sonarr URL / Sonarr API Key | for shows | e.g. `http://sonarr:8989`. API key: Sonarr → Settings → General. |
| Quality Profile / Root Folder (Sonarr) | for shows | Loaded after **Test Connection** succeeds |
| Anime Root Folder (Optional) | no | Used for shows whose Trakt genres include "anime". Empty: regular root folder. |

Movies are added monitored and searched immediately. For shows, the series is added (or updated if it exists) with only the requested season monitored, and a search is started.

### Jellyseerr

| Setting | Required | Description |
|---|---|---|
| Jellyseerr URL | yes | e.g. `http://jellyseerr:5055` |
| Jellyseerr API Key | yes | Jellyseerr → Settings → General → API Key |
| Use Jellyseerr Default Config (Radarr / Sonarr) | default on | Use Jellyseerr's default server and profile. Untick to choose a server and quality profile from the lists (loaded after **Test Connection**). |
| Anime Quality Profile (Optional) | no | Manual Sonarr config only; used for anime |

Requests are made on behalf of the Jellyfin user who triggered them. Users unknown to Jellyseerr are imported from Jellyfin automatically, which requires Jellyseerr to use this Jellyfin server as its media server.

### Webhooks

| Setting | Default | Description |
|---|---|---|
| HTTP Method | POST | GET, POST, PUT or PATCH (shared by movies and shows) |
| Movie / TV Show Webhook URL | empty | Target URL; supports placeholders |
| Custom Headers | none | Name/value pairs; support placeholders |
| Request Payload | JSON template | Sent as `application/json` for POST/PUT/PATCH only |

See [Webhooks](webhooks.md) for placeholders and examples.

## Notifications

| Setting | Default | Range | Description |
|---|---|---|---|
| Enable New Season Email Notifications | off | — | Master switch; users also opt in on the Trakt tab |
| Announce Seasons Released Within (days) | 30 | 1–365 | Only seasons first aired within this many days are emailed |
| SMTP Server | empty | — | Host name |
| SMTP Port | 587 | 1–65535 | 587 (STARTTLS) or 25. **465 (implicit TLS) is not supported.** |
| Use STARTTLS | on | — | Disable only for an unencrypted local relay |
| Username / Password | empty | — | Empty username sends without authentication |
| From Address | empty | — | Many providers require it to match the account |
| From Name | JellyNext | — | Sender display name |

**Send Test Email** sends to *Test Recipient* using the **saved** settings — save first.

## Widget

| Setting | Default | Range | Description |
|---|---|---|---|
| Enable the New Seasons Widget | off | — | Adds the JellyNext script tag to the web client's `index.html` |
| Widget Heading | New Seasons | — | Row title (also used for the Modular Home section) |
| Shows to List | 12 | 1–50 | Number of cards (also used for the Modular Home section) |
| Position | Below | Above / Below | Place relative to Jellyfin's own home sections |
| Register a New Seasons section with Modular Home | off | — | See [Modular Home](usage.md#modular-home-section) |
| Add a Request button to the Modular Home cards | on | — | Adds the button client-side; off means the card's Play button is the request |

**Check Artwork** (pick a user, press the button) prints how each card's image was resolved.

## Library

| Setting | Default | Description |
|---|---|---|
| Tag JellyNext items | off | Writes a Jellyfin tag on every movie, show, season and episode in the JellyNext libraries |
| Tag | JellyNext | Tag name. Renaming moves the tag; disabling removes it. |
| Show a badge on tagged items | on | Web client only; requires tagging |
| Badge Text | empty (= tag name) | Badge label |
| Replace the play icon with a download icon | on | Web client only; requires tagging; cosmetic |
| Button Text | Request | Label of the item page's play button for tagged items |
