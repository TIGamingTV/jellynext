using System;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyNext.Services;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyNext.Api;

/// <summary>
/// Tells the web client which of the items it is drawing are JellyNext's, and how to mark them.
/// </summary>
/// <remarks>
/// Per-user like <see cref="WidgetController"/> and for the same reason: the caller comes from the
/// authorization context, never from a route parameter, so one user cannot enumerate another's
/// virtual library.
/// </remarks>
[ApiController]
[Route("JellyNext/Marker")]
[Produces("application/json")]
public class MarkerController : ControllerBase
{
    private readonly VirtualItemTagService _tagService;
    private readonly IAuthorizationContext _authorizationContext;

    /// <summary>
    /// Initializes a new instance of the <see cref="MarkerController"/> class.
    /// </summary>
    /// <param name="tagService">The virtual item tag service.</param>
    /// <param name="authorizationContext">The authorization context.</param>
    public MarkerController(
        VirtualItemTagService tagService,
        IAuthorizationContext authorizationContext)
    {
        _tagService = tagService;
        _authorizationContext = authorizationContext;
    }

    /// <summary>
    /// Gets the marking settings and the tagged items the calling user can see.
    /// </summary>
    /// <returns>How to mark JellyNext's items, and which items those are.</returns>
    [HttpGet]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<object>> GetMarkedItems()
    {
        var configuration = Plugin.Instance?.Configuration;
        var tag = configuration?.MediaTagName?.Trim();
        var enabled = configuration?.MediaTaggingEnabled == true && !string.IsNullOrEmpty(tag);

        var badge = enabled && configuration!.MediaBadgeEnabled;
        var replaceIcon = enabled && configuration!.MediaRequestIconEnabled;

        var badgeText = configuration?.MediaBadgeText?.Trim();
        if (string.IsNullOrEmpty(badgeText))
        {
            badgeText = tag;
        }

        var requestText = configuration?.MediaRequestButtonText?.Trim();
        if (string.IsNullOrEmpty(requestText))
        {
            requestText = "Request";
        }

        // Nothing is drawn from the ids unless one of the two decorations is on, so the query is not
        // worth making otherwise.
        var itemIds = badge || replaceIcon
            ? _tagService.GetTaggedItemIds(await GetUserId().ConfigureAwait(false))
            : Array.Empty<string>();

        return Ok(new
        {
            enabled,
            tag,
            badge,
            badgeText,
            replaceIcon,
            requestText,
            itemIds
        });
    }

    private async Task<Guid> GetUserId()
    {
        var authorizationInfo = await _authorizationContext.GetAuthorizationInfo(HttpContext).ConfigureAwait(false);
        return authorizationInfo?.UserId ?? Guid.Empty;
    }
}
