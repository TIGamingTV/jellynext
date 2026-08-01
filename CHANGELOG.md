# Changelog

## v1.9.7.0

### Bug Fixes

- **A season that was still airing counted as new forever, whatever the release window was set to**
  - "Only Newly Released Seasons" passed any season part-way through its run without looking at a date at all. A weekly show therefore stayed in Next Seasons — and in the widget — for its entire run, and shortening the window to a few days did nothing, so a show you had decided to skip could not be aged out
  - The window is now measured from the season's most recent release: its premiere, or the latest episode to have aired for a season still running. Long and split-cour seasons still stay in scope while they are airing, which is what the airing rule was for, but they now leave once they stop — and a narrow window means what it says. The same fix applies to new season emails, which share the rule
  - Trakt does not report when a season's last episode aired, so it is placed from the premiere and the aired episode count assuming a weekly cadence. Where that estimate lands in the future the episodes went out faster than weekly — a batch drop with later parts still listed — and the premiere is used instead, so those seasons age out normally too

### Improvements

- **Changing a user's Next Seasons filter now queues a content sync immediately.** The library and the widget are both built from cached content, so a narrowed window previously took up to six hours to show any effect, which read as the setting having done nothing

## v1.9.6.0

### Bug Fixes

- **Widget cards showed no picture at all in the desktop app, and an episode still in the browser**
  - The cards took their width and shape from Jellyfin's own card classes. Where a client does not apply them the card collapses to the width of its title and the artwork, which is sized as a percentage of that width, disappears entirely — leaving a column of bare titles. The widget now sets its own card width and aspect ratio, and only inherits the client's theme (fonts, colours, corners, hover), so the row looks the same everywhere
  - Artwork preferred a backdrop, then a thumbnail, then a poster. A series thumbnail is frequently a scene still, which is why cards looked like they were showing a random episode. Cards are now portrait — what is being offered is a season, and a season's picture is a poster wherever there is one — and posters are preferred over the 16:9 images

### Improvements

- **Cards now show the season's own artwork where Jellyfin has it**, instead of always showing the show's. The image endpoint resolves, in order: the season as a library item (with "display missing episodes" on, Jellyfin already holds the provider's poster for a season you have not downloaded); the season from your metadata providers, which is where a just-premiered season's poster comes from; the show in the library; the show from your metadata providers; and Trakt
- **Library artwork is answered with a redirect rather than copied through the plugin**, so it goes through Jellyfin's own resizing and caching like every other picture on the page. Artwork from outside Jellyfin is still served by the plugin, which is what makes it reachable behind a strict `img-src` policy
- **"Check Artwork" now reports the season as well as the show** — whether Jellyfin knows the season, which images it holds, and which step of the chain the picture actually came from

## v1.9.5.0

### Bug Fixes

- **Widget cards could show only the show's name while the log said nothing at all**
  - The card was redirected to the image host, so anything that stops the *browser* reaching it — a reverse proxy sending `Content-Security-Policy: img-src 'self'`, an ad blocker, filtered DNS — produced a blank card while the server saw a perfectly successful lookup and logged nothing. Jellyfin now serves the artwork itself, the same as it does for library images, so the picture is exactly as reachable as everything else on the page
  - Artwork that has disappeared since it was resolved is dropped from the cache instead of being served as nothing for a week
  - A show with no artwork source at all — not in the library and no Trakt ID — now logs a warning naming the show and its IDs. That state produces a name-only tile without a single request being made, so it previously left no trace anywhere

### Improvements

- **A "Check Artwork" button on the Widget tab** reports, per card, whether the show was matched in your library, which images that library item holds, the paths the card loads, and what your metadata providers and Trakt resolved to. A blank card is otherwise almost invisible to diagnose, since a missing image path means no request is ever made

## v1.9.4.0

### Bug Fixes

- **Artwork was handed to the browser without checking it loads.** A metadata entry with no file path still produces a well-formed URL, which then 404s — redirecting the card to it wasted the card, because by that point the remaining sources were out of reach. Candidates are now checked in order until one answers, at a cost of one request per show per week, and the log says at debug level which source a show's picture came from

## v1.9.3.0

### Bug Fixes

- **Seasons already in the library were offered again**, in the widget, the Next Seasons library and the watchlist sync
  - All three asked "do I already have this show" by TVDB ID only, so a show Jellyfin identified through another provider was treated as absent — the same lookup gap behind the missing artwork. They now match on TVDB, TMDB or IMDB
  - Expect the Next Seasons library and widget to shrink after the next sync if you have TMDB-matched shows: what disappears is seasons you already have

### Improvements

- **Widget artwork now comes from Jellyfin's own metadata providers**, the same source as every other image on the home screen
  - Shows the library has no wide artwork for — and shows not in the library at all — used to fall straight through to Trakt, whose images are a different quality and style to the rest of the page. The image endpoint now asks Jellyfin's configured providers first (TMDB, TheTVDB, whatever the server uses), preferring a backdrop, then a thumbnail, then a poster, and only falls back to Trakt when they have nothing
  - A show in the library is looked up as its real library item, so the providers see the server's metadata language and library options. A show not in the library is looked up by its IDs, which is all the image providers need
  - A library poster is no longer preferred over a wide image from the providers: for a 16:9 card the poster is the worse picture, so it becomes the fallback the card uses if the provider lookup comes back empty
  - Lookups stay lazy, behind the image request, and are cached for 7 days (12 hours for a show with no artwork), so a row of twelve cards never becomes twelve metadata lookups before anything is drawn

## v1.9.2.0

### Bug Fixes

- **Widget cards showed a blank tile or an episode still instead of the show's artwork**
  - The library lookup only matched shows by their TVDB ID, so a show Jellyfin identified through a different provider — anime matched by TMDB is the common case — looked absent from the library, and its artwork was fetched from Trakt instead of taken from the images already on the server. Shows are now matched on TVDB, TMDB or IMDB
  - Artwork preference is now Backdrop, then Thumb, then poster. Both of the first two are 16:9, but a series thumbnail is often a scene still that reads as "some episode" rather than as the show; a backdrop is always key art. On Trakt the same reasoning puts fanart first and its screenshot-style "thumb" last
  - A poster is no longer cropped into a strip to fill a 16:9 card: portrait artwork is shown whole inside the card
  - If the library image fails to load, the card now retries with Trakt's artwork before giving up
  - When there is genuinely no artwork anywhere, the tile shows the show's name instead of the first letter of it, and the reason is written to the log once per show rather than left invisible

## v1.9.1.0

### Improvements

- **The New Seasons widget now looks like a native home screen row.** It previously drew its own small cards, hard against the left edge of the page, which sat oddly next to Continue Watching and Next Up
  - Cards are built from Jellyfin's own card markup, so they are the same size, shape (16:9) and alignment as the rows around them in whatever theme and viewport the user has. Reimplementing that means copying a stack of viewport media queries and getting it subtly wrong; if those classes ever disappear, a probe detects it and a self-contained fallback layout takes over
  - Artwork follows suit: the show's thumbnail or backdrop from the Jellyfin library, falling back to its poster, and to Trakt's fanart for shows not in the library. Previously the poster was always used, which had to be cropped into a wide card
  - The season is now named in the card text ("Season 2 · 2022 · 4 of 12 episodes") as well as shown as a badge on the artwork
  - **The widget now sits below the other home screen sections by default.** A plugin's row pushing Continue Watching down the screen is a poor first impression of an opt-in feature. Existing installations keep whatever they have saved; the setting is on the Widget tab
  - The row is put back in place when Jellyfin adds a section after it. Home sections load one at a time, so a row placed at the bottom could end up in the middle a moment later

## v1.9.0.0

### Features

- **New Seasons home screen widget** (opt-in): a row on the Jellyfin home screen listing the shows the signed-in user has a new season of, each with a **Request** button
  - Answers the same question the Next Seasons virtual library does without the playback workaround it depends on. Playing a stub file to trigger a download only exists because third-party clients offer nothing better; in the web interface a button is simply a button
  - Each card shows the poster, season number, show name, year and the season's episode count — a season still airing reads "6 of 12 episodes"
  - Posters come from the Jellyfin library when the show is already in it, which it usually is since the user watched an earlier season, and from Trakt otherwise. Shows with no artwork anywhere get a plain tile rather than a broken image
  - Requests go through whichever download integration is configured (Radarr/Sonarr, Jellyseerr or a webhook), attributed to the user who pressed the button — the same path playback takes
  - Reads the content the Next Seasons sync already cached, so the widget and the library can never disagree about what counts as a new season, the per-user "Recently Released Seasons Only" filter applies to both, and the widget costs no additional Trakt requests
  - New **Widget** tab configures the heading, how many shows the row holds (1-50, default 12) and whether it sits above or below Jellyfin's own home screen sections
  - Jellyfin has no supported way for a plugin to add code to the web interface, so enabling the widget adds one script tag to the web client's `index.html`. The tag is rewritten on every start, because a server upgrade replaces that file, and removed again when the setting is switched off. A read-only web directory means the widget does not appear and a warning is logged; nothing else is affected
  - The web interface only. Native client apps cannot load plugin scripts, so they keep using the virtual library, which is unchanged

## v1.7.0.0

### Features

- **New season email notifications** (opt-in, per user): emails a user when a new season of a show they watch is released
  - New **Notifications** tab holds the SMTP settings and a **Send Test Email** button; each user then ticks **Email Me About New Seasons** on the Trakt tab and gives an address, since Jellyfin accounts have no email address of their own
  - Announcements ride on the Next Seasons sync, so a season is only announced under the conditions that put it in the user's library: it is the next season they have not watched, it has aired, and it is not already in Jellyfin. No extra Trakt requests are made
  - Only genuine releases are announced. A next season also appears whenever watch progress moves, so finishing a show that ended years ago would otherwise send mail about a decade-old season; a season qualifies only if it premiered inside `NewSeasonNotificationWindowDays` (default 30) or is part-way through airing. That window is separate from the per-user "New Release Window" that filters the library itself
  - Everything found in one sync goes out as a single digest listing each show, season and premiere date, in plain text and HTML
  - Sent announcements are recorded in the plugin configuration, so a restart does not repeat them and a season airing over several months is not announced twice. Records are dropped after 400 days. A failed send records nothing and is retried on the next sync
  - Sends over SMTP with STARTTLS or unencrypted. Implicit SSL (port 465) is not supported — a mail library cannot be added without also shipping copies of Jellyfin's own assemblies, which would break plugin loading. Providers offering 465 practically always offer 587

### Bug Fixes

- **Saving the configuration page always failed** with `The JSON value could not be converted to TraktAuthMode`, so no setting made on the page was ever persisted
  - Jellyfin serializes enums as their member name, so the saved configuration comes back as `"Standalone"` rather than `0`. Assigning that to the authorization mode dropdown, whose options are numeric, matched nothing and silently blanked the selection. The blank was then saved back as `NaN`, which serializes to `null`, and the server rejects `null` for a non-nullable enum — failing the entire request, including every unrelated setting in it
  - The mode is now normalized from either form on load and can never be posted as `NaN`. The Downloads tab already handled the string form, which is why its integration mode was unaffected
  - Number fields that feed non-nullable settings (cache expiration, playback stop delay, recommendation limits) fall back to their defaults instead of posting `null` when left empty, which failed the same way

### Improvements

- **One definition of "newly released season"**: the rule behind the per-user library filter moved into `SeasonReleaseHelper`, shared with the notifications, so the library and the emails cannot drift apart on what counts as new
- **The test email says what to do when SMTP looks unconfigured**: the message now points out that sending uses the saved settings, so unsaved changes on screen do not count

## v1.5.1.0

### Bug Fixes

- **Watch progress never picked up seasons marked watched on Trakt**: Next Seasons kept suggesting a season the user had already finished, or nothing at all
  - After the first run, progress was only advanced from `/sync/history/shows` between the last sync and now. That window only matches episodes whose `watched_at` falls inside it, and marking a whole season watched records it against the original air dates, so the history came back empty and progress stayed where it was ("Found 0 history items", "0 next season recommendations")
  - The in-memory sync timestamp meant the only way out was restarting Jellyfin, which forced a full sync
  - Progress is now read from `/sync/watched/shows`, Trakt's authoritative snapshot, on every run. The per-show season lookup - the expensive part - is still only paid for shows the cache has not seen or whose progress moved, so the steady-state request count is unchanged
  - Progress is also set rather than merged with the previous value, so unmarking a season no longer pins a show to a season it is past
  - Each show whose progress changes is logged (`Watch progress for X: S2 -> S4`), and the run reports how many shows are tracked, how many moved, and how many season lookups it made

- **Seasons you do not have counted as downloaded**: `GetLocalSeasons` accepted Jellyfin's placeholder Season entities
  - With "display missing episodes" enabled, Jellyfin materialises a Season for every season the metadata knows about, not just the ones on disk. Those placeholders made `DoesSeasonExist` return true for a season that was never downloaded, so Next Seasons skipped it as "already in the Jellyfin library" and removed its stub
  - The query now filters on `IsVirtualItem = false`, so only seasons actually present count

- **Watchlist items were re-requested every hour**: nothing tracked what had already been sent
  - `ProcessedWatchlistMovieIds` and `ProcessedWatchlistShowIds` were declared on `TraktUser` and documented as the deduplication mechanism, but no code ever read or wrote them. Every watchlisted item that had not finished downloading was re-sent to Radarr/Sonarr/Jellyseerr on every run, producing duplicate-request errors in the log
  - `WatchlistSyncService` now tracks requested items in memory per user, rebuilt from the current watchlist each run. Removing a title from Trakt drops it from the set, so adding it back requests it again - which a persisted "processed" list would have silently swallowed
  - The dead configuration fields were removed, along with a `SaveConfiguration()` call that existed only to persist them (token refreshes save themselves)

- **Ended shows were never re-read from Trakt**: a revival season stayed invisible for as long as Jellyfin kept running
  - `NextSeasonsProvider` only queries Trakt on demand for shows that have not ended, and the sync only refetched seasons when watch progress moved, so a show cached as ended was frozen until a restart dropped the in-memory cache
  - Season metadata is now re-read when it is older than 7 days even if nothing else changed, which also refreshes the show's status so a returning show starts caching its incomplete seasons again

### Improvements

- **Next Seasons reports why shows produced nothing**: an empty library previously gave no explanation at the default log level, since every skip was a debug-only message
  - The run summary now counts each outcome — no aired next season, hidden by the new-release filter, already in the Jellyfin library — instead of only reporting how many suggestions were found
  - When the new-release filter hides seasons, up to ten are named with their premiere date and the active window, which distinguishes "correctly filtered old backlog" from a window that is too narrow or metadata Trakt did not return

## v1.5.0.0

### Features

- **Only Newly Released Seasons** (opt-in, per user): restricts Next Seasons to seasons that have just come out
  - Next Seasons suggests the next unwatched season of every partially watched show, so a show that ended years ago sits in the library alongside a season that premiered last week. With the filter on, the library answers "what's new for shows I watch" instead of "what haven't I finished"
  - New per-user settings `NextSeasonsRecentOnly` (default off, so existing behaviour is unchanged) and `NextSeasonsRecentDays` (default 90, 1-3650)
  - A season part-way through its run always counts as new whatever its premiere date says, which keeps long and split-cour seasons visible past the cut-off. Ended and canceled shows are excluded from that rule, since their unaired episode counts are cancellation leftovers rather than an ongoing release
  - A season with no premiere date from Trakt is treated as not recent — the filter excludes by default rather than letting undated backlog seasons through

## v1.4.1.0

### Bug Fixes

- **Next Seasons was always empty**: `GetWatchedShows` still called `/sync/watched/shows?extended=full`
  - Trakt changed the watched endpoints on 2026-07-03 ([trakt/trakt-api#775](https://github.com/trakt/trakt-api/discussions/775)): season progress is no longer returned by default, `noseasons` became the default and is now a no-op, and `extended=progress` must be requested explicitly
  - Without a `seasons` array, `GetHighestWatchedSeason` found nothing for every show, so watch progress stayed empty and `NextSeasonsProvider` had nothing to iterate. The sync reported success with zero items and logged no errors
  - Now requests `extended=full,progress`. Bare `progress` returns a minimal show object with no `status` and no `genres`, which would silently break ended-show detection and anime routing
  - Also paginates. A request without pagination parameters now returns only page 1, capped at 100 items, so libraries with more than 100 watched shows were being truncated

### Improvements

- **Degraded responses are no longer silent**: this failure produced no errors at any layer
  - `PerformFullSync` warns when watched shows were fetched but none carried season progress, and separately when none carried a show status, naming the likely cause in each case
  - A full sync that returned shows but established no watch progress no longer advances the incremental-sync timestamp. Previously one bad full sync sent every later run down the incremental path, which only sees newly watched episodes, so the gap never closed and recovery required restarting Jellyfin to clear the in-memory timestamp

## v1.4.0.0

### Features

- **Shared Trakt Connection**: Work around Trakt's free-tier limit of one connected community app per account
  - New `TraktAuthMode` setting with three modes: `Standalone` (default, unchanged behaviour), `SharedTraktPluginToken`, and `SharedClientId`
  - `SharedTraktPluginToken` reads the per-user access token held by the official Jellyfin Trakt plugin and presents that plugin's client ID, so Trakt only ever sees one connected app with one token
  - `SharedClientId` presents the official plugin's client ID while JellyNext authorizes its own token (experimental — Trakt's behaviour when the same client ID is authorized twice is unverified)
  - New `TraktPluginBridge` service reads and writes the official plugin's live in-memory configuration by reflection, matched on `LinkedMbUserId`. Jellyfin loads each plugin into its own `AssemblyLoadContext`, so reflection (not an assembly reference) is required; the plugin is located through `IPluginManager` and reached via the shared `IHasPluginConfiguration` abstraction
  - New `POST /JellyNext/Trakt/Users/{userGuid}/Link` registers a user for shared-token mode without a device authorization flow, and `GET /JellyNext/Trakt/SharedStatus` reports whether the Trakt plugin is present and which users it has linked
  - Configuration UI: authorization mode selector, Trakt plugin detection status, and mode-appropriate linking controls

- **Shared Token Refresh**: `AllowSharedTokenRefresh` (default enabled) lets JellyNext refresh a borrowed token and write the rotated pair back into the official plugin's live configuration
  - Trakt refresh tokens are single-use and rotate on every refresh, so the refresh is skipped entirely unless the rotated pair can be written back — otherwise JellyNext would consume the refresh token and leave the official plugin holding a dead one
  - Guarded by a per-user lock so parallel providers cannot refresh concurrently
  - Never writes `Trakt.xml` directly; the file would be overwritten by the official plugin's next save

### Improvements

- **Auth Failure Handling**: New `TraktAuthenticationException` distinguishes "cannot authenticate" from "no content"
  - `ContentSyncService` now skips the provider's cycle and leaves cached content intact instead of overwriting it with an empty result, which previously tore down a user's virtual library on a transient token problem
  - Trakt 401 responses are detected explicitly and never trigger a re-authorization attempt in shared mode
  - `WatchlistSyncService` logs auth failures as skipped cycles rather than errors

- **Provider Gating**: `IsEnabledForUser` now checks for a usable token under the active authorization mode rather than only JellyNext's own stored token

## v1.3.0.0

### Features

- **Watchlist Sync**: Automatically add Trakt watchlisted items to download systems (Radarr/Sonarr/Jellyseerr)
  - New per-user settings: `SyncWatchlistMovies` and `SyncWatchlistShows` toggles in Trakt user configuration
  - Fetches movies via `/sync/watchlist/movies` and shows via `/sync/watchlist/shows` with `extended=full` for genre metadata
  - Filters out items already in local Jellyfin library and previously processed items
  - Routes downloads through existing `DownloadProviderFactory` (supports Native, Jellyseerr, and Webhook modes)
  - Shows default to Season 1 download (Trakt watchlist doesn't specify seasons)
  - 1-second throttle between downloads to avoid overwhelming download systems
  - Individual item failures logged without stopping the sync process

- **Watchlist Sync Scheduled Task**: Background task running every 1 hour
  - More frequent than content sync (6hr) to respond quickly to watchlist changes
  - Also triggered on startup via `StartupSyncService`

### Improvements

- **State Tracking**: Persistent tracking of processed watchlist items to prevent re-adding
  - `ProcessedWatchlistMovieIds` (TMDB IDs) and `ProcessedWatchlistShowIds` (TVDB IDs) persisted in configuration
  - Reset manually by clearing IDs from config to re-trigger downloads

- **Local Library Deduplication**: New `DoesMovieExist()` method in `LocalLibraryService` to check movie existence by TMDB ID (excludes virtual items)

- **Trakt API**: New watchlist endpoints
  - `GetMovieWatchlist()`: Fetch user's movie watchlist
  - `GetShowWatchlist()`: Fetch user's show watchlist

- **Configuration UI**: Added watchlist sync toggles to Trakt user settings tab

### Acknowledgments

- Thanks to [@medallyon](https://github.com/medallyon) for implementing the watchlist sync feature

## v1.2.1.1

### Bug Fixes

- **Trending movies downloads**: Fixed download failure when playing trending movies
  - PlaybackInterceptor now correctly handles global content paths (`jellynext-virtual/global/movies_trending/`)
  - Detects global content types and uses configured `TrendingMoviesUserId` for cache lookup
  - Previously failed because path parsing expected per-user GUID, not "global" keyword

## v1.2.1.0

### Improvements

- **Modular Configuration UI**: Refactored configuration page into maintainable tab-based architecture
  - Split 1843-line monolithic `configPage.html` into modular components (317-line main shell + 4 tab files)
  - Each tab isolated: `general.html/js`, `trakt.html/js`, `trending.html/js`, `downloads.html/js` (~40-865 lines each)
  - New `ConfigController` serves tab resources from embedded resources via REST endpoints
  - Eager loading strategy ensures all tabs loaded before population
  - Proper async load order prevents race conditions (load tabs → config → users → populate UI)

### Bug Fixes

- **Configuration save errors**: Fixed JSON deserialization errors when saving with unconfigured Radarr/Sonarr
  - Changed `RadarrQualityProfileId` and `SonarrQualityProfileId` from `int` to `int?` (nullable)
  - Added validation in `RadarrService` and `SonarrService` before using profile IDs
  - Fixed JavaScript to properly handle empty select values (avoid `parseInt("")` → `NaN`)
- **Race condition in trending settings**: Fixed issue where user selection was lost on page reload
  - `loadUsers()` now returns promise to ensure dropdown is populated before setting selected value
  - Prevents accidental overwrite of `TrendingMoviesUserId` with null

### Technical

- Updated CLAUDE.md with configuration page architecture patterns and load order requirements

## v1.2.0.0

### Features

- **Download Integration Modes**: Complete overhaul of download system with three pluggable integration modes
  - **Native Mode** (default): Direct Radarr/Sonarr API integration for granular control
  - **Jellyseerr Mode**: Centralized request management with approval workflows and multi-user tracking
  - **Webhook Mode**: Custom HTTP webhooks for complete flexibility and external system integration
  - Factory pattern (`DownloadProviderFactory`) selects provider based on configuration
  - All modes support anime detection and per-season TV downloads

- **Jellyseerr Integration**: Full integration with Jellyseerr request management system
  - **Automatic user import**: Jellyfin users auto-imported to Jellyseerr on first request with REQUEST-only permissions
  - **Per-user attribution**: All requests tracked per-user via `X-Api-User` header
  - **Approval workflows**: Respects Jellyseerr approval settings and request quotas
  - **Configuration modes**:
    - Default mode: Uses Jellyseerr's default Radarr/Sonarr server and profile settings
    - Manual mode: Explicit server and profile selection from UI dropdowns
  - **Server/profile selection**: UI dynamically loads Radarr/Sonarr servers and quality profiles from Jellyseerr
  - **Anime support**: Optional separate anime profile for TV shows detected via Trakt genres
  - **GUID normalization**: Handles UUID format differences between Jellyseerr (no hyphens) and Jellyfin (standard format)
  - **Connection testing**: Test Jellyseerr connection and validate API key from plugin settings
  - New service: `JellyseerrService` for API communication
  - New controller: `JellyseerrController` for UI configuration
  - New models: `MediaRequest`, `JellyseerrUser`, `RadarrServer`, `SonarrServer`, `QualityProfile`

- **Webhook Integration**: Custom HTTP webhook support for external integrations
  - **Flexible HTTP methods**: GET, POST, PUT, PATCH support
  - **Dynamic placeholders**: Replace variables in URLs, headers, and payloads
    - Movies: `{tmdbId}`, `{imdbId}`, `{title}`, `{year}`, `{jellyfinUserId}`
    - TV Shows: All movie placeholders + `{tvdbId}`, `{seasonNumber}`, `{isAnime}`
  - **Custom headers**: Add authentication, API keys, or any custom headers with placeholder support
  - **JSON payload templates**: Fully customizable request body with default templates included
  - **Separate configurations**: Independent settings for movies vs TV shows
  - **Use cases**: Discord/Slack notifications, custom download systems, third-party automation
  - New service: `WebhookDownloadProvider` implementing `IDownloadProvider`
  - New configuration fields: `WebhookMovieUrl`, `WebhookShowUrl`, `WebhookMethod`, `WebhookMovieHeaders`, `WebhookShowHeaders`, `WebhookMoviePayload`, `WebhookShowPayload`
  - New model: `WebhookHeader` for custom header configuration
  - Comprehensive documentation: New `WEBHOOK.md` guide with examples and troubleshooting

### Improvements

- **Download Provider Architecture**: Pluggable provider system for extensibility
  - `IDownloadProvider` interface defines contract for all download providers
  - `NativeDownloadProvider`: Implements direct Radarr/Sonarr integration
  - `JellyseerrDownloadProvider`: Implements Jellyseerr API integration
  - `WebhookDownloadProvider`: Implements custom HTTP webhook integration
  - `DownloadProviderFactory`: Factory pattern selects provider based on `DownloadIntegration` enum
  - `PlaybackInterceptor` uses factory to route download requests

- **Configuration UI Enhancements**: Redesigned download integration section
  - **Visual integration selector**: Card-based UI with icons and descriptions for each mode
  - **Dynamic sections**: UI shows/hides relevant settings based on selected integration mode
  - **Webhook UI**: Interactive payload editors with placeholder insertion buttons
  - **Header management**: Add/remove custom headers dynamically for webhook requests
  - **Payload reset**: Quick reset to default payload templates
  - **Real-time validation**: Client-side validation for URLs, headers, and JSON payloads

- **Documentation**: Comprehensive guides for all integration modes
  - Updated README.md with three integration modes in setup guide
  - New WEBHOOK.md with complete webhook integration guide including:
    - Configuration examples (GET, POST, authentication, Discord/Slack)
    - Building custom webhook endpoints (Python Flask, Node.js Express)
    - Troubleshooting guide
    - Security considerations
    - Advanced usage patterns
  - Updated CLAUDE.md with download provider architecture
  - FAQ updates covering all three integration modes

### Technical Changes

- **New enum**: `DownloadIntegrationType` with values: `Native = 0`, `Jellyseerr = 1`, `Webhook = 2`
- **Configuration migration**: Changed from `UseJellyseerr` boolean to `DownloadIntegration` enum (backward compatible)
- **Service registration**: All download providers registered in `PluginServiceRegistrator`
- **API Controllers**:
  - `JellyseerrController`: Connection testing, server/profile retrieval
  - Enhanced error handling and logging across all providers
- **Models**:
  - `MediaRequest`: Jellyseerr request model with media IDs and user tracking
  - `JellyseerrUser`: User model for auto-import functionality
  - `RadarrServer` / `SonarrServer`: Server configuration models
  - `WebhookHeader`: Custom header model for webhook requests
- **HTTP Client**: All providers use `NamedClient.Default` for Cloudflare bypass compatibility

## v1.1.2.0

### Improvements

- **Configuration UI Redesign**: Complete overhaul of plugin settings interface with native Jellyfin tab styling
  - **Tab-based layout**: Organized settings into 4 tabs (General, Trakt, Trending, Downloads)
  - **Native Jellyfin styling**: Uses `controlgroup` and `localnav` classes matching Jellyfin's UI patterns
  - **Unified save button**: Single save button now handles all settings including per-user Trakt configurations
  - **Improved UX**: Removed redundant "Save User Settings" button, cleaner tab navigation

- **Virtual Library Management**: Enhanced global content directory handling
  - **Automatic cleanup**: Trending movies directory now automatically flushed when feature is disabled
  - **Consistent state**: Prevents stale content from appearing in global libraries after configuration changes

## v1.1.1.0

### Improvements

- **Shows Cache Refactoring**: Complete overhaul of season-level caching system
  - **Hybrid architecture**: Global show/season metadata cache + per-user watch progress tracking
  - **Incremental sync**: History-based delta syncing via `/sync/history/shows` endpoint reduces API load
  - **Smart caching**: Ended shows cache all seasons immediately, ongoing shows only cache complete seasons
  - **Automatic sync mode**: `PerformIncrementalSync()` automatically detects first run and falls back to full sync
  - **In-memory timestamps**: Last sync timestamp no longer persisted to config (triggers full sync on restart for data freshness)
  - **Zero duplicate API calls**: Both RecommendationsProvider and NextSeasonsProvider read from same cache
  - **Progressive discovery**: As users watch episodes, incremental sync detects progression and triggers next season recommendations

- **Next Seasons Provider Enhancement**: Improved reliability and efficiency
  - **Sync-first approach**: Calls `ShowsCacheService.PerformIncrementalSync()` before fetching content
  - **Cache-only reads**: Retrieves watched progress + season metadata entirely from cache (no duplicate Trakt API calls)
  - **Dynamic fetching**: If next season not in cache for ongoing shows, fetches latest from Trakt API and checks season count
  - **Better library deduplication**: Uses LocalLibraryService to exclude shows already in Jellyfin library

- **Recommendations Provider Optimization**: Uses ShowsCacheService for season counts to avoid duplicate API calls

- **Configuration Simplification**: Removed `EndedShowsCacheExpirationDays` setting (no longer needed with new cache architecture)

### Technical Changes

- **New models**:
  - `ShowCacheEntry`: Global show/season metadata (Title, Year, IDs, Status, Genres, Seasons dictionary)
  - `SeasonMetadata`: Season-level data (SeasonNumber, EpisodeCount, AiredEpisodes, FirstAired, IsComplete property)
  - `TraktHistoryItem`: For parsing `/sync/history/shows` endpoint
  - `TraktEpisode`: Episode metadata for history items

- **Deleted files**:
  - `EndedShowsCacheService.cs` (replaced by `ShowsCacheService.cs`)
  - `EndedShowMetadata.cs` (replaced by `ShowCacheEntry.cs` + `SeasonMetadata.cs`)

- **API additions**:
  - `TraktApi.GetShowWatchHistory()`: Fetches watch history with automatic pagination support (100 items/page)
  - `TraktApi.GetWatchedShows()`: Enhanced with `extended=full` parameter for genre data

- **Service registration**: Updated `PluginServiceRegistrator` to use `ShowsCacheService` instead of `EndedShowsCacheService`

## v1.1.0.3

### Bug Fixes

- **Jellyfin 10.11.0 Compatibility**: Pin SDK to exact version 10.11.0 to ensure compatibility across all 10.11.x releases
  - Changed `Jellyfin.Controller` and `Jellyfin.Model` dependencies from `10.11.*` to `10.11.0`
  - Fixes `ReflectionTypeLoadException` on Jellyfin servers running 10.11.0 and 10.11.1
  - Plugin now works on Jellyfin 10.11.0+

## v1.1.0.2

### Documentation

- **Enhanced Setup Instructions**: Added detailed virtual library path discovery instructions
  - Included example log output showing exact paths for each content type
  - Clarified Docker path usage (`/config/data/plugins/Jellyfin.Plugin.JellyNext/jellynext-virtual/...`)
  - Added step-by-step guide for finding jellynext-virtual directory via Jellyfin logs after Trakt user configuration
  - Improved user experience for first-time setup

## v1.1.0.1

### Bug Fixes

- **Configuration Save Error**: Fix `System.FormatException` when saving configuration with trending movies disabled
  - `TrendingMoviesUserId` is now only included in configuration POST when trending is enabled and a valid user is selected
  - Prevents empty string from being parsed as GUID when trending movies is not configured

## v1.1.0.0

### Features

- **Trending Movies (Global)**: Added global trending movies feature visible to all users
  - New global content type: `MoviesTrending`
  - Non-personalized trending movies from Trakt
  - Configurable via Dashboard → Plugins → JellyNext → Trending Movies (Global)
  - Settings:
    - Enable/disable toggle
    - Source user selection (which Trakt account to use for API access)
    - Limit: 1-100 movies (default: 50)
  - Virtual library path: `jellynext-virtual/global/movies_trending`
  - Directory automatically created on plugin startup when enabled
  - Supports same one-click Radarr download functionality as per-user recommendations

### Improvements

- **Global Content Architecture**: Extended virtual library system to support both per-user and global content types
  - New helper method: `VirtualLibraryContentTypeHelper.IsGlobal()` to distinguish content types
  - `VirtualLibraryManager` now handles both per-user (`jellynext-virtual/[userId]/[content-type]/`) and global (`jellynext-virtual/global/[content-type]/`) paths
  - Automatic directory initialization for global content types
  - Setup instructions now include global libraries when enabled
- **Trakt API**: Added `GetTrendingMovies()` method to fetch trending movies with configurable limits
- **New Provider**: `TrendingMoviesProvider` implements `IContentProvider` for modular trending movies support
- **Documentation**: Updated CLAUDE.md and README.md with global content architecture and trending movies feature

## v1.0.3.0

### Features

- **Per-User Recommendation Limits**: Added configurable limits for movie and show recommendations (1-100, default: 50)
  - New settings: `MovieRecommendationsLimit` and `ShowRecommendationsLimit` in per-user configuration
  - Configurable via Dashboard → Plugins → JellyNext → User Settings
  - Validated on both client and server with `Math.Clamp()` to enforce 1-100 range
  - Each user can control how many recommendations they want to fetch

## v1.0.2.0

### Features

- **Short Dummy Video Option**: Added configurable 2-second dummy video for automatic playback stop on all clients
  - New setting: `UseShortDummyVideo` (default: enabled)
  - When enabled: Uses 2-second video that auto-stops playback even on clients without API support
  - When disabled: Uses 1-hour video (prevents "watched" status but requires manual stop)
  - Configurable via Dashboard → Plugins → JellyNext → Playback Settings
  - New embedded resource: `dummy_short.mp4` (~5KB vs ~2MB for long version)

### Improvements

- **Automatic Stub Refresh on Config Change**: Virtual library stub files now automatically rebuild when dummy video setting is changed
  - Validates stub file content matches current configuration on each sync
  - Flushes and rebuilds directory if mismatch detected
  - Ensures consistent experience across all virtual items
- **Better Client Compatibility**: Short dummy video provides automatic stop on clients that don't support Jellyfin's playback control API

## v1.0.1.0

### Features

- **Configurable Playback Stop Delay**: Added setting to configure delay before stopping playback of virtual items (default: 2 seconds, range: 0-30)
  - Allows users to adjust timing for clients that need more initialization time
  - Configurable via Dashboard → Plugins → JellyNext → Playback Settings

### Improvements

- **Reduced Default Playback Delay**: Changed default playback stop delay from 5 seconds to 2 seconds for faster user experience
- **Enhanced Documentation**: Added comprehensive "Playback Stop Behavior" section to README explaining:
  - How automatic playback stop works
  - Client compatibility information
  - Instructions for clients that don't support automatic stop
  - Clarification that download triggers immediately regardless of stop behavior

## v1.0.0.1

### Bug Fixes

- **Sonarr Integration**: Fix series monitoring update failure caused by missing `path` field in API requests

## v1.0.0.0

### Features

- **Per-User Trakt Integration**: OAuth 2.0 device flow authentication with automatic token refresh
- **Virtual Libraries**: Three dedicated libraries per user (Movie Recommendations, Show Recommendations, Next Seasons)
- **One-Click Downloads**: Automatic Radarr/Sonarr integration triggered by playback attempts
- **Per-Season TV Downloads**: Granular control to download specific seasons only
- **Anime Detection**: Automatic routing to separate Sonarr anime folder based on Trakt genres
- **Smart Caching**: Configurable content cache (6hr default) and ended shows cache (7 day default)
- **Per-User Settings**: Granular sync control (movies, shows, next seasons), content filtering (collected, watchlisted), performance options (season 1 limit)
- **Automatic Sync**: Background sync task (6hr interval) with startup sync (5s after start)
- **iOS/tvOS Compatibility**: FFprobe-compatible dummy video files prevent client crashes
- **Native Jellyfin Integration**: Standard .strm file naming with TMDB/TVDB metadata providers
- **Configuration UI**: Web-based admin interface for Trakt/Radarr/Sonarr setup and user management
