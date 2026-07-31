using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyNext.Helpers;
using Jellyfin.Plugin.JellyNext.Models.Common;
using Jellyfin.Plugin.JellyNext.Services.DownloadProviders;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyNext.Services;

/// <summary>
/// Backs the New Seasons home screen widget: what to list for a user, and what happens when they
/// press Request.
/// </summary>
/// <remarks>
/// Reads the same cached <c>nextseasons</c> content the virtual library is built from, so the widget
/// and the library always agree on what counts as a new season - including the per-user new-release
/// filter - and the widget costs no additional Trakt requests.
/// </remarks>
public class NextSeasonsWidgetService
{
    private const string ProviderName = "nextseasons";

    /// <summary>
    /// How long a looked up poster URL is reused before Trakt is asked again.
    /// </summary>
    private static readonly TimeSpan PosterCacheDuration = TimeSpan.FromDays(7);

    /// <summary>
    /// How long a show with no artwork on Trakt is left alone before trying again.
    /// </summary>
    private static readonly TimeSpan PosterMissCacheDuration = TimeSpan.FromHours(12);

    private readonly ILogger<NextSeasonsWidgetService> _logger;
    private readonly ContentCacheService _cacheService;
    private readonly LocalLibraryService _localLibraryService;
    private readonly DownloadProviderFactory _downloadProviderFactory;
    private readonly TraktApi _traktApi;

    // Not persisted, like the watchlist's request tracking: the durable answer to "do I have this
    // season" is the library, which the next content sync re-checks. This only keeps the button from
    // reading "Request" again the moment after it was pressed.
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, DateTime>> _requested = new();
    private readonly ConcurrentDictionary<int, CachedPoster> _posterCache = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="NextSeasonsWidgetService"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="cacheService">The content cache service.</param>
    /// <param name="localLibraryService">The local library service.</param>
    /// <param name="downloadProviderFactory">The download provider factory.</param>
    /// <param name="traktApi">The Trakt API service.</param>
    public NextSeasonsWidgetService(
        ILogger<NextSeasonsWidgetService> logger,
        ContentCacheService cacheService,
        LocalLibraryService localLibraryService,
        DownloadProviderFactory downloadProviderFactory,
        TraktApi traktApi)
    {
        _logger = logger;
        _cacheService = cacheService;
        _localLibraryService = localLibraryService;
        _downloadProviderFactory = downloadProviderFactory;
        _traktApi = traktApi;
    }

    /// <summary>
    /// Gets the shows with a new season for a user, newest season first.
    /// </summary>
    /// <param name="userId">The Jellyfin user ID.</param>
    /// <returns>The widget items, limited to the configured item count.</returns>
    public IReadOnlyList<NextSeasonWidgetItem> GetItems(Guid userId)
    {
        var limit = Math.Clamp(Plugin.Instance?.Configuration.NextSeasonsWidgetLimit ?? 12, 1, 50);
        var requestedForUser = _requested.GetOrAdd(userId, _ => new ConcurrentDictionary<string, DateTime>());

        return _cacheService.GetCachedContent(userId, ProviderName)
            .Where(item => item.Type == ContentType.Show && item.SeasonNumber.HasValue)
            .OrderByDescending(item => item.SeasonFirstAired ?? DateTime.MinValue)
            .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(item => new NextSeasonWidgetItem
            {
                TraktId = item.TraktId,
                TvdbId = item.TvdbId,
                Title = item.Title,
                Year = item.Year,
                SeasonNumber = item.SeasonNumber!.Value,
                EpisodeCount = item.SeasonEpisodeCount,
                AiredEpisodes = item.SeasonAiredEpisodes,
                FirstAired = item.SeasonFirstAired,
                IsAiring = item.SeasonIsAiring,
                ImagePath = GetImagePath(item),
                Requested = requestedForUser.ContainsKey(GetRequestKey(item.TraktId, item.SeasonNumber!.Value))
            })
            .ToList();
    }

    /// <summary>
    /// Requests a season through the configured download integration.
    /// </summary>
    /// <param name="userId">The Jellyfin user making the request.</param>
    /// <param name="traktId">The Trakt show ID.</param>
    /// <param name="seasonNumber">The season to request.</param>
    /// <returns>The download result, or null when the show is not in the user's cached content.</returns>
    public async Task<DownloadResult?> RequestAsync(Guid userId, int traktId, int seasonNumber)
    {
        var contentItem = _cacheService.GetCachedContent(userId, ProviderName)
            .FirstOrDefault(item => item.TraktId == traktId && item.SeasonNumber == seasonNumber);

        if (contentItem == null)
        {
            _logger.LogWarning(
                "Widget request for Trakt show {TraktId} season {Season} is not in the cached content of user {UserId}",
                traktId,
                seasonNumber,
                userId);
            return null;
        }

        var isAnime = contentItem.Genres.Any(genre => genre.Equals("anime", StringComparison.OrdinalIgnoreCase));

        _logger.LogInformation(
            "Widget request from user {UserId}: {Title} ({Year}) season {Season} - Type: {Type}",
            userId,
            contentItem.Title,
            contentItem.Year,
            seasonNumber,
            isAnime ? "Anime" : "Standard");

        var provider = _downloadProviderFactory.GetProvider();
        var result = await provider.RequestShowAsync(
            contentItem,
            seasonNumber,
            userId.ToString("D", CultureInfo.InvariantCulture),
            isAnime);

        if (result.Success)
        {
            _requested.GetOrAdd(userId, _ => new ConcurrentDictionary<string, DateTime>())[
                GetRequestKey(traktId, seasonNumber)] = DateTime.UtcNow;
        }

        return result;
    }

    /// <summary>
    /// Resolves the poster of a show for the widget, falling back to Trakt's artwork.
    /// </summary>
    /// <param name="traktId">The Trakt show ID.</param>
    /// <returns>An absolute image URL, or null when no artwork is available.</returns>
    public async Task<string?> GetTraktPosterUrlAsync(int traktId)
    {
        if (_posterCache.TryGetValue(traktId, out var cached) && !cached.IsExpired)
        {
            return cached.Url;
        }

        // Any linked account can read a show's artwork - it is not user specific - so the first user
        // with a usable token answers for everyone, which also lets the widget's <img> tags stay
        // anonymous requests.
        var traktUser = Plugin.Instance?.Configuration.TraktUsers
            .FirstOrDefault(user => !string.IsNullOrEmpty(user.AccessToken))
            ?? Plugin.Instance?.Configuration.TraktUsers.FirstOrDefault();

        if (traktUser == null)
        {
            return null;
        }

        string? url = null;
        try
        {
            url = await _traktApi.GetShowPosterUrl(traktUser, traktId);
        }
        catch (Exception ex)
        {
            // A missing poster is cosmetic; the widget falls back to a plain tile.
            _logger.LogDebug(ex, "Could not load a poster for Trakt show {TraktId}", traktId);
        }

        _posterCache[traktId] = new CachedPoster
        {
            Url = url,
            ExpiresAt = DateTime.UtcNow + (url == null ? PosterMissCacheDuration : PosterCacheDuration)
        };

        return url;
    }

    private static string GetRequestKey(int traktId, int seasonNumber)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{traktId}:{seasonNumber}");
    }

    /// <summary>
    /// Prefers the poster Jellyfin already holds for the series - the show itself is normally in the
    /// library, since the user watched an earlier season - and only falls back to Trakt.
    /// </summary>
    private string? GetImagePath(ContentItem item)
    {
        if (item.TvdbId.HasValue && item.TvdbId.Value != 0)
        {
            try
            {
                var series = _localLibraryService.FindSeriesByTvdbId(item.TvdbId.Value);
                if (series != null && series.HasImage(ImageType.Primary, 0))
                {
                    return string.Create(
                        CultureInfo.InvariantCulture,
                        $"Items/{series.Id:N}/Images/Primary?fillHeight=330&fillWidth=220&quality=90");
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not resolve a library poster for {Title}", item.Title);
            }
        }

        return item.TraktId == 0
            ? null
            : string.Create(CultureInfo.InvariantCulture, $"JellyNext/Widget/Poster/{item.TraktId}");
    }

    private sealed class CachedPoster
    {
        public string? Url { get; init; }

        public DateTime ExpiresAt { get; init; }

        public bool IsExpired => DateTime.UtcNow > ExpiresAt;
    }
}
