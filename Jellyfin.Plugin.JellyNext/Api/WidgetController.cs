using System;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyNext.Configuration;
using Jellyfin.Plugin.JellyNext.Models.Common;
using Jellyfin.Plugin.JellyNext.Services;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyNext.Api;

/// <summary>
/// API controller backing the New Seasons home screen widget.
/// </summary>
/// <remarks>
/// Unlike the rest of the plugin's API these endpoints are used by ordinary users rather than the
/// dashboard, so they authorize any signed in account and answer for the caller only.
/// </remarks>
[ApiController]
[Route("JellyNext/Widget")]
[Produces("application/json")]
public class WidgetController : ControllerBase
{
    private readonly ILogger<WidgetController> _logger;
    private readonly NextSeasonsWidgetService _widgetService;
    private readonly IAuthorizationContext _authorizationContext;

    /// <summary>
    /// Initializes a new instance of the <see cref="WidgetController"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="widgetService">The widget service.</param>
    /// <param name="authorizationContext">The authorization context.</param>
    public WidgetController(
        ILogger<WidgetController> logger,
        NextSeasonsWidgetService widgetService,
        IAuthorizationContext authorizationContext)
    {
        _logger = logger;
        _widgetService = widgetService;
        _authorizationContext = authorizationContext;
    }

    /// <summary>
    /// Gets the new seasons available to the calling user.
    /// </summary>
    /// <returns>The widget's contents and display settings.</returns>
    [HttpGet("NextSeasons")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<object>> GetNextSeasons()
    {
        var config = Plugin.Instance?.Configuration;
        var enabled = config?.NextSeasonsWidgetEnabled == true;
        var userId = enabled ? await GetUserId() : Guid.Empty;

        return Ok(new
        {
            enabled,
            title = config?.NextSeasonsWidgetTitle ?? "New Seasons",
            position = (config?.NextSeasonsWidgetPosition ?? WidgetPosition.Top).ToString(),
            items = userId == Guid.Empty
                ? Array.Empty<NextSeasonWidgetItem>()
                : _widgetService.GetItems(userId)
        });
    }

    /// <summary>
    /// Requests a season through the configured download integration.
    /// </summary>
    /// <param name="request">The season to request.</param>
    /// <returns>The outcome of the request.</returns>
    [HttpPost("Request")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<object>> RequestSeason([FromBody] WidgetRequest request)
    {
        if (Plugin.Instance?.Configuration.NextSeasonsWidgetEnabled != true)
        {
            return BadRequest(new { success = false, message = "The New Seasons widget is disabled." });
        }

        if (request == null || request.TraktId <= 0 || request.SeasonNumber < 0)
        {
            return BadRequest(new { success = false, message = "A show and season are required." });
        }

        var userId = await GetUserId();
        if (userId == Guid.Empty)
        {
            return BadRequest(new { success = false, message = "Could not identify the requesting user." });
        }

        try
        {
            var result = await _widgetService.RequestAsync(userId, request.TraktId, request.SeasonNumber);
            if (result == null)
            {
                return NotFound(new
                {
                    success = false,
                    message = "That season is no longer available - refresh the page and try again."
                });
            }

            return Ok(new { success = result.Success, message = result.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Widget request failed for Trakt show {TraktId} season {Season}",
                request.TraktId,
                request.SeasonNumber);
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new { success = false, message = "The request could not be sent. Check the Jellyfin log." });
        }
    }

    /// <summary>
    /// Redirects to a show's poster on Trakt.
    /// </summary>
    /// <param name="traktId">The Trakt show ID.</param>
    /// <returns>A redirect to the poster.</returns>
    /// <remarks>
    /// Deliberately anonymous: browsers do not attach Jellyfin's token to <c>img</c> requests, and the
    /// response is a redirect to public artwork.
    /// </remarks>
    [HttpGet("Poster/{traktId}")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> GetPoster([FromRoute, Required] int traktId)
    {
        if (Plugin.Instance?.Configuration.NextSeasonsWidgetEnabled != true)
        {
            return NotFound();
        }

        var url = await _widgetService.GetTraktPosterUrlAsync(traktId);
        if (string.IsNullOrEmpty(url))
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "public, max-age=86400";
        return Redirect(url);
    }

    private async Task<Guid> GetUserId()
    {
        var authorizationInfo = await _authorizationContext.GetAuthorizationInfo(HttpContext);
        return authorizationInfo?.UserId ?? Guid.Empty;
    }

    /// <summary>
    /// Body of a widget season request.
    /// </summary>
    public class WidgetRequest
    {
        /// <summary>
        /// Gets or sets the Trakt show ID.
        /// </summary>
        [JsonPropertyName("traktId")]
        public int TraktId { get; set; }

        /// <summary>
        /// Gets or sets the season number.
        /// </summary>
        [JsonPropertyName("seasonNumber")]
        public int SeasonNumber { get; set; }
    }
}
