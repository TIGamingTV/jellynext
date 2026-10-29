# Using JellyNext

This guide explains how each feature behaves and how to use it. For the setting names, defaults and ranges see the [configuration reference](configuration.md). For first-time setup see the [README quick start](../README.md#quick-start).

- [Virtual libraries](#virtual-libraries)
- [Requesting content (playback)](#requesting-content-playback)
- [Watchlist auto-add](#watchlist-auto-add)
- [Trending Movies](#trending-movies)
- [New season emails](#new-season-emails)
- [New Seasons widget](#new-seasons-widget)
- [Modular Home section](#modular-home-section)
- [Marking JellyNext items](#marking-jellynext-items)
- [Official Trakt plugin and free Trakt accounts](#official-trakt-plugin-and-free-trakt-accounts)

## Virtual libraries

JellyNext writes small placeholder video files into folders, one folder per user and library type. You point Jellyfin libraries at those folders; Jellyfin identifies the placeholders by their IDs (`Title (Year) [tmdbid-123]`, `Title (Year) [tvdbid-123]`) and fetches metadata and artwork with its normal providers.

| Folder | Library type | Contents |
|---|---|---|
| `jellynext-virtual/<user-id>/movies_recommendations` | Movies | Trakt movie recommendations for that user |
| `jellynext-virtual/<user-id>/shows_recommendations` | Shows | Trakt show recommendations; one placeholder per season (season 1 only by default) |
| `jellynext-virtual/<user-id>/shows_nextseasons` | Shows | For each show the user is watching, the **next** unwatched season that has aired and is not in Jellyfin — one placeholder per show |
| `jellynext-virtual/global/movies_trending` | Movies | Trakt trending movies, shared by all users (optional) |

`jellynext-virtual` lives in the plugin's data folder, normally `<plugins dir>/Jellyfin.Plugin.JellyNext/`. The exact paths are printed in the Jellyfin log at startup under `JellyNext Virtual Libraries Initialized`.

**How the content is kept up to date**

- The **Sync Trakt Content** task (at startup and every 6 hours) fetches from Trakt, rewrites the placeholders and scans every library whose folder is under `jellynext-virtual`. You do not need to scan these libraries yourself.
- With **Ignore Collected Items** on (default), titles in the user's Trakt collection or already in Jellyfin are left out. Trakt's own filter is unreliable, so JellyNext also compares locally.
- Next Seasons follows the user's Trakt watch progress: once season 2 is marked watched on Trakt, season 3 appears after the next sync (if it has aired and is not in Jellyfin).
- Split seasons are covered too: if a season stops at a mid-season finale and the rest airs later under the same season number, the rest of that season appears (shown as "From episode N") once it returns, and counts as a new release from its return date. A break must be at least four weeks between two episodes; shorter gaps, such as holiday breaks, are ordinary weekly airing.
- With **Only Newly Released Seasons**, Next Seasons is limited to seasons first aired within the **New Release Window**, turning it from a backlog list into a "just released" list. A season disappears once it is older than the window, even if it is still airing.

**Permissions**: give each user access only to their own JellyNext libraries (**Dashboard → Users → user → Access**). The per-user folders are only meaningful for their owner.

## Requesting content (playback)

Playing any item in a JellyNext library is a request.

1. The user presses **Play** on a movie or on a season's `Download Season N` episode.
2. JellyNext identifies the title (and season), and sends it to the backend selected on the *Downloads* tab:
   - **Native**: movie → Radarr (monitored, searched). Season → Sonarr: series added if missing, only the requested season monitored, search started. Anime (Trakt genre "anime") goes to the *Anime Root Folder* if set.
   - **Jellyseerr**: a request is created on behalf of the Jellyfin user who pressed Play (imported into Jellyseerr if needed). Jellyseerr's approval rules apply.
   - **Webhooks**: an HTTP request is sent — see [Webhooks](webhooks.md).
3. The client shows a **JellyNext Download** message with the result (success or the error).
4. After the *Playback Stop Delay* (2 s), JellyNext stops playback. When playback ends it resets the item's (and its parent's) played state and position, so the placeholder is not marked watched and does not show up in Next Up.

Some clients ignore the remote stop command; stop playback manually there — the request has already been sent. With *Use Short Dummy Video* on (default), playback ends by itself after 2 seconds anyway.

Once the download arrives in your real library, the item drops out of the JellyNext library on the next sync (always for Next Seasons; for recommendations when *Ignore Collected Items* is on).

Requests only work after a sync has cached the content (the lookup uses the in-memory cache, which is rebuilt at startup). See *Content Cache Expiration* in the [configuration reference](configuration.md#general).

## Watchlist auto-add

Sends a user's Trakt watchlist to the download backend without anyone pressing Play.

1. Configure a download backend.
2. *Trakt* tab → select the user → tick **Auto-Add Watchlisted Movies** and/or **Auto-Add Watchlisted Shows** → **Save Configuration**.
3. The **Sync Trakt Watchlists** task runs hourly (or run it from **Dashboard → Scheduled Tasks**).

Behaviour:

- Items already in Jellyfin are skipped.
- Shows are requested with **season 1** (Trakt watchlists do not name seasons).
- An item is requested once per server run while it stays on the watchlist. After a Jellyfin restart, pending items are sent again once; Radarr/Sonarr/Jellyseerr reject duplicates.
- Items are sent one per second.

## Trending Movies

A single library of Trakt's currently trending movies, shared by all users.

1. *Trending* tab → **Enable Trending Movies**, choose a **Source User** (any linked user; their Trakt access is used, and their *Ignore Collected Items* setting decides whether owned movies are hidden) → **Save Configuration**.
2. Run **Sync Trakt Content**.
3. Add a **Movies** library pointing to `jellynext-virtual/global/movies_trending`.

Playing an item requests it like any other JellyNext item.

## New season emails

Setup and behaviour are described in the README: [New season email notifications](../README.md#new-season-email-notifications). For every SMTP setting see the [configuration reference](configuration.md#notifications).

## New Seasons widget

A home-screen row in the Jellyfin **web client** listing the user's next seasons, each with a **Request** button.

1. *Widget* tab → tick **Enable the New Seasons Widget** → optionally set heading, number of shows and position → **Save Configuration**.
2. Reload the web client with Ctrl+F5.

Behaviour:

- Shows the same seasons as the user's Next Seasons library (including the release-window filter), most recently premiered first. Requires *Sync Next Seasons* for that user; the row is hidden when empty. It makes no extra Trakt requests.
- **Request** sends the season to the configured backend on behalf of the signed-in user; the card then leaves the row. A requested season that never arrives is offered again after 14 days.
- Seasons that arrive in Jellyfin or age out of the release window disappear when the row loads, without waiting for a sync.
- Artwork is taken from your library and metadata providers first, Trakt last. Use **Check Artwork** on the Widget tab when cards show only a name.
- It works in the web client and apps that embed it (desktop apps, browsers). Native apps (Android TV, iOS, Roku, Kodi) cannot load plugin scripts; users there request via the Next Seasons library.

How it is loaded: Jellyfin has no plugin API for the web client, so JellyNext adds a `<script>` tag to the web client's `index.html`. The same script serves the widget, the Modular Home Request button and the badge/download icon. The tag is re-added on every server start (Jellyfin upgrades replace the file) and removed as soon as none of those features is enabled, or when the plugin is uninstalled. If Jellyfin cannot write to its web directory, the widget does not appear and the log says `Could not update the web client's index.html`.

## Modular Home section

For servers running the [Modular Home](https://github.com/IAmParadox27/jellyfin-plugin-home-sections) plugin ("Home Screen Sections"), JellyNext can register **New Seasons** as a section that users place themselves.

1. Install Modular Home (it needs its own dependencies — see its documentation).
2. *Widget* tab → check that the status line reports Modular Home as detected → tick **Register a New Seasons section with Modular Home** → **Save Configuration**.
3. **Each user** enables the section in their own Modular Home settings. Nothing appears until they do.

Behaviour:

- Same content and limits as the widget. Cards are real items from the Next Seasons library, so that library must be set up and scanned.
- With **Add a Request button to the Modular Home cards** (default on), JellyNext's script adds a Request button. Without it, the card's Play button requests the season (works on TV layouts too).
- While this is enabled, the standalone widget row is not drawn on Modular Home's home screen, so seasons are not listed twice.
- Modular Home draws third-party sections as landscape cards; an admin can switch this section to portrait in Modular Home's settings. Disabling the integration empties the section (Modular Home offers no way to remove it).

## Marking JellyNext items

Once scanned, placeholders look like real media. The *Library* tab can mark them.

1. *Library* tab → tick **Tag JellyNext items** (optionally rename the tag) → **Save Configuration**.
2. Optionally keep **Show a badge on tagged items** and **Replace the play icon with a download icon** on, and set the badge and button text. Reload the web client with Ctrl+F5.

Behaviour:

- **Tag**: a normal Jellyfin tag on every movie, show, season and episode in the JellyNext libraries — visible in every client, usable in search, filters and collections. It is applied when items are scanned, after every content sync and on every save. Disabling or renaming it removes the old tag.
- **Badge / download icon**: web client only (same script as the widget), and only on tagged items. The icon and "Request" label are cosmetic: pressing them still plays the placeholder, which sends the request.

## Official Trakt plugin and free Trakt accounts

Excluding JellyNext's libraries in the official Trakt plugin, Trakt's one-app limit on free accounts, and the three authorization modes are described in the README: [Compatibility with the official Trakt plugin](../README.md#compatibility-with-the-official-trakt-plugin).
