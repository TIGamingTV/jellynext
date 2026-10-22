# Troubleshooting & FAQ

Most answers are in the Jellyfin log: **Dashboard → Logs**. JellyNext's log lines come from classes such as `VirtualLibraryManager`, `ContentSyncService`, `PlaybackInterceptor`, `TraktApi`, `RadarrService`, `SonarrService`, `JellyseerrService` and `WebhookDownloadProvider`. Scheduled task results are under **Dashboard → Scheduled Tasks → JellyNext**.

## Installation

**JellyNext is not in the catalog**
- Check the repository URL is exactly `https://raw.githubusercontent.com/TIGamingTV/jellynext/main/manifest.json`.
- The repository only lists builds for Jellyfin **12.0.0+**. A 10.11 server sees nothing; see [Jellyfin 10.11](../README.md#jellyfin-1011).

**JellyNext shows another plugin's name or description, or two JellyNext entries appear**
- Releases up to v2.3.0.0 used a GUID shared with another plugin. Uninstall JellyNext, restart, and install the current version from the repository above. Settings are kept (they are stored by file name, not GUID).
- Do not add both this repository and the upstream `luall0` repository.

**Plugin does not load after a manual install**
- The zip must be extracted into its own folder inside `plugins` (for example `plugins/JellyNext_2.6.0.0/`), not directly into `plugins`.
- Check the log at startup for plugin load errors. Current builds need Jellyfin 12 (.NET 10).

## Libraries

**I can't find the `jellynext-virtual` folders**
- They are created when a user is linked. Restart Jellyfin and search the log for `JellyNext Virtual Libraries Initialized`; the block lists every path. If it says `No Trakt users configured yet`, link a user first.
- In Docker, the path in the log is the container path — use it as-is when adding the library in Jellyfin.

**Libraries are empty**
- Run **Sync Trakt Content** after adding the libraries; it fills the folders and scans them.
- Check the user's sync options on the *Trakt* tab (*Sync Movie Recommendations* etc.).
- With *Ignore Collected Items* on, titles you own are hidden. If you own most recommendations, raise the limits or turn the filter off.
- Next Seasons needs watch progress on Trakt. Shows whose next season has not aired, or is already in Jellyfin, are not listed. With *Only Newly Released Seasons* on, only seasons inside the release window are listed.
- Check the task's log for Trakt errors. `401`/authentication errors mean the user needs to re-link (or, in shared mode, the official Trakt plugin's token has expired).

**Content looks stale**
- Content changes only when **Sync Trakt Content** runs (startup and every 6 hours by default). Run it manually, or shorten its interval in **Dashboard → Scheduled Tasks**.

**Library scans are slow**
- Keep *Limit Shows to Season 1 Only* on; otherwise every aired season of every recommended show gets a placeholder.
- Lower the recommendation limits.

## Trakt linking

**Authorization stays on "Waiting for authorization..."**
- Enter the code at <https://trakt.tv/activate> while signed in to the Trakt account you want to link. Codes expire; press **Link Trakt Account** again for a new one.
- On a free Trakt account that already has the official Trakt plugin connected, the second app is rejected. Use [the shared connection](usage.md#shared-trakt-connection-free-trakt-accounts).

**Syncs stop working after a while in shared-token mode**
- With *Let JellyNext refresh the shared token* off, an expired token is only renewed when the official Trakt plugin makes a call. Turn the option on.

**Token refresh fails**
- Check that the server clock is correct.
- Unlink (**Unlink Trakt Account**) and link the user again.

## Requests and downloads

**Pressing Play does nothing**
- Search the log for `Detected playback attempt for virtual item`. If it is missing, the item is not under a `jellynext-virtual` folder.
- `Movie not found in cache` / `Show not found in cache`: the content cache has expired or the sync has not run since startup. Run **Sync Trakt Content**, and keep *Content Cache Expiration* at least as long as the sync interval.
- Use **Test Connection** on the *Downloads* tab and check the backend's own log.

**Native mode: request fails**
- Quality profile and root folder must be selected for both Radarr and Sonarr (they appear after a successful **Test Connection**).
- URLs must be reachable from the Jellyfin server (in Docker, use service names like `http://radarr:7878`).

**Jellyseerr mode: request fails or user not imported**
- Jellyseerr must use this Jellyfin server as its media server, otherwise users cannot be imported.
- The request may be waiting for approval in Jellyseerr.
- With manual configuration, select a server and profile for both Radarr and Sonarr.

**Anime does not go to the anime folder/profile**
- Anime is detected only from Trakt's "anime" genre.
- Native: set *Anime Root Folder*. Jellyseerr: untick *Use Jellyseerr Default Config* for Sonarr and set *Anime Quality Profile*.

**Playback does not stop**
- Some clients ignore the server's stop command. Stop manually; the request was already sent. Increase *Playback Stop Delay*, or keep *Use Short Dummy Video* on so playback ends by itself.

**Webhook problems** — see [Webhooks → Troubleshooting](webhooks.md#troubleshooting).

## Widget, badge and icon (web client)

**Nothing appears**
- Reload the web client with Ctrl+F5 (browsers cache `index.html`).
- Search the log for `Could not update the web client's index.html`: Jellyfin needs write access to its web directory. Read-only web roots (some Docker setups, packages where the web files belong to root) cannot show these features.
- Native apps (Android TV, iOS, Roku, Kodi) cannot load plugin scripts. Only the tag shows there.
- The widget hides when the user has no next seasons. It needs *Sync Next Seasons* for that user.
- Badge and icon require *Tag JellyNext items* as well.

**Cards show only the show's name**
- *Widget* tab → **Check Artwork** shows, per card, what was found in your library, your metadata providers and Trakt. The log says `No artwork available for <title> season <n>` when every source was empty.

**Request in the widget fails**
- It uses the same backend as playback — see [Requests and downloads](#requests-and-downloads).

**An item still shows "Request" after navigating to a normal title**
- Reload the page. The web client reuses page elements, and the script can occasionally miss a change.

## Modular Home

**The section does not appear**
- Each user must enable it in their own Modular Home settings. This is the most common cause.
- The *Widget* tab's status line must report Modular Home as detected. The log shows `Registered the 'jellynext-new-seasons' section with Modular Home` a few seconds after startup.
- The section shows items from the Next Seasons library, so that library must exist and be scanned.

**No Request button on the cards**
- Enable *Add a Request button to the Modular Home cards* and reload with Ctrl+F5. If a Modular Home update changed its markup, the card's Play button still requests the season.

**Cards are landscape** — Modular Home draws third-party sections that way; change it in Modular Home's settings.

## Tags

**The tag is missing**
- Items get the tag after they are scanned into a library; run **Sync Trakt Content**. The log reports `Applied the <tag> tag on <n> virtual library items` (n counts only changed items).

**Items no longer from JellyNext still carry the tag**
- Save the configuration once; each pass removes the tag from items outside the JellyNext folders.

## Email

**Test email fails**
- The test uses the **saved** settings — save first.
- Port 465 (implicit TLS) is not supported; use 587 with *Use STARTTLS*.
- Many providers require an app password and a *From Address* matching the account.

**No emails arrive**
- Requires: master switch on, the user's *Email Me About New Seasons* and address set, *Sync Next Seasons* on.
- Only seasons first aired within *Announce Seasons Released Within* are sent, and each only once. Emails go out at the end of **Sync Trakt Content**.

## FAQ

**Do I need Trakt VIP?** No. Free accounts work; see the shared connection if you also use the official Trakt plugin.

**Does JellyNext download things on its own?** Only with watchlist auto-add enabled. Otherwise nothing is requested until someone presses Play or Request.

**Can I use it without Radarr/Sonarr/Jellyseerr?** Yes, for browsing. Requests fail until a backend is configured.

**Which integration should I choose?** Jellyseerr for multi-user servers with approvals and request tracking; Native for direct Radarr/Sonarr control (quality profile, root folder, anime folder); Webhooks for anything else.

**Does it change my real libraries?** No. It only writes inside its own `jellynext-virtual` folder and, if enabled, tags items in the JellyNext libraries.

**Does it write to my Trakt account?** No. Apart from OAuth (linking and token refresh) it only reads from Trakt and never scrobbles.

**What is the difference between Show Recommendations and Next Seasons?** Show Recommendations are new shows Trakt suggests. Next Seasons is the next unwatched season of shows you already watch.
