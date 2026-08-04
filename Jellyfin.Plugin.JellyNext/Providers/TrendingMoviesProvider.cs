using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyNext.Helpers;
using Jellyfin.Plugin.JellyNext.Models.Common;
using Jellyfin.Plugin.JellyNext.Models.Trakt;
using Jellyfin.Plugin.JellyNext.Services;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyNext.Providers;

/// <summary>
/// Provider for Trakt trending movies (global, not per-user).
/// </summary>
public class TrendingMoviesProvider : IContentProvider
{
    /// <summary>
    /// The most trending movies Trakt will return in one request.
    /// </summary>
    private const int MaxTrendingMovies = 100;

    private readonly ILogger<TrendingMoviesProvider> _logger;
    private readonly TraktApi _traktApi;
    private readonly TraktPluginBridge _traktPluginBridge;
    private readonly TraktCollectionService _collectionService;
    private readonly LocalLibraryService _localLibraryService;

    /// <summary>
    /// Initializes a new instance of the <see cref="TrendingMoviesProvider"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="traktApi">The Trakt API service.</param>
    /// <param name="traktPluginBridge">Bridge to the official Trakt plugin's stored tokens.</param>
    /// <param name="collectionService">The Trakt collection service.</param>
    /// <param name="localLibraryService">The local library service.</param>
    public TrendingMoviesProvider(
        ILogger<TrendingMoviesProvider> logger,
        TraktApi traktApi,
        TraktPluginBridge traktPluginBridge,
        TraktCollectionService collectionService,
        LocalLibraryService localLibraryService)
    {
        _logger = logger;
        _traktApi = traktApi;
        _traktPluginBridge = traktPluginBridge;
        _collectionService = collectionService;
        _localLibraryService = localLibraryService;
    }

    /// <inheritdoc />
    public string ProviderName => "trending";

    /// <inheritdoc />
    public string LibraryName => "Trending Movies";

    /// <inheritdoc />
    public bool IsEnabledForUser(Guid userId)
    {
        // Trending is global, not per-user
        // Check global configuration instead
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            return false;
        }

        return config.TrendingMoviesEnabled;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ContentItem>> FetchContentAsync(Guid userId)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null || !config.TrendingMoviesEnabled)
        {
            return Array.Empty<ContentItem>();
        }

        // Get the user ID from config to fetch trending with
        var trendingUserId = config.TrendingMoviesUserId;
        if (trendingUserId == Guid.Empty)
        {
            _logger.LogWarning("Trending movies enabled but no user ID configured");
            return Array.Empty<ContentItem>();
        }

        var traktUser = UserHelper.GetTraktUser(trendingUserId);
        if (traktUser == null || !_traktPluginBridge.HasUsableToken(traktUser))
        {
            _logger.LogWarning(
                "Trending movies enabled but user {UserId} has no valid Trakt authentication",
                trendingUserId);
            return Array.Empty<ContentItem>();
        }

        var contentItems = new List<ContentItem>();

        try
        {
            var limit = Math.Clamp(config.TrendingMoviesLimit, 1, 100);

            // The trending library is global, but it is fetched with one account's Trakt credentials
            // and that account's "ignore collected" setting is what governs the filter here. The
            // Jellyfin library check needs no such qualification - it is the same for every user.
            var filterCollected = traktUser.IgnoreCollected;
            var collected = filterCollected
                ? await _collectionService.GetCollectedMovieIdsAsync(traktUser)
                : null;

            var movies = await _traktApi.GetTrendingMovies(
                traktUser,
                filterCollected ? MaxTrendingMovies : limit);

            var skipped = 0;

            foreach (var movie in movies)
            {
                if (contentItems.Count >= limit)
                {
                    break;
                }

                if (filterCollected && IsMovieAlreadyHeld(movie, collected))
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
            }

            if (skipped > 0)
            {
                _logger.LogInformation(
                    "Dropped {Count} trending movie(s) already collected or in the library",
                    skipped);
            }

            _logger.LogInformation(
                "Fetched {Count} trending movies",
                contentItems.Count);
        }
        catch (TraktAuthenticationException)
        {
            // Surface auth failures so the caller skips the cycle instead of caching an empty result.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch trending movies");
        }

        return contentItems.AsReadOnly();
    }

    /// <summary>
    /// Checks whether a trending movie is already held.
    /// </summary>
    /// <param name="movie">The trending movie.</param>
    /// <param name="collected">The source user's collected movies, if the collection could be read.</param>
    /// <returns>True when the movie should not be offered.</returns>
    private bool IsMovieAlreadyHeld(TraktMovie movie, TraktIdSet? collected)
    {
        if (collected?.Contains(movie.Ids) == true)
        {
            return true;
        }

        return movie.Ids.Tmdb is > 0 && _localLibraryService.DoesMovieExist(movie.Ids.Tmdb.Value);
    }
}
