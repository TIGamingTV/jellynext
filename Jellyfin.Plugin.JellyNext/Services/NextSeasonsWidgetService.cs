using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyNext.Helpers;
using Jellyfin.Plugin.JellyNext.Models.Common;
using Jellyfin.Plugin.JellyNext.Services.DownloadProviders;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
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

    /// <summary>
    /// How long to wait for an image host to answer before treating the artwork as unusable.
    /// </summary>
    private static readonly TimeSpan ImageCheckTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How many candidates are checked before giving up, so a show with a lot of bad artwork cannot
    /// hold up the request.
    /// </summary>
    private const int MaxImageChecks = 4;

    /// <summary>
    /// Library image types in the order the widget wants them.
    /// </summary>
    /// <remarks>
    /// Backdrop before Thumb: both are 16:9, but a series thumbnail is often a scene still that reads
    /// as "some episode" rather than as the show, while a backdrop is always key art. The poster is
    /// last - it fits the card badly, but a recognisable image beats a blank tile.
    /// </remarks>
    private static readonly ImageType[] PreferredImageTypes =
    {
        ImageType.Backdrop, ImageType.Thumb, ImageType.Primary
    };

    private readonly ILogger<NextSeasonsWidgetService> _logger;
    private readonly ContentCacheService _cacheService;
    private readonly LocalLibraryService _localLibraryService;
    private readonly DownloadProviderFactory _downloadProviderFactory;
    private readonly TraktApi _traktApi;
    private readonly IProviderManager _providerManager;
    private readonly IHttpClientFactory _httpClientFactory;

    // Not persisted, like the watchlist's request tracking: the durable answer to "do I have this
    // season" is the library, which the next content sync re-checks. This only keeps the button from
    // reading "Request" again the moment after it was pressed.
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, DateTime>> _requested = new();
    private readonly ConcurrentDictionary<int, CachedPoster> _posterCache = new();

    // What the image endpoint needs to look a show up, recorded while the list is built. The endpoint
    // is reached by an anonymous <img> request carrying nothing but a Trakt ID, and it always follows
    // a render of the list, so this is populated by the time it is read.
    private readonly ConcurrentDictionary<int, ShowImageLookup> _imageLookups = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="NextSeasonsWidgetService"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="cacheService">The content cache service.</param>
    /// <param name="localLibraryService">The local library service.</param>
    /// <param name="downloadProviderFactory">The download provider factory.</param>
    /// <param name="traktApi">The Trakt API service.</param>
    /// <param name="providerManager">The metadata provider manager.</param>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    public NextSeasonsWidgetService(
        ILogger<NextSeasonsWidgetService> logger,
        ContentCacheService cacheService,
        LocalLibraryService localLibraryService,
        DownloadProviderFactory downloadProviderFactory,
        TraktApi traktApi,
        IProviderManager providerManager,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _cacheService = cacheService;
        _localLibraryService = localLibraryService;
        _downloadProviderFactory = downloadProviderFactory;
        _traktApi = traktApi;
        _providerManager = providerManager;
        _httpClientFactory = httpClientFactory;
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
            .Select(item =>
            {
                var images = GetImagePaths(item);
                return new NextSeasonWidgetItem
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
                    ImagePath = images.ImagePath,
                    FallbackImagePath = images.FallbackImagePath,
                    Requested = requestedForUser.ContainsKey(GetRequestKey(item.TraktId, item.SeasonNumber!.Value))
                };
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
    /// Resolves artwork for a show the Jellyfin library has no usable image for.
    /// </summary>
    /// <param name="traktId">The Trakt show ID.</param>
    /// <returns>An absolute image URL, or null when no artwork is available anywhere.</returns>
    /// <remarks>
    /// Jellyfin's own metadata providers are asked first, so the picture comes from the same place as
    /// every other image on the home screen and looks like it belongs there; Trakt is the backstop for
    /// shows those providers cannot illustrate. Resolved lazily behind the redirect endpoint rather
    /// than while building the list, so a row of twelve cards never turns into twelve metadata lookups
    /// before anything is drawn.
    /// </remarks>
    public async Task<string?> GetExternalImageUrlAsync(int traktId)
    {
        if (_posterCache.TryGetValue(traktId, out var cached) && !cached.IsExpired)
        {
            return cached.Url;
        }

        _imageLookups.TryGetValue(traktId, out var lookup);
        var title = lookup?.Title ?? "unknown show";

        var url = await FirstImageThatLoadsAsync(
            await GetProviderImageCandidatesAsync(lookup).ConfigureAwait(false),
            "a metadata provider",
            title).ConfigureAwait(false);

        if (url == null)
        {
            var traktUrl = await GetTraktImageUrlAsync(traktId).ConfigureAwait(false);
            url = await FirstImageThatLoadsAsync(
                traktUrl == null ? Array.Empty<string>() : new[] { traktUrl },
                "Trakt",
                title).ConfigureAwait(false);
        }

        if (url == null)
        {
            // Worth saying out loud: a card with no artwork is the most visible failure the widget
            // has. Logged at most once every PosterMissCacheDuration per show.
            _logger.LogInformation(
                "No artwork available for {Title} (Trakt {TraktId}) from Jellyfin's metadata providers "
                + "or Trakt; the widget will show a plain tile",
                title,
                traktId);
        }

        _posterCache[traktId] = new CachedPoster
        {
            Url = url,
            ExpiresAt = DateTime.UtcNow + (url == null ? PosterMissCacheDuration : PosterCacheDuration)
        };

        return url;
    }

    /// <summary>
    /// Returns the first candidate that actually serves an image.
    /// </summary>
    /// <remarks>
    /// A URL that a provider offers is not necessarily one that resolves - a metadata entry with no
    /// file path produces a well-formed URL that 404s. Redirecting the browser to it would waste the
    /// card, because by then the remaining candidates are out of reach, so each one is checked here
    /// while there is still something else to try. Only paid once per show, on a cache miss.
    /// </remarks>
    private async Task<string?> FirstImageThatLoadsAsync(IEnumerable<string> candidates, string source, string title)
    {
        foreach (var candidate in candidates.Where(url => !string.IsNullOrWhiteSpace(url)).Take(MaxImageChecks))
        {
            if (await IsReachableAsync(candidate).ConfigureAwait(false))
            {
                _logger.LogDebug("Artwork for {Title} came from {Source}: {Url}", title, source, candidate);
                return candidate;
            }

            _logger.LogDebug("Artwork candidate for {Title} from {Source} did not load: {Url}", title, source, candidate);
        }

        return null;
    }

    private async Task<bool> IsReachableAsync(string url)
    {
        try
        {
            using var httpClient = _httpClientFactory.CreateClient(NamedClient.Default);
            httpClient.Timeout = ImageCheckTimeout;

            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode
                || response.StatusCode == HttpStatusCode.NotFound
                || response.StatusCode == HttpStatusCode.Gone)
            {
                return response.IsSuccessStatusCode;
            }

            // Anything else is more likely a host that dislikes HEAD than a missing image, so ask for
            // the headers of a GET instead of downloading the whole file.
            using var getRequest = new HttpRequestMessage(HttpMethod.Get, url);
            using var getResponse = await httpClient
                .SendAsync(getRequest, HttpCompletionOption.ResponseHeadersRead)
                .ConfigureAwait(false);

            return getResponse.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not reach the artwork at {Url}", url);
            return false;
        }
    }

    /// <summary>
    /// Asks Jellyfin's configured metadata providers for the show's artwork.
    /// </summary>
    /// <remarks>
    /// For a show in the library the real item is passed, so the providers see the user's metadata
    /// language and library options. For one that is not, a detached <see cref="Series"/> carrying the
    /// show's provider IDs stands in - the image providers key off those IDs, not off library
    /// membership. If that is ever rejected, the identify-style search is asked instead, which only
    /// needs a name and an ID.
    /// </remarks>
    private async Task<IReadOnlyList<string>> GetProviderImageCandidatesAsync(ShowImageLookup? lookup)
    {
        if (lookup == null)
        {
            return Array.Empty<string>();
        }

        var candidates = new List<string>();

        try
        {
            var item = lookup.LibraryItemId.HasValue
                ? _localLibraryService.FindItemById(lookup.LibraryItemId.Value)
                : null;

            item ??= BuildProbeSeries(lookup);

            var images = await _providerManager
                .GetAvailableRemoteImages(item, new RemoteImageQuery(string.Empty), CancellationToken.None)
                .ConfigureAwait(false);

            candidates.AddRange(PickImagesByPreference(images));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Remote image lookup failed for {Title}", lookup.Title);
        }

        try
        {
            var results = await _providerManager.GetRemoteSearchResults<Series, SeriesInfo>(
                new RemoteSearchQuery<SeriesInfo>
                {
                    SearchInfo = new SeriesInfo
                    {
                        Name = lookup.Title,
                        Year = lookup.Year,
                        ProviderIds = BuildProviderIds(lookup)
                    }
                },
                CancellationToken.None).ConfigureAwait(false);

            candidates.AddRange(results
                .Select(result => result.ImageUrl)
                .Where(imageUrl => !string.IsNullOrEmpty(imageUrl))
                .Select(imageUrl => imageUrl!));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Metadata search failed for {Title}", lookup.Title);
        }

        return candidates;
    }

    /// <summary>
    /// Falls back to Trakt's artwork.
    /// </summary>
    private async Task<string?> GetTraktImageUrlAsync(int traktId)
    {
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

        try
        {
            return await _traktApi.GetShowImageUrl(traktUser, traktId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Missing artwork is cosmetic; the widget falls back to a plain tile.
            _logger.LogDebug(ex, "Could not load Trakt artwork for show {TraktId}", traktId);
            return null;
        }
    }

    /// <summary>
    /// Orders the provider's images the same way the library's are preferred, best of each type first.
    /// </summary>
    private static IEnumerable<string> PickImagesByPreference(IEnumerable<RemoteImageInfo> images)
    {
        var candidates = images.Where(image => !string.IsNullOrEmpty(image.Url)).ToList();

        return PreferredImageTypes
            .Select(imageType => candidates
                .Where(image => image.Type == imageType)
                .OrderByDescending(image => image.CommunityRating ?? 0)
                .ThenByDescending(image => image.Width ?? 0)
                .FirstOrDefault())
            .Where(image => image != null)
            .Select(image => image!.Url);
    }

    private static Dictionary<string, string> BuildProviderIds(ShowImageLookup lookup)
    {
        var providerIds = new Dictionary<string, string>();

        if (lookup.TvdbId.HasValue)
        {
            providerIds[MetadataProvider.Tvdb.ToString()] = lookup.TvdbId.Value.ToString(CultureInfo.InvariantCulture);
        }

        if (lookup.TmdbId.HasValue)
        {
            providerIds[MetadataProvider.Tmdb.ToString()] = lookup.TmdbId.Value.ToString(CultureInfo.InvariantCulture);
        }

        if (!string.IsNullOrEmpty(lookup.ImdbId))
        {
            providerIds[MetadataProvider.Imdb.ToString()] = lookup.ImdbId;
        }

        return providerIds;
    }

    private static Series BuildProbeSeries(ShowImageLookup lookup)
    {
        var series = new Series { Name = lookup.Title, ProductionYear = lookup.Year };

        foreach (var providerId in BuildProviderIds(lookup))
        {
            series.SetProviderId(providerId.Key, providerId.Value);
        }

        return series;
    }

    private static string GetRequestKey(int traktId, int seasonNumber)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{traktId}:{seasonNumber}");
    }

    /// <summary>
    /// Picks the artwork for a card: the show's own images from the Jellyfin library, with Trakt as a
    /// second chance.
    /// </summary>
    /// <remarks>
    /// The library is preferred because the show itself is normally there - the user watched an
    /// earlier season - and it is the same artwork the rest of their home screen shows. Both paths are
    /// returned so the widget can retry with Trakt if the library image turns out to be missing.
    /// </remarks>
    private (string? ImagePath, string? FallbackImagePath) GetImagePaths(ContentItem item)
    {
        var externalPath = item.TraktId == 0
            ? null
            : string.Create(CultureInfo.InvariantCulture, $"JellyNext/Widget/Poster/{item.TraktId}");

        string? libraryPath = null;
        var libraryImageIsWide = false;
        Guid? libraryItemId = null;

        try
        {
            var series = _localLibraryService.FindSeriesByAnyProviderId(item.TvdbId, item.TmdbId, item.ImdbId);
            if (series != null)
            {
                libraryItemId = series.Id;

                foreach (var imageType in PreferredImageTypes)
                {
                    if (series.HasImage(imageType, 0))
                    {
                        libraryPath = string.Create(
                            CultureInfo.InvariantCulture,
                            $"Items/{series.Id:N}/Images/{imageType}?fillWidth=560&fillHeight=315&quality=90");
                        libraryImageIsWide = imageType != ImageType.Primary;
                        break;
                    }
                }

                if (libraryPath == null)
                {
                    _logger.LogDebug("{Title} is in the library but has no artwork yet", item.Title);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not resolve library artwork for {Title}", item.Title);
        }

        if (item.TraktId != 0)
        {
            _imageLookups[item.TraktId] = new ShowImageLookup
            {
                Title = item.Title,
                Year = item.Year,
                TvdbId = item.TvdbId,
                TmdbId = item.TmdbId,
                ImdbId = item.ImdbId,
                LibraryItemId = libraryItemId
            };
        }

        // A poster is the library's least useful image for a 16:9 card, so a wide one from the
        // metadata providers is tried ahead of it and the poster becomes the backstop. Wide library
        // artwork always wins - it is the picture the rest of the home screen is already showing.
        return libraryImageIsWide
            ? (libraryPath, externalPath)
            : (externalPath ?? libraryPath, externalPath == null ? null : libraryPath);
    }

    /// <summary>
    /// What is needed to ask a metadata provider for a show's artwork.
    /// </summary>
    private sealed class ShowImageLookup
    {
        public string Title { get; init; } = string.Empty;

        public int? Year { get; init; }

        public int? TvdbId { get; init; }

        public int? TmdbId { get; init; }

        public string? ImdbId { get; init; }

        public Guid? LibraryItemId { get; init; }
    }

    private sealed class CachedPoster
    {
        public string? Url { get; init; }

        public DateTime ExpiresAt { get; init; }

        public bool IsExpired => DateTime.UtcNow > ExpiresAt;
    }
}
