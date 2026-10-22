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
- [Shared Trakt connection (free Trakt accounts)](#shared-trakt-connection-free-trakt-accounts)
- [Using the official Trakt plugin alongside JellyNext](#using-the-official-trakt-plugin-alongside-jellynext)

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

Emails a user when a new season of a show they watch is released.

1. *Notifications* tab → fill in the SMTP settings, tick **Enable New Season Email Notifications**, **Save Configuration**.
2. Enter a **Test Recipient** and press **Send Test Email** to confirm SMTP works.
3. *Trakt* tab → select the user → make sure **Sync Next Seasons** is on → tick **Email Me About New Seasons**, enter the **Email Address** → **Save Configuration**.

Behaviour:

- Emails are sent at the end of each **Sync Trakt Content** run, at most one digest per user per run, listing show, season and premiere date.
- A season is included when it appears in the user's Next Seasons list **and** was first aired within *Announce Seasons Released Within* (default 30 days). Catching up on an old show does not trigger emails.
- Each season is announced once; the record survives restarts. If sending fails, nothing is recorded and the next sync retries.

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

## Shared Trakt connection (free Trakt accounts)

Trakt's free tier allows one connected app per account. JellyNext and the official Jellyfin Trakt plugin are different apps, so on a free account the second one to connect is rejected. Trakt VIP accounts are not affected.

To share the official plugin's connection:

1. Install the official Trakt plugin and link each user's Trakt account **there**.
2. JellyNext *Trakt* tab → **Authorization Mode** → *Share the Trakt plugin's token* → **Save Configuration**.
3. For each user: select them and press **Use the Trakt Plugin's Account**. No Trakt authorization happens; this only registers the user with JellyNext.

Keep **Let JellyNext refresh the shared token** on (default). Trakt refresh tokens are single-use, so JellyNext writes the refreshed token back into the official plugin. If you turn it off, JellyNext skips syncs while the token is expired and waits for the official plugin, which only refreshes when it makes a call (scrobbling or its sync task, which has no default schedule).

*Own token, Trakt plugin's client ID* is experimental: it is not verified that Trakt keeps both tokens valid. Prefer the shared-token mode.

In both shared modes JellyNext never scrobbles or writes to your Trakt account, apart from the token refresh described above.

## Using the official Trakt plugin alongside JellyNext

In the official Trakt plugin's settings, exclude every JellyNext library for each user, so pressing Play on a placeholder is never scrobbled to Trakt. Your real libraries continue to sync normally.
