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

            foreach (var (show, progress) in watchedShowsList)
            {
                try
                {
                    var (contentItem, skipReason) = await ProcessWatchedShowAsync(show, progress, traktUser, hiddenExamples);
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
        WatchProgress progress,
        TraktUser traktUser,
        List<string> hiddenExamples)
    {
        if (!cachedShow.TvdbId.HasValue || cachedShow.TvdbId.Value == 0)
        {
            return (null, "no TVDB id");
        }

        var tvdbId = cachedShow.TvdbId.Value;
        var highestWatchedSeason = progress.Season;
        var nextSeasonNumber = highestWatchedSeason + 1;

        _logger.LogDebug(
            "Checking next season for {Title} (TVDB: {TvdbId}): highest watched S{Watched}E{Episode}, checking S{Next}",
            cachedShow.Title,
            tvdbId,
            highestWatchedSeason,
            progress.Episode,
            nextSeasonNumber);

        // Check cache for next season
        var cachedSeason = _showsCache.GetCachedSeason(tvdbId, nextSeasonNumber);

        // An ongoing show's incomplete seasons are deliberately not cached, so the latest season data -
        // for the next season and for the one the user is in - has to come from Trakt.
        TraktSeason[]? traktSeasons = null;
        if (cachedSeason == null && !cachedShow.IsEnded)
        {
            _logger.LogDebug(
                "Next season S{Season} not in cache for ongoing show {Title}, fetching from Trakt",
                nextSeasonNumber,
                cachedShow.Title);

            traktSeasons = await _traktApi.GetShowSeasons(traktUser, cachedShow.TraktId);
        }

        // The rest of the season the user is in comes before the next one: a split season that returned
        // from its mid-season break is what they are actually missing.
        var resumed = await TryGetResumedSeasonAsync(cachedShow, progress, traktSeasons, traktUser, hiddenExamples);
        if (resumed != null)
        {
            return (resumed, null);
        }

        if (traktSeasons != null)
        {
            var nextTraktSeason = traktSeasons.FirstOrDefault(s => s.Number == nextSeasonNumber);

            if (nextTraktSeason != null && nextTraktSeason.AiredEpisodes > 0)
            {
                cachedSeason = ToSeasonMetadata(nextTraktSeason);
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

        if (traktUser.NextSeasonsRecentOnly
            && !SeasonReleaseHelper.IsRecentlyReleased(cachedSeason.FirstAired, traktUser.NextSeasonsRecentDays))
        {
            var premiered = cachedSeason.FirstAired?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "unknown";
            hiddenExamples.Add($"{cachedShow.Title} S{nextSeasonNumber} (premiered {premiered})");
            return (null, "hidden by the new-release filter");
        }

        // Check if season exists in local library
        var existsLocally = _localLibraryService.DoesSeasonExist(
            cachedShow.TvdbId,
            cachedShow.TmdbId,
            cachedShow.ImdbId,
            nextSeasonNumber);
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

        return (CreateContentItem(cachedShow, cachedSeason, cachedSeason.FirstAired, null), null);
    }

    /// <summary>
    /// Offers the rest of the season the user is in, when it has come back from a mid-season break.
    /// </summary>
    /// <remarks>
    /// Some shows stop a season at a mid-season finale and air the second half months later under the
    /// same season number. Progress then stays on that season and there is no next season to point at,
    /// so without this the return of the show was never noticed. The part's own return date stands in
    /// for the premiere, which is what makes the new-release window and the email treat it as new.
    /// </remarks>
    private async Task<ContentItem?> TryGetResumedSeasonAsync(
        ShowCacheEntry cachedShow,
        WatchProgress progress,
        TraktSeason[]? traktSeasons,
        TraktUser traktUser,
        List<string> hiddenExamples)
    {
        var freshSeason = traktSeasons?.FirstOrDefault(s => s.Number == progress.Season);
        var season = freshSeason != null
            ? ToSeasonMetadata(freshSeason)
            : _showsCache.GetCachedSeason(cachedShow.TvdbId!.Value, progress.Season);

        // Nothing has aired past the user's last episode. This also keeps the per-season episode lookup
        // off the path for every show the user is caught up on.
        if (season == null || season.AiredEpisodes <= progress.Episode)
        {
            return null;
        }

        var episodes = await _showsCache.GetSeasonEpisodesAsync(
            traktUser, cachedShow.TraktId, progress.Season, season.AiredEpisodes);
        var part = SeasonReleaseHelper.FindResumedPart(episodes, progress.Episode);
        if (part == null)
        {
            return null;
        }

        var (fromEpisode, released) = part.Value;

        if (traktUser.NextSeasonsRecentOnly
            && !SeasonReleaseHelper.IsRecentlyReleased(released, traktUser.NextSeasonsRecentDays))
        {
            hiddenExamples.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{cachedShow.Title} S{progress.Season} from E{fromEpisode} (returned {released:yyyy-MM-dd})"));
            return null;
        }

        if (_localLibraryService.DoesSeasonExist(
                cachedShow.TvdbId, cachedShow.TmdbId, cachedShow.ImdbId, progress.Season, fromEpisode))
        {
            _logger.LogDebug(
                "Resumed season S{Season} from E{Episode} already exists locally for {Title}",
                progress.Season,
                fromEpisode,
                cachedShow.Title);
            return null;
        }

        _logger.LogInformation(
            "Recommending resumed season S{Season} from E{Episode} for {Title} (TVDB: {TvdbId}), back since {Released:yyyy-MM-dd}",
            progress.Season,
            fromEpisode,
            cachedShow.Title,
            cachedShow.TvdbId,
            released);

        return CreateContentItem(cachedShow, season, released, fromEpisode);
    }

    private ContentItem CreateContentItem(
        ShowCacheEntry show, SeasonMetadata season, DateTime? released, int? resumesAtEpisode)
    {
        return new ContentItem
        {
            Type = ContentType.Show,
            Title = show.Title,
            Year = show.Year,
            TmdbId = show.TmdbId,
            ImdbId = show.ImdbId,
            TvdbId = show.TvdbId,
            TraktId = show.TraktId,
            ProviderName = ProviderName,
            SeasonNumber = season.SeasonNumber,
            SeasonFirstAired = released,
            SeasonResumesAtEpisode = resumesAtEpisode,
            SeasonEpisodeCount = season.EpisodeCount,
            SeasonAiredEpisodes = season.AiredEpisodes,
            SeasonIsAiring = SeasonReleaseHelper.IsAiring(show, season),
            Genres = show.Genres
        };
    }

    private static SeasonMetadata ToSeasonMetadata(TraktSeason season)
    {
        return new SeasonMetadata
        {
            SeasonNumber = season.Number,
            EpisodeCount = season.EpisodeCount,
            AiredEpisodes = season.AiredEpisodes,
            FirstAired = season.FirstAired,
            CachedAt = DateTime.UtcNow
        };
    }
}
