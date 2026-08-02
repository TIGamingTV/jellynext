using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyNext.Models.Common;
using Jellyfin.Plugin.JellyNext.Services.DownloadProviders;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
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
    /// How many candidates are checked before giving up, so a show with a lot of bad artwork cannot
    /// hold up the request.
    /// </summary>
    private const int MaxImageChecks = 4;

    /// <summary>
    /// How long a resolved image is reused before it is looked up again.
    /// </summary>
    private static readonly TimeSpan ImageCacheDuration = TimeSpan.FromDays(7);

    /// <summary>
    /// How long a show with no artwork anywhere is left alone before trying again.
    /// </summary>
    private static readonly TimeSpan ImageMissCacheDuration = TimeSpan.FromHours(12);

    /// <summary>
    /// How long to wait for an image host to answer before treating the artwork as unusable.
    /// </summary>
    private static readonly TimeSpan ImageCheckTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long to wait for the artwork itself, which is a real download rather than a probe.
    /// </summary>
    private static readonly TimeSpan ImageFetchTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Image types in the order the widget wants them.
    /// </summary>
    /// <remarks>
    /// The cards are portrait, because the thing being offered is a season and a season's picture is a
    /// poster everywhere Jellyfin and the metadata providers show one. So the poster comes first. A
    /// thumbnail or a backdrop is only taken when there is no poster: both are 16:9, and a series
    /// thumbnail in particular is frequently a scene still that reads as a random episode rather than
    /// as the show - which is exactly what the widget used to put on every card.
    /// </remarks>
    private static readonly ImageType[] PreferredImageTypes =
    {
        ImageType.Primary, ImageType.Thumb, ImageType.Backdrop
    };

    private readonly ILogger<NextSeasonsWidgetService> _logger;
    private readonly ContentCacheService _cacheService;
    private readonly LocalLibraryService _localLibraryService;
    private readonly DownloadProviderFactory _downloadProviderFactory;
    private readonly TraktApi _traktApi;
    private readonly IProviderManager _providerManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly VirtualLibrary.VirtualLibraryManager _virtualLibraryManager;

    // Not persisted, like the watchlist's request tracking: the durable answer to "do I have this
    // season" is the library, which the next content sync re-checks. This only keeps the button from
    // reading "Request" again the moment after it was pressed.
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, DateTime>> _requested = new();
    private readonly ConcurrentDictionary<string, CachedImage> _imageCache = new();

    // What the image endpoint needs to look a show up, recorded while the list is built. The endpoint
    // is reached by an anonymous <img> request carrying nothing but a Trakt ID and a season number,
    // and it always follows a render of the list, so this is populated by the time it is read.
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
    /// <param name="virtualLibraryManager">The virtual library manager.</param>
    public NextSeasonsWidgetService(
        ILogger<NextSeasonsWidgetService> logger,
        ContentCacheService cacheService,
        LocalLibraryService localLibraryService,
        DownloadProviderFactory downloadProviderFactory,
        TraktApi traktApi,
        IProviderManager providerManager,
        IHttpClientFactory httpClientFactory,
        VirtualLibrary.VirtualLibraryManager virtualLibraryManager)
    {
        _logger = logger;
        _cacheService = cacheService;
        _localLibraryService = localLibraryService;
        _downloadProviderFactory = downloadProviderFactory;
        _traktApi = traktApi;
        _providerManager = providerManager;
        _httpClientFactory = httpClientFactory;
        _virtualLibraryManager = virtualLibraryManager;
    }

    /// <summary>
    /// Gets the shows with a new season for a user, newest season first.
    /// </summary>
    /// <param name="userId">The Jellyfin user ID.</param>
    /// <returns>The widget items, limited to the configured item count.</returns>
    public IReadOnlyList<NextSeasonWidgetItem> GetItems(Guid userId)
    {
        var requestedForUser = _requested.GetOrAdd(userId, _ => new ConcurrentDictionary<string, DateTime>());

        return GetContentItems(userId)
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
                    Requested = requestedForUser.ContainsKey(GetRequestKey(item.TraktId, item.SeasonNumber!.Value)),
                    LibraryItemId = FindVirtualLibraryItem(userId, item)?.Id.ToString("N", CultureInfo.InvariantCulture)
                };
            })
            .ToList();
    }

    /// <summary>
    /// Finds the virtual library item Jellyfin created for a show's Next Seasons stub.
    /// </summary>
    /// <param name="userId">The user whose virtual library to look in.</param>
    /// <param name="item">The cached content item.</param>
    /// <returns>The show folder's library item, or null when it has not been scanned in.</returns>
    /// <remarks>
    /// The show folder rather than the stub file inside it: the folder is the item carrying the show's
    /// name and poster, which is what a card should be. Absent is the normal state on a server that
    /// never set the virtual library up, so this stays quiet about it - the widget's own cards do not
    /// need the item, only the Modular Home section and the card-matching in the client script do.
    /// </remarks>
    public BaseItem? FindVirtualLibraryItem(Guid userId, ContentItem item)
    {
        try
        {
            var folder = _virtualLibraryManager.GetNextSeasonShowFolderPath(userId, item);
            return folder == null ? null : _localLibraryService.FindByPath(folder, isFolder: true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not resolve the virtual library item for {Title}", item.Title);
            return null;
        }
    }

    /// <summary>
    /// Gets the cached Next Seasons content for a user, in the order the widget lists it.
    /// </summary>
    /// <param name="userId">The Jellyfin user ID.</param>
    /// <returns>The content items, limited to the configured item count.</returns>
    /// <remarks>
    /// Exposed so the Modular Home section can answer with the same shows, in the same order, under
    /// the same per-user filters as the widget, without a second definition of any of it.
    /// </remarks>
    public IReadOnlyList<ContentItem> GetContentItems(Guid userId)
    {
        var limit = Math.Clamp(Plugin.Instance?.Configuration.NextSeasonsWidgetLimit ?? 12, 1, 50);

        return _cacheService.GetCachedContent(userId, ProviderName)
            .Where(item => item.Type == ContentType.Show && item.SeasonNumber.HasValue)
            .OrderByDescending(item => item.SeasonFirstAired ?? DateTime.MinValue)
            .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
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
    /// Picks the artwork for one card: the season's own picture where there is one, the show's
    /// otherwise.
    /// </summary>
    /// <param name="traktId">The Trakt show ID.</param>
    /// <param name="seasonNumber">The season being offered, if the caller knows it.</param>
    /// <returns>What to show, or null when there is no artwork anywhere.</returns>
    /// <remarks>
    /// The whole chain lives on the server so that a card has exactly one URL to load and the client
    /// never has to know where the picture came from. In order:
    /// <list type="number">
    /// <item>the season as Jellyfin already knows it - with "display missing episodes" on, the season
    /// the user has not downloaded yet still exists as an item carrying the provider's season poster;</item>
    /// <item>the season as Jellyfin's metadata providers describe it, which is where a season poster
    /// comes from when the library has no entity for it yet - the usual case for a season that has
    /// just premiered;</item>
    /// <item>the show in the library, which is the same artwork the rest of the home screen shows;</item>
    /// <item>the show from the metadata providers, for a show the user does not have at all;</item>
    /// <item>Trakt, as the backstop.</item>
    /// </list>
    /// Steps 1 and 3 are library items and are answered with a Jellyfin image path, so the browser
    /// loads them through the server's own resizing and caching rather than through this plugin.
    /// </remarks>
    public async Task<WidgetImage?> ResolveImageAsync(int traktId, int? seasonNumber)
    {
        var cacheKey = GetImageCacheKey(traktId, seasonNumber);
        if (_imageCache.TryGetValue(cacheKey, out var cached) && !cached.IsExpired)
        {
            return cached.Image;
        }

        _imageLookups.TryGetValue(traktId, out var lookup);
        var image = await ResolveImageUncachedAsync(traktId, seasonNumber, lookup).ConfigureAwait(false);

        if (image == null)
        {
            // Worth saying out loud: a card with no artwork is the most visible failure the widget
            // has. Logged at most once every ImageMissCacheDuration per season.
            _logger.LogInformation(
                "No artwork available for {Title} season {Season} (Trakt {TraktId}) from the library, "
                + "Jellyfin's metadata providers or Trakt; the widget will show a plain tile",
                lookup?.Title ?? "unknown show",
                seasonNumber,
                traktId);
        }

        _imageCache[cacheKey] = new CachedImage
        {
            Image = image,
            ExpiresAt = DateTime.UtcNow + (image == null ? ImageMissCacheDuration : ImageCacheDuration)
        };

        return image;
    }

    /// <summary>
    /// Fetches artwork that lives outside Jellyfin so the plugin can serve it itself.
    /// </summary>
    /// <param name="url">The external image URL, as resolved by <see cref="ResolveImageAsync"/>.</param>
    /// <param name="traktId">The Trakt show ID, so a dead URL can be dropped from the cache.</param>
    /// <param name="seasonNumber">The season the URL was resolved for.</param>
    /// <returns>The image bytes and content type, or null when it could not be fetched.</returns>
    /// <remarks>
    /// The card used to be redirected to the image host, which fails invisibly whenever the browser
    /// cannot reach it - a reverse proxy sending <c>Content-Security-Policy: img-src 'self'</c>, an ad
    /// blocker, or filtered DNS all block third-party images while the server sees a perfectly
    /// successful lookup. Serving the bytes from here makes the picture as reachable as the rest of
    /// the library's artwork, which the browser is already loading.
    /// </remarks>
    public async Task<(byte[] Content, string ContentType)?> FetchExternalImageAsync(
        string url, int traktId, int? seasonNumber)
    {
        try
        {
            using var httpClient = _httpClientFactory.CreateClient(NamedClient.Default);
            httpClient.Timeout = ImageFetchTimeout;

            using var response = await httpClient.GetAsync(url).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // The URL passed its check when it was resolved, so this is artwork that has gone away
                // since. Drop it so the next request resolves again instead of serving nothing forever.
                _imageCache.TryRemove(GetImageCacheKey(traktId, seasonNumber), out _);
                _logger.LogDebug(
                    "Artwork for Trakt show {TraktId} is no longer available at {Url} ({Status})",
                    traktId,
                    url,
                    response.StatusCode);
                return null;
            }

            var content = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg";

            return (content, contentType);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not fetch the artwork for Trakt show {TraktId} from {Url}", traktId, url);
            return null;
        }
    }

    private async Task<WidgetImage?> ResolveImageUncachedAsync(
        int traktId, int? seasonNumber, ShowImageLookup? lookup)
    {
        var title = lookup?.Title ?? "unknown show";
        var series = lookup?.LibraryItemId is Guid libraryItemId
            ? _localLibraryService.FindItemById(libraryItemId) as Series
            : null;

        if (series != null && seasonNumber.HasValue)
        {
            var season = _localLibraryService.FindSeason(series, seasonNumber.Value);
            var seasonImage = BuildLibraryImagePath(season);
            if (seasonImage != null)
            {
                _logger.LogDebug("Artwork for {Title} came from the season in the library", title);
                return new WidgetImage { LibraryImagePath = seasonImage, Source = "library season" };
            }

            var seasonUrl = await FirstImageThatLoadsAsync(
                await GetSeasonImageCandidatesAsync(series, season, seasonNumber.Value).ConfigureAwait(false),
                "a metadata provider (season)",
                title).ConfigureAwait(false);

            if (seasonUrl != null)
            {
                return new WidgetImage { ExternalUrl = seasonUrl, Source = "metadata provider (season)" };
            }
        }

        var seriesImage = BuildLibraryImagePath(series);
        if (seriesImage != null)
        {
            _logger.LogDebug("Artwork for {Title} came from the show in the library", title);
            return new WidgetImage { LibraryImagePath = seriesImage, Source = "library show" };
        }

        var showUrl = await FirstImageThatLoadsAsync(
            await GetShowImageCandidatesAsync(lookup, series).ConfigureAwait(false),
            "a metadata provider",
            title).ConfigureAwait(false);

        if (showUrl != null)
        {
            return new WidgetImage { ExternalUrl = showUrl, Source = "metadata provider (show)" };
        }

        var traktUrl = await GetTraktImageUrlAsync(traktId).ConfigureAwait(false);
        traktUrl = await FirstImageThatLoadsAsync(
            traktUrl == null ? Array.Empty<string>() : new[] { traktUrl },
            "Trakt",
            title).ConfigureAwait(false);

        return traktUrl == null ? null : new WidgetImage { ExternalUrl = traktUrl, Source = "Trakt" };
    }

    /// <summary>
    /// Returns the first candidate that actually serves an image.
    /// </summary>
    /// <remarks>
    /// A URL that a provider offers is not necessarily one that resolves - a metadata entry with no
    /// file path produces a well-formed URL that 404s. Handing the browser one of those would waste
    /// the card, because by then the remaining candidates are out of reach, so each one is checked
    /// here while there is still something else to try. Only paid once per season, on a cache miss.
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
    /// Asks Jellyfin's metadata providers for the season's own artwork.
    /// </summary>
    /// <remarks>
    /// The season image providers key off the parent series' provider IDs and the season index, so
    /// they need a season that can find its series - which is why this only runs for a show that is in
    /// the library. Jellyfin's own season item is used as the probe where one exists, so the providers
    /// see the library's metadata language and options; otherwise a detached season pointed at the
    /// real series stands in.
    /// </remarks>
    private async Task<IReadOnlyList<string>> GetSeasonImageCandidatesAsync(
        Series series, Season? season, int seasonNumber)
    {
        try
        {
            var probe = season ?? new Season
            {
                Name = string.Create(CultureInfo.InvariantCulture, $"Season {seasonNumber}"),
                IndexNumber = seasonNumber,
                SeriesId = series.Id,
                ParentId = series.Id,
                SeriesName = series.Name
            };

            var images = await _providerManager
                .GetAvailableRemoteImages(probe, new RemoteImageQuery(string.Empty), CancellationToken.None)
                .ConfigureAwait(false);

            return PickImagesByPreference(images).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Season artwork lookup failed for {Title} season {Season}", series.Name, seasonNumber);
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Asks Jellyfin's metadata providers for the show's artwork.
    /// </summary>
    /// <remarks>
    /// For a show in the library the real item is passed, so the providers see the user's metadata
    /// language and library options. For one that is not, a detached <see cref="Series"/> carrying the
    /// show's provider IDs stands in - the image providers key off those IDs, not off library
    /// membership. If that is ever rejected, the identify-style search is asked instead, which only
    /// needs a name and an ID.
    /// </remarks>
    private async Task<IReadOnlyList<string>> GetShowImageCandidatesAsync(ShowImageLookup? lookup, Series? series)
    {
        if (lookup == null)
        {
            return Array.Empty<string>();
        }

        var candidates = new List<string>();

        try
        {
            BaseItem item = series ?? BuildProbeSeries(lookup);

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
    /// Orders a provider's images the same way the library's are preferred, best of each type first.
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

    /// <summary>
    /// Builds the Jellyfin image path for a library item's best available artwork.
    /// </summary>
    /// <remarks>
    /// Only the width is constrained. Asking for a height as well makes Jellyfin crop to that exact
    /// box, which turns a 16:9 backdrop into a slice of itself when the card is portrait; leaving the
    /// aspect ratio alone lets the client decide whether to cover or contain.
    /// </remarks>
    private static string? BuildLibraryImagePath(BaseItem? item)
    {
        if (item == null)
        {
            return null;
        }

        foreach (var imageType in PreferredImageTypes)
        {
            if (item.HasImage(imageType, 0))
            {
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"Items/{item.Id:N}/Images/{imageType}?fillWidth=400&quality=90");
            }
        }

        return null;
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

    /// <summary>
    /// Reports what the server sees for each card, so an artwork problem can be read off one page
    /// instead of guessed at from a blank tile.
    /// </summary>
    /// <param name="userId">The user whose widget contents to inspect.</param>
    /// <returns>One entry per listed show.</returns>
    public async Task<IReadOnlyList<WidgetDiagnostics>> GetDiagnosticsAsync(Guid userId)
    {
        var report = new List<WidgetDiagnostics>();

        foreach (var item in GetItems(userId))
        {
            var contentItem = _cacheService.GetCachedContent(userId, ProviderName)
                .FirstOrDefault(cached => cached.TraktId == item.TraktId);

            var diagnostics = new WidgetDiagnostics
            {
                Title = item.Title,
                TraktId = item.TraktId,
                TvdbId = item.TvdbId,
                TmdbId = contentItem?.TmdbId,
                ImdbId = contentItem?.ImdbId,
                SeasonNumber = item.SeasonNumber,
                ImagePath = item.ImagePath,
                FallbackImagePath = item.FallbackImagePath
            };

            try
            {
                var series = _localLibraryService.FindSeriesByAnyProviderId(
                    item.TvdbId, contentItem?.TmdbId, contentItem?.ImdbId);

                if (series != null)
                {
                    diagnostics.LibraryItemId = series.Id.ToString("N", CultureInfo.InvariantCulture);
                    diagnostics.LibraryImages = ListImages(series);

                    var season = _localLibraryService.FindSeason(series, item.SeasonNumber);
                    if (season != null)
                    {
                        diagnostics.SeasonItemId = season.Id.ToString("N", CultureInfo.InvariantCulture);
                        diagnostics.SeasonImages = ListImages(season);
                    }
                }
            }
            catch (Exception ex)
            {
                diagnostics.Error = ex.Message;
            }

            if (item.TraktId != 0)
            {
                var resolved = await ResolveImageAsync(item.TraktId, item.SeasonNumber).ConfigureAwait(false);
                diagnostics.ResolvedSource = resolved?.Source;
                diagnostics.ResolvedUrl = resolved?.ExternalUrl ?? resolved?.LibraryImagePath;
            }

            report.Add(diagnostics);
        }

        return report;
    }

    private static string[] ListImages(BaseItem item)
    {
        return PreferredImageTypes
            .Where(imageType => item.HasImage(imageType, 0))
            .Select(imageType => imageType.ToString())
            .ToArray();
    }

    private static string GetRequestKey(int traktId, int seasonNumber)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{traktId}:{seasonNumber}");
    }

    private static string GetImageCacheKey(int traktId, int? seasonNumber)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{traktId}:{seasonNumber?.ToString(CultureInfo.InvariantCulture) ?? "show"}");
    }

    /// <summary>
    /// Records what the artwork endpoint will need, and points the card at it.
    /// </summary>
    /// <remarks>
    /// The card gets a single plugin URL rather than a library path, because only the server can tell
    /// whether the better picture is the season's or the show's, and resolving that while the list is
    /// built would turn a row of twelve cards into twelve metadata lookups before anything is drawn.
    /// The library's own image path is still handed over as a fallback, so a card is never blank while
    /// the show sits in the library with artwork on it.
    /// </remarks>
    private (string? ImagePath, string? FallbackImagePath) GetImagePaths(ContentItem item)
    {
        var seasonNumber = item.SeasonNumber ?? 0;

        var pluginPath = item.TraktId == 0
            ? null
            : string.Create(
                CultureInfo.InvariantCulture,
                $"JellyNext/Widget/Poster/{item.TraktId}/{seasonNumber}");

        string? libraryPath = null;
        Guid? libraryItemId = null;

        try
        {
            var series = _localLibraryService.FindSeriesByAnyProviderId(item.TvdbId, item.TmdbId, item.ImdbId);
            if (series != null)
            {
                libraryItemId = series.Id;
                libraryPath = BuildLibraryImagePath(_localLibraryService.FindSeason(series, seasonNumber))
                    ?? BuildLibraryImagePath(series);

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

        if (libraryPath == null && pluginPath == null)
        {
            // Nothing to even attempt: the card can only be a name tile, and no image request will be
            // made, so without this the failure leaves no trace on the server at all.
            _logger.LogWarning(
                "No artwork source for {Title}: it is not in the library and has no Trakt ID "
                + "(TVDB {TvdbId}, TMDB {TmdbId}, IMDB {ImdbId})",
                item.Title,
                item.TvdbId,
                item.TmdbId,
                item.ImdbId ?? "none");
        }

        return (pluginPath ?? libraryPath, pluginPath == null ? null : libraryPath);
    }

    /// <summary>
    /// Where one card's artwork lives.
    /// </summary>
    public class WidgetImage
    {
        /// <summary>
        /// Gets or sets the Jellyfin API path of a library item's image, if that is the best picture.
        /// </summary>
        /// <remarks>
        /// Answered as a same-origin redirect rather than copied through the plugin, so the image goes
        /// through Jellyfin's own resizing and caching like every other picture on the page.
        /// </remarks>
        public string? LibraryImagePath { get; set; }

        /// <summary>
        /// Gets or sets the URL of artwork outside Jellyfin, which the plugin serves itself.
        /// </summary>
        public string? ExternalUrl { get; set; }

        /// <summary>
        /// Gets or sets a short description of where the picture came from, for diagnostics.
        /// </summary>
        public string? Source { get; set; }
    }

    /// <summary>
    /// What the server resolved for one card, for the artwork check on the configuration page.
    /// </summary>
    public class WidgetDiagnostics
    {
        /// <summary>Gets or sets the show title.</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>Gets or sets the Trakt ID.</summary>
        public int TraktId { get; set; }

        /// <summary>Gets or sets the TVDB ID.</summary>
        public int? TvdbId { get; set; }

        /// <summary>Gets or sets the TMDB ID.</summary>
        public int? TmdbId { get; set; }

        /// <summary>Gets or sets the IMDB ID.</summary>
        public string? ImdbId { get; set; }

        /// <summary>Gets or sets the season being offered.</summary>
        public int SeasonNumber { get; set; }

        /// <summary>Gets or sets the matched library item ID, if the show is in the library.</summary>
        public string? LibraryItemId { get; set; }

        /// <summary>Gets or sets the image types the library item actually holds.</summary>
        public string[] LibraryImages { get; set; } = Array.Empty<string>();

        /// <summary>Gets or sets the matched season item ID, if Jellyfin knows the season.</summary>
        public string? SeasonItemId { get; set; }

        /// <summary>Gets or sets the image types the season item actually holds.</summary>
        public string[] SeasonImages { get; set; } = Array.Empty<string>();

        /// <summary>Gets or sets the path the card loads first.</summary>
        public string? ImagePath { get; set; }

        /// <summary>Gets or sets the path the card falls back to.</summary>
        public string? FallbackImagePath { get; set; }

        /// <summary>Gets or sets which step of the chain produced the picture.</summary>
        public string? ResolvedSource { get; set; }

        /// <summary>Gets or sets what that step resolved to.</summary>
        public string? ResolvedUrl { get; set; }

        /// <summary>Gets or sets the error, if the lookup itself failed.</summary>
        public string? Error { get; set; }
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

    private sealed class CachedImage
    {
        public WidgetImage? Image { get; init; }

        public DateTime ExpiresAt { get; init; }

        public bool IsExpired => DateTime.UtcNow > ExpiresAt;
    }
}
