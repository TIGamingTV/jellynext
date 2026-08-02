using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyNext.Models.Common;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyNext.Services;

/// <summary>
/// Answers Modular Home's request for the contents of JellyNext's New Seasons section.
/// </summary>
/// <remarks>
/// <para>
/// Modular Home constructs this class itself, by name, with
/// <c>ActivatorUtilities.CreateInstance</c> against Jellyfin's root service provider - the same
/// container <see cref="PluginServiceRegistrator"/> registers JellyNext's services in - so ordinary
/// constructor injection works even though nothing in JellyNext ever news it up. The class and the
/// method must stay public and keep their names: <see cref="ModularHomeBridge"/> sends both as
/// strings, and a rename would only fail at runtime.
/// </para>
/// <para>
/// The return type is deliberately a <c>MediaBrowser.Model</c> type. Those assemblies resolve from
/// the default load context in every plugin, so the value crosses the plugin boundary as one shared
/// type - which is the whole reason this integration needs no reference to Modular Home's assembly.
/// </para>
/// </remarks>
public class ModularHomeSectionHandler
{
    private readonly ILogger<ModularHomeSectionHandler> _logger;
    private readonly NextSeasonsWidgetService _widgetService;
    private readonly IDtoService _dtoService;
    private readonly IUserManager _userManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="ModularHomeSectionHandler"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="widgetService">The widget service.</param>
    /// <param name="dtoService">The DTO service.</param>
    /// <param name="userManager">The user manager.</param>
    public ModularHomeSectionHandler(
        ILogger<ModularHomeSectionHandler> logger,
        NextSeasonsWidgetService widgetService,
        IDtoService dtoService,
        IUserManager userManager)
    {
        _logger = logger;
        _widgetService = widgetService;
        _dtoService = dtoService;
        _userManager = userManager;
    }

    /// <summary>
    /// Gets the New Seasons cards for a user.
    /// </summary>
    /// <param name="payload">The user the home screen is being built for.</param>
    /// <returns>The section's items.</returns>
    /// <remarks>
    /// Answers with the user's own virtual library items rather than synthesized DTOs. A third-party
    /// section is rendered by Jellyfin's stock card builder, which derives artwork and navigation from
    /// the item id, so an item without one renders as an empty card. Using the real item also means
    /// the card's play overlay plays the stub, which the playback interceptor already turns into a
    /// download - the request path works without any client script at all.
    /// </remarks>
    public QueryResult<BaseItemDto> GetResults(ModularHomeSectionPayload payload)
    {
        try
        {
            if (Plugin.Instance?.Configuration.ModularHomeIntegrationEnabled != true
                || payload == null
                || payload.UserId.Equals(Guid.Empty))
            {
                return new QueryResult<BaseItemDto>();
            }

            var user = _userManager.GetUserById(payload.UserId);
            if (user == null)
            {
                return new QueryResult<BaseItemDto>();
            }

            var dtoOptions = new DtoOptions(true);
            var results = new List<BaseItemDto>();
            var contentItems = _widgetService.GetContentItems(payload.UserId);

            foreach (var contentItem in contentItems)
            {
                var libraryItem = _widgetService.FindVirtualLibraryItem(payload.UserId, contentItem);
                if (libraryItem == null)
                {
                    // The stub has not been scanned in - or the virtual library was never set up.
                    // Dropping the row is better than a card that renders as an empty tile.
                    continue;
                }

                results.Add(_dtoService.GetBaseItemDto(libraryItem, dtoOptions, user));
            }

            _logger.LogDebug(
                "Modular Home section returning {Count} of {Total} new seasons for user {UserId}",
                results.Count,
                contentItems.Count,
                payload.UserId);

            return new QueryResult<BaseItemDto>(0, results.Count, results);
        }
        catch (Exception ex)
        {
            // Modular Home invokes this by reflection and builds the rest of the home screen from the
            // result, so an exception here must not escape into its section loop.
            _logger.LogError(ex, "Failed to build the Modular Home section contents");
            return new QueryResult<BaseItemDto>();
        }
    }
}
