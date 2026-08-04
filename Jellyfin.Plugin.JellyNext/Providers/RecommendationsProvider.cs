using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyNext.Helpers;
using Jellyfin.Plugin.JellyNext.Models.Common;
using Jellyfin.Plugin.JellyNext.Models.Radarr;
using Jellyfin.Plugin.JellyNext.Models.Sonarr;
using Jellyfin.Plugin.JellyNext.Models.Trakt;
using Jellyfin.Plugin.JellyNext.Services;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyNext.Providers;

/// <summary>
/// Provider for Trakt personalized recommendations.
/// </summary>
public class RecommendationsProvider : IContentProvider
{
    /// <summary>
    /// The most recommendations Trakt will return in one request.
    /// </summary>
    private const int MaxTraktRecommendations = 100;

    private readonly ILogger<RecommendationsProvider> _logger;
    private readonly TraktApi _traktApi;
    private readonly ShowsCacheService _showsCache;
    private readonly TraktPluginBridge _traktPluginBridge;
    private readonly TraktCollectionService _collectionService;
    private readonly LocalLibraryService _localLibraryService;

    /// <summary>
    /// Initializes a new instance of the <see cref="RecommendationsProvider"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="traktApi">The Trakt API service.</param>
    /// <param name="showsCache">The shows cache service.</param>
    /// <param name="traktPluginBridge">Bridge to the official Trakt plugin's stored tokens.</param>
    /// <param name="collectionService">The Trakt collection service.</param>
    /// <param name="localLibraryService">The local library service.</param>
    public RecommendationsProvider(
        ILogger<RecommendationsProvider> logger,
        TraktApi traktApi,
        ShowsCacheService showsCache,
        TraktPluginBridge traktPluginBridge,
        TraktCollectionService collectionService,
        LocalLibraryService localLibraryService)
    {
        _logger = logger;
        _traktApi = traktApi;
        _showsCache = showsCache;
        _traktPluginBridge = traktPluginBridge;
        _collectionService = collectionService;
        _localLibraryService = localLibraryService;
    }

    /// <inheritdoc />
    public string ProviderName => "recommendations";

    /// <inheritdoc />
    public string LibraryName => "Trakt Recommendations";

    /// <inheritdoc />
    public bool IsEnabledForUser(Guid userId)
    {
        var traktUser = UserHelper.GetTraktUser(userId);
        if (!_traktPluginBridge.HasUsableToken(traktUser))
        {
            return false;
        }

        return traktUser!.SyncMovieRecommendations || traktUser.SyncShowRecommendations;
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

        var contentItems = new List<ContentItem>();

        try
        {
            if (traktUser.SyncMovieRecommendations)
            {
                await FetchMovieRecommendationsAsync(traktUser, contentItems);
            }

            if (traktUser.SyncShowRecommendations)
            {
                await FetchShowRecommendationsAsync(traktUser, contentItems);
            }

            int movieCount = contentItems.Count(c => c.Type == ContentType.Movie);
            int showCount = contentItems.Count(c => c.Type == ContentType.Show);
            _logger.LogInformation(
                "Fetched {MovieCount} movie and {ShowCount} show recommendations for user {UserId}",
                movieCount,
                showCount,
                userId);
        }
        catch (TraktAuthenticationException)
        {
            // Surface auth failures so the caller skips the cycle instead of caching an empty result.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch recommendations for user {UserId}", userId);
        }

        return contentItems.AsReadOnly();
    }

    private async Task FetchMovieRecommendationsAsync(TraktUser traktUser, List<ContentItem> contentItems)
    {
        var limit = Math.Clamp(traktUser.MovieRecommendationsLimit, 1, 100);
        var collected = traktUser.IgnoreCollected
            ? await _collectionService.GetCollectedMovieIdsAsync(traktUser)
            : null;

        var movies = await _traktApi.GetMovieRecommendations(
            traktUser,
            traktUser.IgnoreCollected,
            traktUser.IgnoreWatchlisted,
            limit: GetFetchLimit(traktUser, limit));

        var added = 0;
        var skipped = 0;

        foreach (var movie in movies)
        {
            if (added >= limit)
            {
                break;
            }

            if (traktUser.IgnoreCollected && IsMovieAlreadyHeld(movie, collected))
            {
                skipped++;
                continue;
            }

            contentItems.Add(new ContentItem
            {
                Type = ContentType.Movie,
                Title = movie.Title,
                Year = movie.Year,
                TmdbId = movie.Ids.Tmdb,
                ImdbId = movie.Ids.Imdb,
                TraktId = movie.Ids.Trakt,
                ProviderName = ProviderName,
                Genres = movie.Genres
            });
            added++;
        }

        LogSkipped("movie", skipped, traktUser.LinkedMbUserId);
    }

    private async Task FetchShowRecommendationsAsync(TraktUser traktUser, List<ContentItem> contentItems)
    {
        var limit = Math.Clamp(traktUser.ShowRecommendationsLimit, 1, 100);
        var collected = traktUser.IgnoreCollected
            ? await _collectionService.GetCollectedShowIdsAsync(traktUser)
            : null;

        var shows = await _traktApi.GetShowRecommendations(
            traktUser,
            traktUser.IgnoreCollected,
            traktUser.IgnoreWatchlisted,
            limit: GetFetchLimit(traktUser, limit));

        var added = 0;
        var skipped = 0;

        foreach (var show in shows)
        {
            // Checked before the season lookup below, which costs a Trakt request per show that is
            // not already in the shows cache.
            if (added >= limit)
            {
                break;
            }

            if (traktUser.IgnoreCollected && IsShowAlreadyHeld(show, collected))
            {
                skipped++;
                continue;
            }

            var contentItem = await ProcessShowRecommendationAsync(show, traktUser);
            contentItems.Add(contentItem);
            added++;
        }

        LogSkipped("show", skipped, traktUser.LinkedMbUserId);
    }

    /// <summary>
    /// Gets how many recommendations to ask Trakt for.
    /// </summary>
    /// <param name="traktUser">The Trakt user configuration.</param>
    /// <param name="limit">How many recommendations the user wants to end up with.</param>
    /// <returns>The limit to send to Trakt.</returns>
    /// <remarks>
    /// Over-fetching when the collected filter is on keeps the configured limit meaning "this many
    /// suggestions": without it, a user whose top recommendations are things they already own ends
    /// up with a nearly empty library, which is the symptom the filter is meant to remove. It costs
    /// nothing beyond a larger response - the endpoint is a single unpaginated request either way.
    /// </remarks>
    private static int GetFetchLimit(TraktUser traktUser, int limit)
    {
        return traktUser.IgnoreCollected ? MaxTraktRecommendations : limit;
    }

    /// <summary>
    /// Checks whether the user already has a recommended movie.
    /// </summary>
    /// <param name="movie">The recommended movie.</param>
    /// <param name="collected">The user's collected movies, if the collection could be read.</param>
    /// <returns>True when the movie should not be recommended.</returns>
    /// <remarks>
    /// The Jellyfin library is checked as well as the Trakt collection because it is the thing the
    /// recommendation would offer to download: a title already on the server is never worth
    /// suggesting, whether or not Trakt knows it is collected.
    /// </remarks>
    private bool IsMovieAlreadyHeld(TraktMovie movie, TraktIdSet? collected)
    {
        if (collected?.Contains(movie.Ids) == true)
        {
            return true;
        }

        return movie.Ids.Tmdb is > 0 && _localLibraryService.DoesMovieExist(movie.Ids.Tmdb.Value);
    }

    /// <summary>
    /// Checks whether the user already has a recommended show.
    /// </summary>
    /// <param name="show">The recommended show.</param>
    /// <param name="collected">The user's collected shows, if the collection could be read.</param>
    /// <returns>True when the show should not be recommended.</returns>
    /// <remarks>
    /// A show counts as held as soon as any of it is collected or in the library. Continuing a show
    /// the user has already started is what the Next Seasons library is for; recommending it again
    /// would offer stubs for seasons they own.
    /// </remarks>
    private bool IsShowAlreadyHeld(TraktShow show, TraktIdSet? collected)
    {
        if (collected?.Contains(show.Ids) == true)
        {
            return true;
        }

        return _localLibraryService.FindSeriesByAnyProviderId(show.Ids.Tvdb, show.Ids.Tmdb, show.Ids.Imdb) != null;
    }

    private void LogSkipped(string kind, int skipped, Guid userId)
    {
        if (skipped == 0)
        {
            return;
        }

        _logger.LogInformation(
            "Dropped {Count} {Kind} recommendation(s) already collected or in the library for user {UserId}",
            skipped,
            kind,
            userId);
    }

    private async Task<ContentItem> ProcessShowRecommendationAsync(TraktShow show, TraktUser traktUser)
    {
        var isEnded = IsShowEnded(show);
        var airedSeasonCount = await GetAiredSeasonCountAsync(show, traktUser, isEnded);

        return new ContentItem
        {
            Type = ContentType.Show,
            Title = show.Title,
            Year = show.Year,
            TmdbId = show.Ids.Tmdb,
            ImdbId = show.Ids.Imdb,
            TvdbId = show.Ids.Tvdb,
            TraktId = show.Ids.Trakt,
            ProviderName = ProviderName,
            AiredSeasonCount = airedSeasonCount,
            Genres = show.Genres
        };
    }

    private bool IsShowEnded(TraktShow show)
    {
        return !string.IsNullOrEmpty(show.Status) &&
               (show.Status.Equals("ended", StringComparison.OrdinalIgnoreCase) ||
                show.Status.Equals("canceled", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<int?> GetAiredSeasonCountAsync(TraktShow show, TraktUser traktUser, bool isEnded)
    {
        var cachedSeasonCount = TryGetCachedSeasonCount(show, isEnded);
        if (cachedSeasonCount.HasValue)
        {
            return cachedSeasonCount.Value;
        }

        return await FetchAndCacheSeasonCountAsync(show, traktUser, isEnded);
    }

    private int? TryGetCachedSeasonCount(TraktShow show, bool isEnded)
    {
        if (show.Ids.Tvdb == null || show.Ids.Tvdb <= 0)
        {
            return null;
        }

        var cachedShow = _showsCache.GetCachedShow(show.Ids.Tvdb.Value);
        if (cachedShow != null && cachedShow.Seasons.Count > 0)
        {
            var airedSeasonCount = cachedShow.Seasons.Values.Count(s => s.AiredEpisodes > 0);
            _logger.LogDebug(
                "Using cached season count for show: {Title} (TVDB: {TvdbId}, Status: {Status}, Seasons: {Seasons})",
                show.Title,
                show.Ids.Tvdb.Value,
                cachedShow.Status,
                airedSeasonCount);
            return airedSeasonCount;
        }

        return null;
    }

    private async Task<int?> FetchAndCacheSeasonCountAsync(TraktShow show, TraktUser traktUser, bool isEnded)
    {
        try
        {
            var seasons = await _traktApi.GetShowSeasons(traktUser, show.Ids.Trakt);
            var airedSeasonCount = seasons.Count(s => s.Number > 0 && s.AiredEpisodes > 0);

            _logger.LogDebug(
                "Show {Title} has {SeasonCount} aired seasons",
                show.Title,
                airedSeasonCount);

            // Note: ShowsCacheService caching is handled by NextSeasonsProvider's sync process
            // We don't cache here to avoid redundant caching logic

            return airedSeasonCount;
        }
        catch (TraktAuthenticationException)
        {
            // Surface auth failures so the caller skips the cycle instead of caching an empty result.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch season info for show {Title}, will use default", show.Title);
            return null;
        }
    }
}
