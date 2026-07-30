using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyNext.Helpers;
using Jellyfin.Plugin.JellyNext.Models.Common;
using Jellyfin.Plugin.JellyNext.Models.Trakt;
using Jellyfin.Plugin.JellyNext.Services;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyNext.Providers;

/// <summary>
/// Provider for next seasons of watched shows.
/// </summary>
public class NextSeasonsProvider : IContentProvider
{
    private readonly ILogger<NextSeasonsProvider> _logger;
    private readonly TraktApi _traktApi;
    private readonly LocalLibraryService _localLibraryService;
    private readonly ShowsCacheService _showsCache;
    private readonly TraktPluginBridge _traktPluginBridge;

    /// <summary>
    /// Initializes a new instance of the <see cref="NextSeasonsProvider"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="traktApi">The Trakt API service.</param>
    /// <param name="localLibraryService">The local library service.</param>
    /// <param name="showsCache">The shows cache service.</param>
    /// <param name="traktPluginBridge">Bridge to the official Trakt plugin's stored tokens.</param>
    public NextSeasonsProvider(
        ILogger<NextSeasonsProvider> logger,
        TraktApi traktApi,
        LocalLibraryService localLibraryService,
        ShowsCacheService showsCache,
        TraktPluginBridge traktPluginBridge)
    {
        _logger = logger;
        _traktApi = traktApi;
        _localLibraryService = localLibraryService;
        _showsCache = showsCache;
        _traktPluginBridge = traktPluginBridge;
    }

    /// <inheritdoc />
    public string ProviderName => "nextseasons";

    /// <inheritdoc />
    public string LibraryName => "Next Seasons";

    /// <inheritdoc />
    public bool IsEnabledForUser(Guid userId)
    {
        var traktUser = UserHelper.GetTraktUser(userId);
        if (!_traktPluginBridge.HasUsableToken(traktUser))
        {
            return false;
        }

        return traktUser!.SyncNextSeasons;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ContentItem>> FetchContentAsync(Guid userId)
    {
        var traktUser = UserHelper.GetTraktUser(userId);
        if (traktUser == null)
        {
            _logger.LogWarning("No Trakt user found for Jellyfin user {UserId}", userId);
            return Array.Empty<ContentItem>();
        }

        // Perform sync (full or incremental) - this populates the cache with watched progress
        await SyncWatchedShows(traktUser);

        var contentItems = new List<ContentItem>();

        try
        {
            // Get all shows with watched progress from cache for this user (no duplicate API calls!)
            var watchedShows = _showsCache.GetShowsWithWatchedProgress(userId);
            var watchedShowsList = watchedShows.ToList();
            _logger.LogInformation("Processing {Count} watched shows from cache for user {UserId}", watchedShowsList.Count, userId);

            if (watchedShowsList.Count == 0)
            {
                return Array.Empty<ContentItem>();
            }

            // Why a show produced nothing is the only useful thing to know when the library comes out
            // empty, so the reasons are counted and reported together rather than left in debug logs.
            var skipReasons = new Dictionary<string, int>();
            var hiddenExamples = new List<string>();

            foreach (var (show, highestWatchedSeason) in watchedShowsList)
            {
                try
                {
                    var (contentItem, skipReason) = await ProcessWatchedShowAsync(show, highestWatchedSeason, traktUser, hiddenExamples);
                    if (contentItem != null)
                    {
                        contentItems.Add(contentItem);
                    }
                    else if (skipReason != null)
                    {
                        skipReasons[skipReason] = skipReasons.GetValueOrDefault(skipReason) + 1;
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
                        "Error processing watched show {Title}",
                        show.Title);
                }
            }

            _logger.LogInformation(
                "Found {Count} next season recommendations for user {UserId} (skipped: {SkipReasons})",
                contentItems.Count,
                userId,
                skipReasons.Count == 0
                    ? "none"
                    : string.Join(", ", skipReasons.Select(reason => $"{reason.Key}={reason.Value}")));

            if (hiddenExamples.Count > 0)
            {
                _logger.LogInformation(
                    "Hidden by the {Days} day new-release filter for user {UserId}: {Examples}",
                    traktUser.NextSeasonsRecentDays,
                    userId,
                    string.Join("; ", hiddenExamples.Take(10)));
            }
        }
        catch (TraktAuthenticationException)
        {
            // Surface auth failures so the caller skips the cycle instead of caching an empty result.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch next seasons for user {UserId}", userId);
        }

        return contentItems.AsReadOnly();
    }

    /// <summary>
    /// Refreshes watch progress and season metadata before content is read from the cache.
    /// </summary>
    /// <param name="traktUser">The Trakt user configuration.</param>
    private async Task SyncWatchedShows(TraktUser traktUser)
    {
        await _showsCache.SyncWatchedShows(traktUser);
    }

    /// <summary>
    /// Processes a watched show to determine if next season should be recommended.
    /// Returns the suggestion, or the reason the show produced none.
    /// </summary>
    private async Task<(ContentItem? Item, string? SkipReason)> ProcessWatchedShowAsync(
        ShowCacheEntry cachedShow,
        int highestWatchedSeason,
        TraktUser traktUser,
        List<string> hiddenExamples)
    {
        if (!cachedShow.TvdbId.HasValue || cachedShow.TvdbId.Value == 0)
        {
            return (null, "no TVDB id");
        }

        var tvdbId = cachedShow.TvdbId.Value;
        var nextSeasonNumber = highestWatchedSeason + 1;

        _logger.LogDebug(
            "Checking next season for {Title} (TVDB: {TvdbId}): highest watched S{Watched}, checking S{Next}",
            cachedShow.Title,
            tvdbId,
            highestWatchedSeason,
            nextSeasonNumber);

        // Check cache for next season
        var cachedSeason = _showsCache.GetCachedSeason(tvdbId, nextSeasonNumber);

        // If not in cache and show is not ended, fetch fresh data
        if (cachedSeason == null && !cachedShow.IsEnded)
        {
            _logger.LogDebug(
                "Next season S{Season} not in cache for ongoing show {Title}, fetching from Trakt",
                nextSeasonNumber,
                cachedShow.Title);

            // Fetch latest seasons from Trakt
            var traktSeasons = await _traktApi.GetShowSeasons(traktUser, cachedShow.TraktId);
            var nextTraktSeason = traktSeasons.FirstOrDefault(s => s.Number == nextSeasonNumber);

            if (nextTraktSeason != null && nextTraktSeason.AiredEpisodes > 0)
            {
                cachedSeason = new SeasonMetadata
                {
                    SeasonNumber = nextTraktSeason.Number,
                    EpisodeCount = nextTraktSeason.EpisodeCount,
                    AiredEpisodes = nextTraktSeason.AiredEpisodes,
                    FirstAired = nextTraktSeason.FirstAired,
                    CachedAt = DateTime.UtcNow
                };
            }
            else
            {
                _logger.LogDebug(
                    "Next season S{Season} does not exist or has not aired for {Title}",
                    nextSeasonNumber,
                    cachedShow.Title);
                return (null, "next season does not exist or has not aired");
            }
        }

        // An ended show caches every season it has, so a missing one means the user has finished it.
        if (cachedSeason == null)
        {
            return (null, "show is finished, no further season");
        }

        // Check if season has aired
        if (cachedSeason.AiredEpisodes == 0)
        {
            _logger.LogDebug(
                "Next season S{Season} has not aired yet for {Title}",
                nextSeasonNumber,
                cachedShow.Title);
            return (null, "next season has not aired");
        }

        if (traktUser.NextSeasonsRecentOnly && !IsRecentlyReleased(cachedShow, cachedSeason, traktUser))
        {
            var premiered = cachedSeason.FirstAired?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "unknown";
            hiddenExamples.Add($"{cachedShow.Title} S{nextSeasonNumber} (premiered {premiered})");
            return (null, "hidden by the new-release filter");
        }

        // Check if season exists in local library
        var existsLocally = _localLibraryService.DoesSeasonExist(tvdbId, nextSeasonNumber);
        if (existsLocally)
        {
            _logger.LogDebug(
                "Next season S{Season} already exists locally for {Title}",
                nextSeasonNumber,
                cachedShow.Title);
            return (null, "already in the Jellyfin library");
        }

        // Recommend the next season
        _logger.LogInformation(
            "Recommending next season S{Season} for {Title} (TVDB: {TvdbId})",
            nextSeasonNumber,
            cachedShow.Title,
            tvdbId);

        return (
            new ContentItem
            {
                Type = ContentType.Show,
                Title = cachedShow.Title,
                Year = cachedShow.Year,
                TmdbId = cachedShow.TmdbId,
                ImdbId = cachedShow.ImdbId,
                TvdbId = cachedShow.TvdbId,
                TraktId = cachedShow.TraktId,
                ProviderName = ProviderName,
                SeasonNumber = nextSeasonNumber,
                Genres = cachedShow.Genres
            },
            null);
    }

    /// <summary>
    /// Determines whether a season counts as a new release.
    /// </summary>
    /// <remarks>
    /// Backs the opt-in "recently released seasons only" filter, which exists because the default
    /// behaviour surfaces the next season of every partially watched show - including shows that
    /// ended years ago - rather than only what has just come out.
    /// </remarks>
    private bool IsRecentlyReleased(ShowCacheEntry show, SeasonMetadata season, TraktUser traktUser)
    {
        // A season part-way through its run is airing right now whatever its premiere date says, which
        // keeps long or split-cour seasons visible past the cut-off. Ended shows are excluded because
        // their unaired episode counts are leftovers from a cancellation, not an ongoing release.
        if (!show.IsEnded && season.AiredEpisodes > 0 && season.EpisodeCount > season.AiredEpisodes)
        {
            return true;
        }

        if (!season.FirstAired.HasValue)
        {
            // Without a premiere date there is nothing to judge recency by, and the filter is meant to
            // exclude by default - an undated season stays hidden rather than leaking the backlog back in.
            return false;
        }

        var firstAired = season.FirstAired.Value;
        var firstAiredUtc = firstAired.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(firstAired, DateTimeKind.Utc)
            : firstAired.ToUniversalTime();

        var windowDays = Math.Clamp(traktUser.NextSeasonsRecentDays, 1, 3650);
        return firstAiredUtc >= DateTime.UtcNow.AddDays(-windowDays);
    }
}
