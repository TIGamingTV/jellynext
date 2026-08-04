using System;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyNext.Helpers;
using Jellyfin.Plugin.JellyNext.ScheduledTasks;
using Jellyfin.Plugin.JellyNext.Services;
using Jellyfin.Plugin.JellyNext.VirtualLibrary;
using MediaBrowser.Common.Api;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyNext.Api;

/// <summary>
/// API controller for Trakt OAuth operations.
/// </summary>
[ApiController]
[Route("JellyNext/Trakt")]
[Produces("application/json")]
public class TraktController : ControllerBase
{
    private readonly ILogger<TraktController> _logger;
    private readonly TraktApi _traktApi;
    private readonly VirtualLibraryManager _virtualLibraryManager;
    private readonly TraktPluginBridge _traktPluginBridge;
    private readonly ITaskManager _taskManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="TraktController"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="traktApi">The Trakt API service.</param>
    /// <param name="virtualLibraryManager">The virtual library manager.</param>
    /// <param name="traktPluginBridge">Bridge to the official Trakt plugin's stored tokens.</param>
    /// <param name="taskManager">The scheduled task manager.</param>
    public TraktController(
        ILogger<TraktController> logger,
        TraktApi traktApi,
        VirtualLibraryManager virtualLibraryManager,
        TraktPluginBridge traktPluginBridge,
        ITaskManager taskManager)
    {
        _logger = logger;
        _traktApi = traktApi;
        _virtualLibraryManager = virtualLibraryManager;
        _traktPluginBridge = traktPluginBridge;
        _taskManager = taskManager;
    }

    /// <summary>
    /// Reports whether the official Trakt plugin is present and which users it has linked.
    /// </summary>
    /// <returns>The shared authorization status.</returns>
    [HttpGet("SharedStatus")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> GetSharedStatus()
    {
        return Ok(new
        {
            authMode = (int)TraktApi.AuthMode,
            traktPluginAvailable = _traktPluginBridge.IsAvailable,
            traktPluginVersion = _traktPluginBridge.PluginVersion,
            linkedUserIds = _traktPluginBridge.GetLinkedUserIds().Select(id => id.ToString()).ToArray()
        });
    }

    /// <summary>
    /// Registers a Jellyfin user with JellyNext without running a device authorization flow.
    /// </summary>
    /// <param name="userGuid">The Jellyfin user GUID.</param>
    /// <returns>Success status.</returns>
    /// <remarks>
    /// Used in shared-token mode: the token already lives in the official Trakt plugin, so JellyNext
    /// only needs a configuration entry to hold this user's sync preferences.
    /// </remarks>
    [HttpPost("Users/{userGuid}/Link")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<object> LinkSharedUser([FromRoute][Required] Guid userGuid)
    {
        if (!TraktApi.UsesSharedToken)
        {
            return BadRequest(new
            {
                error = "Shared linking is only available when the Trakt authorization mode is set to "
                        + "'Share the Trakt plugin's token'."
            });
        }

        if (!_traktPluginBridge.IsAvailable)
        {
            return BadRequest(new
            {
                error = "The official Trakt plugin is not installed or not enabled."
            });
        }

        if (_traktPluginBridge.GetToken(userGuid) == null)
        {
            return BadRequest(new
            {
                error = "The Trakt plugin has no linked Trakt account for this Jellyfin user. "
                        + "Link it in the Trakt plugin's settings first."
            });
        }

        if (UserHelper.GetTraktUser(userGuid) == null)
        {
            Plugin.Instance?.Configuration.AddUser(userGuid);
            Plugin.Instance?.SaveConfiguration();
            _virtualLibraryManager.InitializeUserDirectories(userGuid);
        }

        _logger.LogInformation("Linked user {UserGuid} to JellyNext using the Trakt plugin's token", userGuid);

        return Ok(new { success = true });
    }

    /// <summary>
    /// Initiates OAuth device authorization for a user.
    /// </summary>
    /// <param name="userGuid">The Jellyfin user GUID.</param>
    /// <returns>The user code and verification URL.</returns>
    [HttpPost("Users/{userGuid}/Authorize")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<object>> AuthorizeUser([FromRoute][Required] Guid userGuid)
    {
        if (TraktApi.UsesSharedToken)
        {
            // Checked before creating a configuration entry, so a rejected attempt leaves no orphan.
            return BadRequest(new
            {
                error = "Device authorization is disabled while JellyNext shares the Trakt plugin's token. "
                        + "Link the account in the Trakt plugin, then use 'Use the Trakt Plugin's Account'."
            });
        }

        try
        {
            var traktUser = UserHelper.GetTraktUser(userGuid);
            var isNewUser = traktUser == null;

            if (isNewUser)
            {
                Plugin.Instance?.Configuration.AddUser(userGuid);
                Plugin.Instance?.SaveConfiguration();
                traktUser = UserHelper.GetTraktUser(userGuid);

                // Initialize virtual library directories immediately for new user
                _virtualLibraryManager.InitializeUserDirectories(userGuid);
            }

            if (traktUser == null)
            {
                return BadRequest(new { error = "Failed to create Trakt user configuration" });
            }

            var userCode = await _traktApi.AuthorizeDevice(traktUser);

            return Ok(new
            {
                userCode,
                verificationUrl = "https://trakt.tv/activate"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error initiating Trakt authorization for user {UserGuid}", userGuid);
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Checks the authorization status for a user.
    /// </summary>
    /// <param name="userGuid">The Jellyfin user GUID.</param>
    /// <returns>The authorization status.</returns>
    [HttpGet("Users/{userGuid}/AuthorizationStatus")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> GetAuthorizationStatus([FromRoute][Required] Guid userGuid)
    {
        var traktUser = UserHelper.GetTraktUser(userGuid);

        if (TraktApi.UsesSharedToken)
        {
            // The token lives in the official Trakt plugin; JellyNext's own entry only holds settings.
            return Ok(new
            {
                isAuthorized = traktUser != null && _traktPluginBridge.GetToken(userGuid) != null,
                sharedMode = true,
                traktPluginAvailable = _traktPluginBridge.IsAvailable,
                traktPluginHasToken = _traktPluginBridge.GetToken(userGuid) != null,
                registeredWithJellyNext = traktUser != null
            });
        }

        var isAuthorized = traktUser != null &&
                          !string.IsNullOrEmpty(traktUser.AccessToken) &&
                          !string.IsNullOrEmpty(traktUser.RefreshToken);

        return Ok(new { isAuthorized, sharedMode = false });
    }

    /// <summary>
    /// Deauthorizes a user's Trakt account.
    /// </summary>
    /// <param name="userGuid">The Jellyfin user GUID.</param>
    /// <returns>Success status.</returns>
    [HttpPost("Users/{userGuid}/Deauthorize")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult DeauthorizeUser([FromRoute][Required] Guid userGuid)
    {
        var traktUser = UserHelper.GetTraktUser(userGuid);
        if (traktUser == null)
        {
            return NotFound(new { error = "Trakt user configuration not found" });
        }

        Plugin.Instance?.Configuration.RemoveUser(userGuid);
        Plugin.Instance?.SaveConfiguration();

        _logger.LogInformation("Deauthorized Trakt for user {UserGuid}", userGuid);

        return Ok(new { success = true });
    }

    /// <summary>
    /// Gets the user-specific Trakt settings.
    /// </summary>
    /// <param name="userGuid">The Jellyfin user GUID.</param>
    /// <returns>The user settings.</returns>
    [HttpGet("Users/{userGuid}/Settings")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<object> GetUserSettings([FromRoute][Required] Guid userGuid)
    {
        var traktUser = UserHelper.GetTraktUser(userGuid);
        if (traktUser == null)
        {
            return NotFound(new { error = "Trakt user configuration not found" });
        }

        return Ok(new
        {
            syncMovieRecommendations = traktUser.SyncMovieRecommendations,
            syncShowRecommendations = traktUser.SyncShowRecommendations,
            syncNextSeasons = traktUser.SyncNextSeasons,
            nextSeasonsRecentOnly = traktUser.NextSeasonsRecentOnly,
            nextSeasonsRecentDays = traktUser.NextSeasonsRecentDays,
            syncWatchlistMovies = traktUser.SyncWatchlistMovies,
            syncWatchlistShows = traktUser.SyncWatchlistShows,
            ignoreCollected = traktUser.IgnoreCollected,
            ignoreWatchlisted = traktUser.IgnoreWatchlisted,
            limitShowsToSeasonOne = traktUser.LimitShowsToSeasonOne,
            movieRecommendationsLimit = traktUser.MovieRecommendationsLimit,
            showRecommendationsLimit = traktUser.ShowRecommendationsLimit,
            notifyNewSeasonsByEmail = traktUser.NotifyNewSeasonsByEmail,
            notificationEmail = traktUser.NotificationEmail
        });
    }

    /// <summary>
    /// Updates the user-specific Trakt settings.
    /// </summary>
    /// <param name="userGuid">The Jellyfin user GUID.</param>
    /// <param name="settings">The updated settings.</param>
    /// <returns>Success status.</returns>
    [HttpPost("Users/{userGuid}/Settings")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult UpdateUserSettings([FromRoute][Required] Guid userGuid, [FromBody] UserSettingsDto settings)
    {
        var traktUser = UserHelper.GetTraktUser(userGuid);
        if (traktUser == null)
        {
            return NotFound(new { error = "Trakt user configuration not found" });
        }

        var recentDays = Math.Clamp(settings.NextSeasonsRecentDays, 1, 3650);

        // Every library here is built from cached content, so a narrowed filter only takes effect on
        // the next sync - up to six hours of the items the user just filtered out still sitting there,
        // which reads as the setting having done nothing.
        var contentFilterChanged = traktUser.SyncNextSeasons != settings.SyncNextSeasons
            || traktUser.NextSeasonsRecentOnly != settings.NextSeasonsRecentOnly
            || traktUser.NextSeasonsRecentDays != recentDays
            || traktUser.IgnoreCollected != settings.IgnoreCollected
            || traktUser.IgnoreWatchlisted != settings.IgnoreWatchlisted;

        traktUser.SyncMovieRecommendations = settings.SyncMovieRecommendations;
        traktUser.SyncShowRecommendations = settings.SyncShowRecommendations;
        traktUser.SyncNextSeasons = settings.SyncNextSeasons;
        traktUser.NextSeasonsRecentOnly = settings.NextSeasonsRecentOnly;
        traktUser.NextSeasonsRecentDays = recentDays;
        traktUser.SyncWatchlistMovies = settings.SyncWatchlistMovies;
        traktUser.SyncWatchlistShows = settings.SyncWatchlistShows;
        traktUser.IgnoreCollected = settings.IgnoreCollected;
        traktUser.IgnoreWatchlisted = settings.IgnoreWatchlisted;
        traktUser.LimitShowsToSeasonOne = settings.LimitShowsToSeasonOne;
        traktUser.MovieRecommendationsLimit = Math.Clamp(settings.MovieRecommendationsLimit, 1, 100);
        traktUser.ShowRecommendationsLimit = Math.Clamp(settings.ShowRecommendationsLimit, 1, 100);
        traktUser.NotifyNewSeasonsByEmail = settings.NotifyNewSeasonsByEmail;
        traktUser.NotificationEmail = (settings.NotificationEmail ?? string.Empty).Trim();

        Plugin.Instance?.SaveConfiguration();

        _logger.LogInformation("Updated Trakt settings for user {UserGuid}", userGuid);

        if (contentFilterChanged)
        {
            _logger.LogInformation(
                "Content filters changed for user {UserGuid}, queueing a content sync",
                userGuid);
            _taskManager.QueueScheduledTask<ContentSyncScheduledTask>();
        }

        return Ok(new { success = true });
    }

    /// <summary>
    /// DTO for user settings update.
    /// </summary>
    public class UserSettingsDto
    {
        /// <summary>
        /// Gets or sets a value indicating whether to sync movie recommendations.
        /// </summary>
        public bool SyncMovieRecommendations { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether to sync show recommendations.
        /// </summary>
        public bool SyncShowRecommendations { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether to sync next seasons.
        /// </summary>
        public bool SyncNextSeasons { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether next seasons are limited to recently released seasons.
        /// </summary>
        public bool NextSeasonsRecentOnly { get; set; }

        /// <summary>
        /// Gets or sets the new-release window for next seasons, in days (1-3650).
        /// </summary>
        public int NextSeasonsRecentDays { get; set; } = 90;

        /// <summary>
        /// Gets or sets a value indicating whether to ignore collected items.
        /// </summary>
        public bool IgnoreCollected { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether to ignore watchlisted items.
        /// </summary>
        public bool IgnoreWatchlisted { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether to limit shows to season 1 only.
        /// </summary>
        public bool LimitShowsToSeasonOne { get; set; }

        /// <summary>
        /// Gets or sets the number of movie recommendations to fetch (1-100).
        /// </summary>
        public int MovieRecommendationsLimit { get; set; }

        /// <summary>
        /// Gets or sets the number of show recommendations to fetch (1-100).
        /// </summary>
        public int ShowRecommendationsLimit { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether to automatically add watchlisted movies to download system.
        /// </summary>
        public bool SyncWatchlistMovies { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether to automatically add watchlisted shows to download system.
        /// </summary>
        public bool SyncWatchlistShows { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether to email this user when a new season is released.
        /// </summary>
        public bool NotifyNewSeasonsByEmail { get; set; }

        /// <summary>
        /// Gets or sets the address new season notifications are sent to.
        /// </summary>
        public string? NotificationEmail { get; set; }
    }
}
