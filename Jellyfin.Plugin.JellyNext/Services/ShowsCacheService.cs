using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyNext.Models.Common;
using Jellyfin.Plugin.JellyNext.Models.Trakt;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyNext.Services;

/// <summary>
/// Service for caching TV shows with season-level metadata and per-user watch progress.
/// </summary>
public class ShowsCacheService
{
    // How long cached season metadata is trusted for a show whose watch progress has not moved.
    // Ended shows are otherwise never re-read: the provider only queries Trakt on demand for ongoing
    // shows, so without this a revival season would stay invisible until Jellyfin restarted and
    // dropped the in-memory cache.
    private static readonly TimeSpan SeasonMetadataMaxAge = TimeSpan.FromDays(7);

    private readonly ILogger<ShowsCacheService> _logger;
    private readonly TraktApi _traktApi;

    // Global cache: tvdbId -> ShowCacheEntry (shared metadata/seasons)
    private readonly ConcurrentDictionary<int, ShowCacheEntry> _showsCache;

    // Per-user watch progress: userId -> (tvdbId -> highest watched season)
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<int, int>> _userWatchProgress;

    /// <summary>
    /// Initializes a new instance of the <see cref="ShowsCacheService"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="traktApi">The Trakt API service.</param>
    public ShowsCacheService(ILogger<ShowsCacheService> logger, TraktApi traktApi)
    {
        _logger = logger;
        _traktApi = traktApi;
        _showsCache = new ConcurrentDictionary<int, ShowCacheEntry>();
        _userWatchProgress = new ConcurrentDictionary<Guid, ConcurrentDictionary<int, int>>();
    }

    /// <summary>
    /// Gets or creates the watch progress dictionary for a specific user.
    /// </summary>
    private ConcurrentDictionary<int, int> GetUserWatchProgress(Guid userId)
    {
        return _userWatchProgress.GetOrAdd(userId, _ => new ConcurrentDictionary<int, int>());
    }

    /// <summary>
    /// Syncs a user's watched shows, refreshing watch progress and caching season metadata.
    /// </summary>
    /// <param name="traktUser">The Trakt user configuration.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <remarks>
    /// Progress comes from <c>/sync/watched/shows</c>, which is the authoritative snapshot of what the
    /// user has watched, rather than from a history delta. A delta only sees episodes whose
    /// <c>watched_at</c> falls inside the polled window, so marking a whole season watched - which
    /// Trakt records against the original air dates - produced no history at all and left progress
    /// stuck at the season the user had before. Next Seasons then kept pointing at a season they had
    /// already finished, and only a Jellyfin restart (which clears the in-memory sync state) recovered.
    /// The snapshot costs one request per 100 shows; the per-show season lookup is the expensive part,
    /// so it is only paid for shows the cache has never seen or whose progress actually moved.
    /// </remarks>
    public async Task SyncWatchedShows(TraktUser traktUser)
    {
        _logger.LogInformation("Syncing watched shows for user {UserId}", traktUser.LinkedMbUserId);

        var watchedShows = await _traktApi.GetWatchedShows(traktUser);
        _logger.LogInformation("Found {Count} watched shows for user {UserId}", watchedShows.Length, traktUser.LinkedMbUserId);

        WarnOnDegradedWatchedShows(watchedShows);

        var userProgress = GetUserWatchProgress(traktUser.LinkedMbUserId);
        var progressChanges = 0;
        var seasonsFetched = 0;

        foreach (var watchedShow in watchedShows)
        {
            if (watchedShow.Show.Ids.Tvdb == null || watchedShow.Show.Ids.Tvdb == 0)
            {
                continue;
            }

            var tvdbId = watchedShow.Show.Ids.Tvdb.Value;

            try
            {
                var highestWatchedSeason = GetHighestWatchedSeason(watchedShow);
                var knownSeason = userProgress.TryGetValue(tvdbId, out var known) ? known : (int?)null;
                var progressMoved = highestWatchedSeason.HasValue && highestWatchedSeason != knownSeason;
                var cached = GetCachedShow(tvdbId);
                var stale = cached != null && DateTime.UtcNow - cached.CachedAt > SeasonMetadataMaxAge;

                if (progressMoved || cached == null || stale)
                {
                    await CacheShowWithSeasons(watchedShow.Show, traktUser);
                    seasonsFetched++;
                }

                if (highestWatchedSeason.HasValue)
                {
                    SetUserWatchProgress(traktUser.LinkedMbUserId, tvdbId, highestWatchedSeason.Value);
                    if (progressMoved)
                    {
                        progressChanges++;
                        _logger.LogInformation(
                            "Watch progress for {Title}: {Previous} -> S{Current}",
                            watchedShow.Show.Title,
                            knownSeason.HasValue ? $"S{knownSeason.Value}" : "not tracked",
                            highestWatchedSeason.Value);
                    }
                }
            }
            catch (TraktAuthenticationException)
            {
                // Surface auth failures so the caller skips the cycle instead of caching an empty result.
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to cache show {Title} (TVDB: {TvdbId})",
                    watchedShow.Show.Title,
                    tvdbId);
            }
        }

        if (watchedShows.Length > 0 && userProgress.IsEmpty)
        {
            _logger.LogWarning(
                "Fetched {Count} watched shows for user {UserId} but none carried season progress, so "
                + "Next Seasons will be empty. Trakt only returns season progress for "
                + "'/sync/watched/shows?extended=progress'; if this persists, the response shape has "
                + "changed again",
                watchedShows.Length,
                traktUser.LinkedMbUserId);
            return;
        }

        _logger.LogInformation(
            "Sync completed for user {UserId}: {Tracked} shows tracked, {Changes} with changed progress, "
            + "{Fetched} season lookups",
            traktUser.LinkedMbUserId,
            userProgress.Count,
            progressChanges,
            seasonsFetched);
    }

    /// <summary>
    /// Warns when a watched-shows response is missing data later stages depend on.
    /// </summary>
    /// <param name="watchedShows">The fetched watched shows.</param>
    /// <remarks>
    /// JellyNext asks for <c>extended=full,progress</c>, an undocumented combination. Should Trakt
    /// stop honouring it, the fallout would otherwise be invisible: a response with no season
    /// progress empties Next Seasons, and one with no show status makes every show look ongoing, so
    /// only complete seasons get cached and anime routing loses its genre data.
    /// </remarks>
    private void WarnOnDegradedWatchedShows(TraktWatchedShow[] watchedShows)
    {
        if (watchedShows.Length == 0)
        {
            return;
        }

        var withoutSeasons = watchedShows.Count(s => s.Seasons.Length == 0);
        if (withoutSeasons == watchedShows.Length)
        {
            _logger.LogWarning(
                "None of the {Count} watched shows returned a 'seasons' array. Trakt stopped sending "
                + "season progress by default on 2026-07-03; 'extended=progress' is required",
                watchedShows.Length);
        }

        var withoutStatus = watchedShows.Count(s => string.IsNullOrEmpty(s.Show.Status));
        if (withoutStatus == watchedShows.Length)
        {
            _logger.LogWarning(
                "None of the {Count} watched shows returned a 'status' field, so every show will be "
                + "treated as ongoing and anime detection has no genres to work with. "
                + "'extended=full,progress' may no longer return the full show object",
                watchedShows.Length);
        }
    }

    /// <summary>
    /// Caches a show with its seasons based on show status.
    /// This caches global metadata/seasons only, not user-specific watch progress.
    /// </summary>
    /// <param name="show">The Trakt show.</param>
    /// <param name="traktUser">The Trakt user configuration.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task CacheShowWithSeasons(TraktShow show, TraktUser traktUser)
    {
        if (show.Ids.Tvdb == null || show.Ids.Tvdb == 0)
        {
            return;
        }

        var tvdbId = show.Ids.Tvdb.Value;
        var isEnded = IsShowEnded(show);

        // Get all seasons from Trakt
        var traktSeasons = await _traktApi.GetShowSeasons(traktUser, show.Ids.Trakt);
        if (traktSeasons.Length == 0)
        {
            _logger.LogDebug("No seasons found for {Title} (TVDB: {TvdbId})", show.Title, tvdbId);
            return;
        }

        // Create or update cache entry (global)
        var cacheEntry = _showsCache.GetOrAdd(tvdbId, _ => new ShowCacheEntry
        {
            Title = show.Title,
            Year = show.Year,
            TmdbId = show.Ids.Tmdb,
            ImdbId = show.Ids.Imdb,
            TvdbId = show.Ids.Tvdb,
            TraktId = show.Ids.Trakt,
            Status = show.Status ?? "unknown",
            Genres = show.Genres ?? Array.Empty<string>(),
            CachedAt = DateTime.UtcNow
        });

        // Update show metadata. Refreshing the timestamp is what keeps a show off the staleness path
        // until the next interval; the status is refreshed with it, so a show that returns from
        // "ended" starts getting its incomplete seasons cached again.
        cacheEntry.Status = show.Status ?? "unknown";
        cacheEntry.Genres = show.Genres ?? Array.Empty<string>();
        cacheEntry.CachedAt = DateTime.UtcNow;

        // Cache seasons based on show status
        var cachedSeasons = 0;
        foreach (var season in traktSeasons.Where(s => s.Number > 0))
        {
            if (isEnded)
            {
                // For ended/canceled shows: cache all seasons
                CacheSeason(cacheEntry, season);
                cachedSeasons++;
            }
            else
            {
                // For ongoing shows: only cache complete seasons
                if (season.EpisodeCount > 0 && season.EpisodeCount == season.AiredEpisodes)
                {
                    CacheSeason(cacheEntry, season);
                    cachedSeasons++;
                }
                else if (cacheEntry.Seasons.ContainsKey(season.Number))
                {
                    // Update incomplete season if already in cache (e.g., newly aired episodes)
                    CacheSeason(cacheEntry, season);
                }
            }
        }

        _logger.LogDebug(
            "Cached {Title} (TVDB: {TvdbId}, Status: {Status}): {CachedSeasons}/{TotalSeasons} seasons",
            show.Title,
            tvdbId,
            show.Status ?? "unknown",
            cachedSeasons,
            traktSeasons.Count(s => s.Number > 0));
    }

    /// <summary>
    /// Caches a single season's metadata.
    /// </summary>
    /// <param name="cacheEntry">The show cache entry.</param>
    /// <param name="traktSeason">The Trakt season.</param>
    private void CacheSeason(ShowCacheEntry cacheEntry, TraktSeason traktSeason)
    {
        cacheEntry.Seasons[traktSeason.Number] = new SeasonMetadata
        {
            SeasonNumber = traktSeason.Number,
            EpisodeCount = traktSeason.EpisodeCount,
            AiredEpisodes = traktSeason.AiredEpisodes,
            FirstAired = traktSeason.FirstAired,
            CachedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Sets the user's watch progress for a show.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <param name="tvdbId">The TVDB ID.</param>
    /// <param name="highestWatchedSeason">The highest season watched.</param>
    /// <remarks>
    /// The value replaces whatever was held rather than being merged with it. Progress now comes from
    /// Trakt's watched snapshot, so it is authoritative in both directions - keeping the higher of the
    /// two would pin a show to a season the user has since unmarked, and Next Seasons would keep asking
    /// for a season past it.
    /// </remarks>
    public void SetUserWatchProgress(Guid userId, int tvdbId, int highestWatchedSeason)
    {
        var userProgress = GetUserWatchProgress(userId);
        userProgress[tvdbId] = highestWatchedSeason;
    }

    /// <summary>
    /// Gets the user's highest watched season for a show.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <param name="tvdbId">The TVDB ID.</param>
    /// <returns>The highest watched season if found, null otherwise.</returns>
    public int? GetUserHighestWatchedSeason(Guid userId, int tvdbId)
    {
        var userProgress = GetUserWatchProgress(userId);
        return userProgress.TryGetValue(tvdbId, out var season) ? season : null;
    }

    /// <summary>
    /// Gets a cached show by TVDB ID.
    /// </summary>
    /// <param name="tvdbId">The TVDB ID.</param>
    /// <returns>The cached show entry if found, null otherwise.</returns>
    public ShowCacheEntry? GetCachedShow(int tvdbId)
    {
        return _showsCache.TryGetValue(tvdbId, out var entry) ? entry : null;
    }

    /// <summary>
    /// Gets cached metadata for a specific season.
    /// </summary>
    /// <param name="tvdbId">The TVDB ID.</param>
    /// <param name="seasonNumber">The season number.</param>
    /// <returns>The cached season metadata if found, null otherwise.</returns>
    public SeasonMetadata? GetCachedSeason(int tvdbId, int seasonNumber)
    {
        var show = GetCachedShow(tvdbId);
        return show?.Seasons.TryGetValue(seasonNumber, out var season) == true ? season : null;
    }

    /// <summary>
    /// Checks if a season exists in the cache and has aired.
    /// </summary>
    /// <param name="tvdbId">The TVDB ID.</param>
    /// <param name="seasonNumber">The season number.</param>
    /// <returns>True if the season exists and has aired, false otherwise.</returns>
    public bool IsSeasonAvailable(int tvdbId, int seasonNumber)
    {
        var season = GetCachedSeason(tvdbId, seasonNumber);
        return season != null && season.AiredEpisodes > 0;
    }

    /// <summary>
    /// Removes a show from the cache.
    /// </summary>
    /// <param name="tvdbId">The TVDB ID.</param>
    public void RemoveShow(int tvdbId)
    {
        if (_showsCache.TryRemove(tvdbId, out var entry))
        {
            _logger.LogInformation("Removed show from cache: {Title} (TVDB: {TvdbId})", entry.Title, tvdbId);
        }
    }

    /// <summary>
    /// Clears the entire cache.
    /// </summary>
    public void ClearCache()
    {
        _showsCache.Clear();
        _logger.LogInformation("Cleared all shows cache");
    }

    /// <summary>
    /// Gets the count of cached shows.
    /// </summary>
    /// <returns>Number of shows in cache.</returns>
    public int GetCachedShowCount()
    {
        return _showsCache.Count;
    }

    /// <summary>
    /// Gets all cached shows.
    /// </summary>
    /// <returns>Dictionary of TVDB ID to show cache entry.</returns>
    public IReadOnlyDictionary<int, ShowCacheEntry> GetAllCachedShows()
    {
        return _showsCache;
    }

    /// <summary>
    /// Gets all cached shows that have watched progress for a specific user.
    /// Returns tuples of (show, highestWatchedSeason).
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <returns>Collection of shows with their watched progress for the user.</returns>
    public IEnumerable<(ShowCacheEntry Show, int HighestWatchedSeason)> GetShowsWithWatchedProgress(Guid userId)
    {
        var userProgress = GetUserWatchProgress(userId);
        foreach (var (tvdbId, highestSeason) in userProgress)
        {
            var show = GetCachedShow(tvdbId);
            if (show != null)
            {
                yield return (show, highestSeason);
            }
        }
    }

    /// <summary>
    /// Determines if a show is ended or canceled.
    /// </summary>
    /// <param name="show">The Trakt show.</param>
    /// <returns>True if ended or canceled, false otherwise.</returns>
    private bool IsShowEnded(TraktShow show)
    {
        return !string.IsNullOrEmpty(show.Status) &&
               (show.Status.Equals("ended", StringComparison.OrdinalIgnoreCase) ||
                show.Status.Equals("canceled", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Gets the highest watched season from a watched show.
    /// </summary>
    /// <param name="watchedShow">The watched show data.</param>
    /// <returns>The highest season number with watched episodes, or null if none.</returns>
    private int? GetHighestWatchedSeason(TraktWatchedShow watchedShow)
    {
        var watchedSeasons = watchedShow.Seasons
            .Where(s => s.Number > 0 && s.Episodes.Any())
            .Select(s => s.Number)
            .OrderByDescending(s => s)
            .ToList();

        return watchedSeasons.Any() ? watchedSeasons.First() : null;
    }
}
