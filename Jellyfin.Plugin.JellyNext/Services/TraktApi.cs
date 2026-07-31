using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyNext.Configuration;
using Jellyfin.Plugin.JellyNext.Models.Common;
using Jellyfin.Plugin.JellyNext.Models.Radarr;
using Jellyfin.Plugin.JellyNext.Models.Sonarr;
using Jellyfin.Plugin.JellyNext.Models.Trakt;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyNext.Services;

/// <summary>
/// Service for interacting with the Trakt API.
/// </summary>
public class TraktApi
{
    private const string TraktApiBaseUrl = "https://api.trakt.tv";
    private const string DeviceCodeEndpoint = "/oauth/device/code";
    private const string DeviceTokenEndpoint = "/oauth/device/token";
    private const string RefreshTokenEndpoint = "/oauth/token";

    private const string JellyNextClientId = "2c2621eef7a2c82a221f7a03c65bfa0088555699ebeb4cefe1ee2490c8245864";
    private const string JellyNextClientSecret = "d2fa3baeafd861a7e024cbb53b0b9bf2451db8f56e8ca8ee650324963c2c967c";

    // The official jellyfin-plugin-trakt application credentials, which that plugin publishes as
    // public constants in Trakt/Api/TraktURIs.cs. Presenting these makes Trakt count JellyNext and
    // the official plugin as a single connected app, which is what a free Trakt account allows.
    private const string OfficialPluginClientId = "bfdd2e032c30c35b368f97ef4ec81587b899bcb028b91a1d4ba5589a4b6a7267";
    private const string OfficialPluginClientSecret = "bf9fce37cf45c1de91da009e7ac6fca905a35d7a718bf65a52f92199073a2503";

    private readonly ILogger<TraktApi> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TraktPluginBridge _traktPluginBridge;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _refreshLocks = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="TraktApi"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="traktPluginBridge">Bridge to the official Trakt plugin's stored tokens.</param>
    public TraktApi(
        ILogger<TraktApi> logger,
        IHttpClientFactory httpClientFactory,
        TraktPluginBridge traktPluginBridge)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _traktPluginBridge = traktPluginBridge;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        };
    }

    /// <summary>
    /// Gets the configured Trakt authorization mode.
    /// </summary>
    public static TraktAuthMode AuthMode =>
        Plugin.Instance?.Configuration.TraktAuthMode ?? TraktAuthMode.Standalone;

    /// <summary>
    /// Gets a value indicating whether tokens are borrowed from the official Trakt plugin.
    /// </summary>
    public static bool UsesSharedToken => AuthMode == TraktAuthMode.SharedTraktPluginToken;

    /// <summary>
    /// Gets the client id presented to Trakt under the active authorization mode.
    /// </summary>
    private static string ClientId =>
        AuthMode == TraktAuthMode.Standalone ? JellyNextClientId : OfficialPluginClientId;

    /// <summary>
    /// Gets the client secret presented to Trakt under the active authorization mode.
    /// </summary>
    private static string ClientSecret =>
        AuthMode == TraktAuthMode.Standalone ? JellyNextClientSecret : OfficialPluginClientSecret;

    /// <summary>
    /// Initiates the OAuth device authorization flow.
    /// </summary>
    /// <param name="traktUser">The Trakt user configuration.</param>
    /// <returns>The user code to display to the user.</returns>
    public async Task<string> AuthorizeDevice(TraktUser traktUser)
    {
        if (UsesSharedToken)
        {
            throw new InvalidOperationException(
                "Device authorization is disabled while JellyNext shares the Trakt plugin's token. "
                + "Link the account in the Trakt plugin instead.");
        }

        var request = new { client_id = ClientId };

        using var httpClient = _httpClientFactory.CreateClient(NamedClient.Default);
        httpClient.BaseAddress = new Uri(TraktApiBaseUrl);
        httpClient.DefaultRequestHeaders.Add("trakt-api-version", "2");
        httpClient.DefaultRequestHeaders.Add("trakt-api-key", ClientId);

        var response = await httpClient.PostAsJsonAsync(DeviceCodeEndpoint, request);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogError("Trakt API error: Status={Status}, Content={Content}", response.StatusCode, errorContent);
        }

        response.EnsureSuccessStatusCode();

        var deviceCode = await response.Content.ReadFromJsonAsync<TraktDeviceCode>(_jsonOptions);
        if (deviceCode == null)
        {
            throw new InvalidOperationException("Failed to obtain device code from Trakt");
        }

        // Start background polling task and track it
        var pollingTask = Task.Run(() => PollForAccessToken(deviceCode, traktUser));
        Plugin.Instance?.PollingTasks.TryAdd(traktUser.LinkedMbUserId, pollingTask);

        return deviceCode.UserCode;
    }

    /// <summary>
    /// Polls Trakt for access token completion.
    /// </summary>
    /// <param name="deviceCode">The device code from the initial authorization.</param>
    /// <param name="traktUser">The Trakt user configuration.</param>
    /// <returns>True if authorization succeeded, false otherwise.</returns>
    public async Task<bool> PollForAccessToken(TraktDeviceCode deviceCode, TraktUser traktUser)
    {
        var request = new
        {
            code = deviceCode.DeviceCode,
            client_id = ClientId,
            client_secret = ClientSecret
        };

        var pollingInterval = deviceCode.Interval;
        var expiresAt = DateTime.UtcNow.AddSeconds(deviceCode.ExpiresIn);

        using var httpClient = _httpClientFactory.CreateClient(NamedClient.Default);
        httpClient.BaseAddress = new Uri(TraktApiBaseUrl);
        httpClient.DefaultRequestHeaders.Add("trakt-api-version", "2");
        httpClient.DefaultRequestHeaders.Add("trakt-api-key", ClientId);

        while (DateTime.UtcNow < expiresAt)
        {
            try
            {
                var response = await httpClient.PostAsJsonAsync(DeviceTokenEndpoint, request);

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    var accessToken = await response.Content.ReadFromJsonAsync<TraktUserAccessToken>(_jsonOptions);
                    if (accessToken != null)
                    {
                        traktUser.AccessToken = accessToken.AccessToken;
                        traktUser.RefreshToken = accessToken.RefreshToken;
                        traktUser.AccessTokenExpiration = DateTime.Now.AddSeconds(accessToken.ExpirationWithBuffer);

                        Plugin.Instance?.SaveConfiguration();
                        _logger.LogInformation("Successfully authorized Trakt user {UserId}", traktUser.LinkedMbUserId);
                        return true;
                    }
                }
                else if (response.StatusCode == HttpStatusCode.BadRequest)
                {
                    // Still pending user authorization
                    _logger.LogDebug("Waiting for user to authorize device...");
                }
                else if (response.StatusCode == (HttpStatusCode)418)
                {
                    // User denied authorization
                    _logger.LogWarning("User denied Trakt authorization");
                    return false;
                }
                else if (response.StatusCode == HttpStatusCode.Gone)
                {
                    // Device code expired
                    _logger.LogWarning("Trakt device code expired");
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error polling for Trakt access token");
            }

            await Task.Delay(pollingInterval * 1000);
        }

        _logger.LogWarning("Trakt authorization timed out");
        return false;
    }

    /// <summary>
    /// Refreshes the user's access token using the refresh token.
    /// </summary>
    /// <param name="traktUser">The Trakt user configuration.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task RefreshUserAccessToken(TraktUser traktUser)
    {
        if (UsesSharedToken)
        {
            // The official Trakt plugin owns the token lifecycle in shared mode. Refreshing JellyNext's
            // own (empty) token here would be meaningless; RefreshSharedTokenAsync handles that case.
            _logger.LogDebug("Skipping JellyNext token refresh: the Trakt plugin owns the token");
            return;
        }

        if (string.IsNullOrWhiteSpace(traktUser.RefreshToken))
        {
            _logger.LogError("Attempted to refresh Trakt token but no refresh token was available");
            return;
        }

        var accessToken = await RequestTokenRefresh(traktUser.RefreshToken, traktUser.LinkedMbUserId);
        if (accessToken != null)
        {
            traktUser.AccessToken = accessToken.AccessToken;
            traktUser.RefreshToken = accessToken.RefreshToken;
            traktUser.AccessTokenExpiration = DateTime.Now.AddSeconds(accessToken.ExpirationWithBuffer);

            Plugin.Instance?.SaveConfiguration();
            _logger.LogInformation("Successfully refreshed Trakt access token for user {UserId}", traktUser.LinkedMbUserId);
        }
    }

    /// <summary>
    /// Ensures the user's access token is valid, refreshing if necessary.
    /// </summary>
    /// <param name="traktUser">The Trakt user configuration.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task EnsureValidAccessToken(TraktUser traktUser)
    {
        if (UsesSharedToken)
        {
            // JellyNext holds no token of its own in shared mode.
            return;
        }

        if (DateTime.Now >= traktUser.AccessTokenExpiration)
        {
            traktUser.AccessToken = string.Empty;
            await RefreshUserAccessToken(traktUser);
        }
    }

    /// <summary>
    /// Creates an HTTP client with proper Trakt API headers.
    /// </summary>
    /// <param name="traktUser">The Trakt user configuration (optional, for authenticated requests).</param>
    /// <returns>Configured HTTP client.</returns>
    /// <exception cref="TraktAuthenticationException">
    /// Thrown when no usable token is available for the user.
    /// </exception>
    public async Task<HttpClient> CreateTraktClient(TraktUser? traktUser = null)
    {
        var httpClient = _httpClientFactory.CreateClient();
        httpClient.BaseAddress = new Uri(TraktApiBaseUrl);
        httpClient.DefaultRequestHeaders.Add("trakt-api-version", "2");
        httpClient.DefaultRequestHeaders.Add("trakt-api-key", ClientId);
        httpClient.DefaultRequestHeaders.Add("User-Agent", "JellyNext/1.0");
        httpClient.DefaultRequestHeaders.Add("Accept", "application/json");

        if (traktUser != null)
        {
            var accessToken = await ResolveAccessTokenAsync(traktUser);

            if (!string.IsNullOrEmpty(accessToken))
            {
                httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            }
        }

        return httpClient;
    }

    /// <summary>
    /// Resolves the access token to present for a user under the active authorization mode.
    /// </summary>
    /// <param name="traktUser">The Trakt user configuration.</param>
    /// <returns>The access token, or null when the user is simply not linked.</returns>
    /// <exception cref="TraktAuthenticationException">
    /// Thrown in shared mode when the token is missing or expired and cannot be renewed, so callers
    /// skip the cycle instead of treating the user as having no content.
    /// </exception>
    private async Task<string?> ResolveAccessTokenAsync(TraktUser traktUser)
    {
        if (!UsesSharedToken)
        {
            await EnsureValidAccessToken(traktUser);
            return traktUser.AccessToken;
        }

        var userId = traktUser.LinkedMbUserId;
        var token = _traktPluginBridge.GetToken(userId);

        if (token == null)
        {
            throw new TraktAuthenticationException(
                _traktPluginBridge.IsAvailable
                    ? $"The Trakt plugin holds no access token for Jellyfin user {userId}. Link the account there first."
                    : "The official Trakt plugin is not installed or not enabled, so JellyNext has no token to share.");
        }

        if (!token.IsExpired)
        {
            return token.AccessToken;
        }

        var refreshed = await RefreshSharedTokenAsync(userId, token);
        if (refreshed == null)
        {
            throw new TraktAuthenticationException(
                $"The Trakt plugin's access token for Jellyfin user {userId} has expired and could not be renewed. "
                + "Skipping this cycle; it will be retried once the Trakt plugin refreshes it.");
        }

        return refreshed;
    }

    /// <summary>
    /// Refreshes a token borrowed from the official Trakt plugin and writes the rotated pair back.
    /// </summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="expiredToken">The expired token snapshot.</param>
    /// <returns>The new access token, or null when the refresh was skipped or failed.</returns>
    /// <remarks>
    /// Trakt refresh tokens are single use and rotate on every refresh, so the refresh is only
    /// attempted when the rotated pair can be handed straight back to the official plugin. Otherwise
    /// JellyNext would consume the refresh token and leave the official plugin holding a dead one.
    /// </remarks>
    private async Task<string?> RefreshSharedTokenAsync(Guid userId, TraktPluginToken expiredToken)
    {
        if (!(Plugin.Instance?.Configuration.AllowSharedTokenRefresh ?? true))
        {
            _logger.LogWarning(
                "The Trakt plugin's token for user {UserId} is expired and shared-token refresh is "
                + "disabled. Waiting for the Trakt plugin to refresh it",
                userId);
            return null;
        }

        if (string.IsNullOrWhiteSpace(expiredToken.RefreshToken))
        {
            _logger.LogWarning(
                "The Trakt plugin has no refresh token for user {UserId}; the account must be re-linked there",
                userId);
            return null;
        }

        if (!_traktPluginBridge.CanPersistToken(userId))
        {
            _logger.LogWarning(
                "Refusing to refresh the Trakt plugin's token for user {UserId} because the rotated "
                + "token could not be written back. Trakt refresh tokens are single use, so refreshing "
                + "without write-back would break the Trakt plugin",
                userId);
            return null;
        }

        var refreshLock = _refreshLocks.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
        await refreshLock.WaitAsync();

        try
        {
            // Another provider running in parallel may have refreshed while we waited.
            var current = _traktPluginBridge.GetToken(userId);
            if (current != null && !current.IsExpired)
            {
                return current.AccessToken;
            }

            var refreshToken = current?.RefreshToken ?? expiredToken.RefreshToken;
            var accessToken = await RequestTokenRefresh(refreshToken, userId);
            if (accessToken == null)
            {
                return null;
            }

            var expiration = DateTime.Now.AddSeconds(accessToken.ExpirationWithBuffer);
            if (!_traktPluginBridge.TryPersistToken(userId, accessToken.AccessToken, accessToken.RefreshToken, expiration))
            {
                // The pair is rotated regardless; surface it so this cycle still works even though
                // persistence failed. TryPersistToken has already logged the severity.
                _logger.LogWarning(
                    "Using the refreshed Trakt token for user {UserId} even though it could not be fully persisted",
                    userId);
            }

            return accessToken.AccessToken;
        }
        finally
        {
            refreshLock.Release();
        }
    }

    private async Task<TraktUserAccessToken?> RequestTokenRefresh(string refreshToken, Guid userId)
    {
        var request = new TraktUserRefreshTokenRequest
        {
            RefreshToken = refreshToken,
            ClientId = ClientId,
            ClientSecret = ClientSecret
        };

        try
        {
            using var httpClient = _httpClientFactory.CreateClient(NamedClient.Default);
            httpClient.BaseAddress = new Uri(TraktApiBaseUrl);
            httpClient.DefaultRequestHeaders.Add("trakt-api-version", "2");
            httpClient.DefaultRequestHeaders.Add("trakt-api-key", ClientId);
            httpClient.DefaultRequestHeaders.Add("User-Agent", "JellyNext/1.0");
            httpClient.DefaultRequestHeaders.Add("Accept", "application/json");

            var response = await httpClient.PostAsJsonAsync(RefreshTokenEndpoint, request);
            response.EnsureSuccessStatusCode();

            return await response.Content.ReadFromJsonAsync<TraktUserAccessToken>(_jsonOptions);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to refresh Trakt access token for user {UserId}", userId);
            return null;
        }
    }

    /// <summary>
    /// Throws when Trakt rejected the credentials, so the caller skips the cycle rather than
    /// caching an empty result.
    /// </summary>
    /// <param name="response">The Trakt response.</param>
    /// <param name="operation">A description of the attempted call, used in the message.</param>
    /// <exception cref="TraktAuthenticationException">Thrown on 401 Unauthorized.</exception>
    private void ThrowIfUnauthorized(HttpResponseMessage response, string operation)
    {
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return;
        }

        var hint = UsesSharedToken
            ? "The token borrowed from the Trakt plugin was rejected. JellyNext will not re-authorize; "
              + "the Trakt plugin owns the token lifecycle and this will recover once it refreshes."
            : "Re-link the Trakt account for this user.";

        _logger.LogWarning("Trakt rejected the request for {Operation} with 401. {Hint}", operation, hint);

        throw new TraktAuthenticationException($"Trakt returned 401 for {operation}. {hint}");
    }

    /// <summary>
    /// Gets personalized movie recommendations for a user.
    /// </summary>
    /// <param name="traktUser">The Trakt user configuration.</param>
    /// <param name="ignoreCollected">Whether to ignore collected movies.</param>
    /// <param name="ignoreWatchlisted">Whether to ignore watchlisted movies.</param>
    /// <param name="limit">Maximum number of recommendations to return (default: 10, max: 100).</param>
    /// <returns>List of recommended movies.</returns>
    public async Task<TraktMovie[]> GetMovieRecommendations(
        TraktUser traktUser,
        bool ignoreCollected = true,
        bool ignoreWatchlisted = false,
        int limit = 10)
    {
        var queryParams = $"?limit={limit}&extended=full";
        if (ignoreCollected)
        {
            queryParams += "&ignore_collected=true";
        }

        if (ignoreWatchlisted)
        {
            queryParams += "&ignore_watchlisted=true";
        }

        using var httpClient = await CreateTraktClient(traktUser);
        var response = await httpClient.GetAsync($"/recommendations/movies{queryParams}");

        if (!response.IsSuccessStatusCode)
        {
            ThrowIfUnauthorized(response, "movie recommendations");

            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogError(
                "Failed to get movie recommendations: Status={Status}, Content={Content}",
                response.StatusCode,
                errorContent);
            return Array.Empty<TraktMovie>();
        }

        var movies = await response.Content.ReadFromJsonAsync<TraktMovie[]>(_jsonOptions);
        return movies ?? Array.Empty<TraktMovie>();
    }

    /// <summary>
    /// Gets personalized show recommendations for a user.
    /// </summary>
    /// <param name="traktUser">The Trakt user configuration.</param>
    /// <param name="ignoreCollected">Whether to ignore collected shows.</param>
    /// <param name="ignoreWatchlisted">Whether to ignore watchlisted shows.</param>
    /// <param name="limit">Maximum number of recommendations to return (default: 10, max: 100).</param>
    /// <returns>List of recommended shows.</returns>
    public async Task<TraktShow[]> GetShowRecommendations(
        TraktUser traktUser,
        bool ignoreCollected = true,
        bool ignoreWatchlisted = false,
        int limit = 10)
    {
        var queryParams = $"?limit={limit}&extended=full";
        if (ignoreCollected)
        {
            queryParams += "&ignore_collected=true";
        }

        if (ignoreWatchlisted)
        {
            queryParams += "&ignore_watchlisted=true";
        }

        using var httpClient = await CreateTraktClient(traktUser);
        var response = await httpClient.GetAsync($"/recommendations/shows{queryParams}");

        if (!response.IsSuccessStatusCode)
        {
            ThrowIfUnauthorized(response, "show recommendations");

            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogError(
                "Failed to get show recommendations: Status={Status}, Content={Content}",
                response.StatusCode,
                errorContent);
            return Array.Empty<TraktShow>();
        }

        var shows = await response.Content.ReadFromJsonAsync<TraktShow[]>(_jsonOptions);
        return shows ?? Array.Empty<TraktShow>();
    }

    /// <summary>
    /// Gets the user's watched shows with season/episode progress, fetching all pages.
    /// </summary>
    /// <param name="traktUser">The Trakt user configuration.</param>
    /// <returns>List of watched shows with progress information.</returns>
    /// <remarks>
    /// <para>
    /// Trakt changed this endpoint on 2026-07-03 (trakt/trakt-api discussion 775). Season progress
    /// is no longer returned by default - <c>noseasons</c> became the default and is now a no-op -
    /// so <c>extended=progress</c> has to be requested explicitly. A request without pagination
    /// parameters also returns only page 1, capped at 100 items, so the results must be paged
    /// through rather than read in one call.
    /// </para>
    /// <para>
    /// <c>full,progress</c> rather than bare <c>progress</c>: on its own, <c>progress</c> returns a
    /// minimal show object with no <c>status</c> and no <c>genres</c>. That would silently break
    /// ended-show detection (every show would look ongoing, so only complete seasons would be
    /// cached) and anime detection, which routes downloads to a different Sonarr folder and
    /// profile. The combination is undocumented, so <see cref="ShowsCacheService"/> warns if the
    /// response ever stops carrying either piece.
    /// </para>
    /// </remarks>
    public async Task<TraktWatchedShow[]> GetWatchedShows(TraktUser traktUser)
    {
        const int PageSize = 100;
        const int MaxPages = 100;

        var allWatchedShows = new List<TraktWatchedShow>();
        var page = 1;

        while (page <= MaxPages)
        {
            using var httpClient = await CreateTraktClient(traktUser);
            var response = await httpClient.GetAsync(
                $"/sync/watched/shows?page={page}&limit={PageSize}&extended=full,progress");

            if (!response.IsSuccessStatusCode)
            {
                ThrowIfUnauthorized(response, "watched shows");

                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError(
                    "Failed to get watched shows (page {Page}): Status={Status}, Content={Content}",
                    page,
                    response.StatusCode,
                    errorContent);
                break;
            }

            var watchedShows = await response.Content.ReadFromJsonAsync<TraktWatchedShow[]>(_jsonOptions);
            if (watchedShows == null || watchedShows.Length == 0)
            {
                break;
            }

            allWatchedShows.AddRange(watchedShows);

            // Trakt caps the applied page size, so the header is more reliable than the limit we asked for.
            var pageCount = TryGetPageCount(response);
            if (pageCount.HasValue ? page >= pageCount.Value : watchedShows.Length < PageSize)
            {
                break;
            }

            page++;
        }

        if (page > MaxPages)
        {
            _logger.LogWarning(
                "Stopped fetching watched shows for user {UserId} after {MaxPages} pages",
                traktUser.LinkedMbUserId,
                MaxPages);
        }

        _logger.LogInformation(
            "Fetched {Count} watched shows across {PageCount} page(s) for user {UserId}",
            allWatchedShows.Count,
            Math.Min(page, MaxPages),
            traktUser.LinkedMbUserId);

        return allWatchedShows.ToArray();
    }

    /// <summary>
    /// Reads Trakt's X-Pagination-Page-Count response header.
    /// </summary>
    /// <param name="response">The Trakt response.</param>
    /// <returns>The total page count, or null when the header is absent or unparseable.</returns>
    private static int? TryGetPageCount(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("X-Pagination-Page-Count", out var values)
            && int.TryParse(values.FirstOrDefault(), out var pageCount))
        {
            return pageCount;
        }

        return null;
    }

    /// <summary>
    /// Gets all seasons for a show by Trakt ID.
    /// </summary>
    /// <param name="traktUser">The Trakt user configuration.</param>
    /// <param name="traktId">The Trakt show ID.</param>
    /// <returns>List of seasons for the show.</returns>
    public async Task<TraktSeason[]> GetShowSeasons(TraktUser traktUser, int traktId)
    {
        using var httpClient = await CreateTraktClient(traktUser);
        var response = await httpClient.GetAsync($"/shows/{traktId}/seasons?extended=full");

        if (!response.IsSuccessStatusCode)
        {
            ThrowIfUnauthorized(response, "show seasons");

            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogError(
                "Failed to get show seasons for Trakt ID {TraktId}: Status={Status}, Content={Content}",
                traktId,
                response.StatusCode,
                errorContent);
            return Array.Empty<TraktSeason>();
        }

        var responseContent = await response.Content.ReadAsStringAsync();

        TraktSeason[]? seasons = null;
        try
        {
            seasons = System.Text.Json.JsonSerializer.Deserialize<TraktSeason[]>(responseContent, _jsonOptions);
        }
        catch (System.Text.Json.JsonException ex)
        {
            _logger.LogError(ex, "Failed to deserialize Trakt seasons for show {TraktId}", traktId);
            return Array.Empty<TraktSeason>();
        }

        return seasons ?? Array.Empty<TraktSeason>();
    }

    /// <summary>
    /// Gets the poster URL of a show.
    /// </summary>
    /// <param name="traktUser">The Trakt user configuration.</param>
    /// <param name="traktId">The Trakt show ID.</param>
    /// <returns>An absolute poster URL, or null when Trakt returned no artwork.</returns>
    /// <remarks>
    /// Only used as a fallback for shows the Jellyfin library has no poster for, so a show that
    /// carries no artwork on Trakt is a normal outcome rather than an error.
    /// </remarks>
    public async Task<string?> GetShowPosterUrl(TraktUser traktUser, int traktId)
    {
        using var httpClient = await CreateTraktClient(traktUser);
        var response = await httpClient.GetAsync($"/shows/{traktId}?extended=images");

        if (!response.IsSuccessStatusCode)
        {
            ThrowIfUnauthorized(response, "show images");

            _logger.LogDebug(
                "Failed to get images for Trakt show {TraktId}: Status={Status}",
                traktId,
                response.StatusCode);
            return null;
        }

        TraktShowSummary? show;
        try
        {
            show = await response.Content.ReadFromJsonAsync<TraktShowSummary>(_jsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Failed to deserialize images for Trakt show {TraktId}", traktId);
            return null;
        }

        var url = show?.Images?.Poster.FirstOrDefault() ?? show?.Images?.Thumb.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        // Trakt returns protocol relative URLs.
        return url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : $"https://{url}";
    }

    /// <summary>
    /// Gets trending movies.
    /// </summary>
    /// <param name="traktUser">The Trakt user configuration (for auth).</param>
    /// <param name="limit">Maximum number of trending movies to return (default: 10, max: 100).</param>
    /// <returns>List of trending movies.</returns>
    public async Task<TraktMovie[]> GetTrendingMovies(TraktUser traktUser, int limit = 10)
    {
        var queryParams = $"?limit={limit}&extended=full";

        using var httpClient = await CreateTraktClient(traktUser);
        var response = await httpClient.GetAsync($"/movies/trending{queryParams}");

        if (!response.IsSuccessStatusCode)
        {
            ThrowIfUnauthorized(response, "trending movies");

            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogError(
                "Failed to get trending movies: Status={Status}, Content={Content}",
                response.StatusCode,
                errorContent);
            return Array.Empty<TraktMovie>();
        }

        // Trending endpoint returns an array of objects with a "movie" property
        var trendingItems = await response.Content.ReadFromJsonAsync<TraktTrendingMovieItem[]>(_jsonOptions);
        if (trendingItems == null)
        {
            return Array.Empty<TraktMovie>();
        }

        // Extract the movie objects from the trending items
        return trendingItems.Select(item => item.Movie).ToArray();
    }

    /// <summary>
    /// Gets the user's movie watchlist.
    /// </summary>
    /// <param name="traktUser">The Trakt user configuration.</param>
    /// <returns>List of watchlisted movies.</returns>
    public async Task<TraktMovie[]> GetMovieWatchlist(TraktUser traktUser)
    {
        using var httpClient = await CreateTraktClient(traktUser);
        var response = await httpClient.GetAsync("/sync/watchlist/movies?extended=full");

        if (!response.IsSuccessStatusCode)
        {
            ThrowIfUnauthorized(response, "the movie watchlist");

            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogError(
                "Failed to get movie watchlist: Status={Status}, Content={Content}",
                response.StatusCode,
                errorContent);
            return Array.Empty<TraktMovie>();
        }

        var watchlistItems = await response.Content.ReadFromJsonAsync<TraktWatchlistMovieItem[]>(_jsonOptions);
        if (watchlistItems == null)
        {
            return Array.Empty<TraktMovie>();
        }

        return watchlistItems.Select(item => item.Movie).ToArray();
    }

    /// <summary>
    /// Gets the user's show watchlist.
    /// </summary>
    /// <param name="traktUser">The Trakt user configuration.</param>
    /// <returns>List of watchlisted shows.</returns>
    public async Task<TraktShow[]> GetShowWatchlist(TraktUser traktUser)
    {
        using var httpClient = await CreateTraktClient(traktUser);
        var response = await httpClient.GetAsync("/sync/watchlist/shows?extended=full");

        if (!response.IsSuccessStatusCode)
        {
            ThrowIfUnauthorized(response, "the show watchlist");

            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogError(
                "Failed to get show watchlist: Status={Status}, Content={Content}",
                response.StatusCode,
                errorContent);
            return Array.Empty<TraktShow>();
        }

        var watchlistItems = await response.Content.ReadFromJsonAsync<TraktWatchlistShowItem[]>(_jsonOptions);
        if (watchlistItems == null)
        {
            return Array.Empty<TraktShow>();
        }

        return watchlistItems.Select(item => item.Show).ToArray();
    }

    /// <summary>
    /// Gets watch history for shows with automatic pagination and date filtering.
    /// Fetches all pages automatically until no more results are available.
    /// </summary>
    /// <param name="traktUser">The Trakt user configuration.</param>
    /// <param name="startAt">Start of history window (ISO 8601, optional).</param>
    /// <param name="endAt">End of history window (ISO 8601, optional).</param>
    /// <param name="limit">Number of items per page (default: 100, max: 100).</param>
    /// <returns>List of all watch history items across all pages.</returns>
    public async Task<TraktHistoryItem[]> GetShowWatchHistory(
        TraktUser traktUser,
        DateTime? startAt = null,
        DateTime? endAt = null,
        int limit = 100)
    {
        var allHistoryItems = new List<TraktHistoryItem>();
        var page = 1;
        var hasMorePages = true;

        while (hasMorePages)
        {
            var queryParams = $"?page={page}&limit={limit}&extended=full";

            if (startAt.HasValue)
            {
                // Format as ISO 8601 with Z suffix
                queryParams += $"&start_at={startAt.Value.ToUniversalTime():yyyy-MM-ddTHH:mm:ss.fffZ}";
            }

            if (endAt.HasValue)
            {
                // Format as ISO 8601 with Z suffix
                queryParams += $"&end_at={endAt.Value.ToUniversalTime():yyyy-MM-ddTHH:mm:ss.fffZ}";
            }

            using var httpClient = await CreateTraktClient(traktUser);
            var response = await httpClient.GetAsync($"/sync/history/shows{queryParams}");

            if (!response.IsSuccessStatusCode)
            {
                ThrowIfUnauthorized(response, "show watch history");

                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError(
                    "Failed to get show watch history (page {Page}): Status={Status}, Content={Content}",
                    page,
                    response.StatusCode,
                    errorContent);
                break;
            }

            var historyItems = await response.Content.ReadFromJsonAsync<TraktHistoryItem[]>(_jsonOptions);
            if (historyItems == null || historyItems.Length == 0)
            {
                // No more results
                hasMorePages = false;
            }
            else
            {
                allHistoryItems.AddRange(historyItems);
                _logger.LogDebug(
                    "Fetched {Count} history items from page {Page}",
                    historyItems.Length,
                    page);

                // If we got fewer items than the limit, we've reached the last page
                if (historyItems.Length < limit)
                {
                    hasMorePages = false;
                }
                else
                {
                    page++;
                }
            }
        }

        _logger.LogInformation(
            "Fetched {TotalCount} total history items across {PageCount} page(s)",
            allHistoryItems.Count,
            page);

        return allHistoryItems.ToArray();
    }
}
