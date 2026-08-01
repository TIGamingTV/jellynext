using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyNext.Services;

/// <summary>
/// Service for querying the local Jellyfin library.
/// </summary>
public class LocalLibraryService
{
    private readonly ILogger<LocalLibraryService> _logger;
    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="LocalLibraryService"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="libraryManager">The library manager.</param>
    public LocalLibraryService(ILogger<LocalLibraryService> logger, ILibraryManager libraryManager)
    {
        _logger = logger;
        _libraryManager = libraryManager;
    }

    /// <summary>
    /// Finds a TV series in the local library by any of the IDs known for it.
    /// </summary>
    /// <param name="tvdbId">The TVDB ID, if known.</param>
    /// <param name="tmdbId">The TMDB ID, if known.</param>
    /// <param name="imdbId">The IMDB ID, if known.</param>
    /// <returns>The series if found, null otherwise.</returns>
    /// <remarks>
    /// Matching on TVDB alone misses shows Jellyfin identified through a different provider - anime
    /// matched by TMDB is the common case - which then look absent from the library even though the
    /// user is watching them.
    /// </remarks>
    public Series? FindSeriesByAnyProviderId(int? tvdbId, int? tmdbId, string? imdbId)
    {
        var providerIds = new Dictionary<string, string>();

        if (tvdbId.HasValue && tvdbId.Value != 0)
        {
            providerIds[MetadataProvider.Tvdb.ToString()] = tvdbId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (tmdbId.HasValue && tmdbId.Value != 0)
        {
            providerIds[MetadataProvider.Tmdb.ToString()] = tmdbId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (!string.IsNullOrEmpty(imdbId))
        {
            providerIds[MetadataProvider.Imdb.ToString()] = imdbId;
        }

        if (providerIds.Count == 0)
        {
            return null;
        }

        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Series },
            HasAnyProviderId = providerIds,
            Recursive = true
        });

        return items
            .OfType<Series>()
            .FirstOrDefault(s => !s.Path?.Contains("jellynext-virtual", StringComparison.OrdinalIgnoreCase) ?? true);
    }

    /// <summary>
    /// Finds a movie in the local library by TMDB ID.
    /// </summary>
    /// <param name="tmdbId">The TMDB ID.</param>
    /// <returns>True if the movie exists in the library, false otherwise.</returns>
    public bool DoesMovieExist(int tmdbId)
    {
        var tmdbIdString = tmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var allItems = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie },
            HasAnyProviderId = new Dictionary<string, string>
            {
                { MetadataProvider.Tmdb.ToString(), tmdbIdString }
            },
            Recursive = true
        });

        return allItems
            .Where(m => !m.Path?.Contains("jellynext-virtual", StringComparison.OrdinalIgnoreCase) ?? true)
            .Any();
    }

    /// <summary>
    /// Gets the season numbers that exist locally for a series.
    /// </summary>
    /// <param name="series">The series.</param>
    /// <returns>Set of season numbers that exist locally.</returns>
    /// <remarks>
    /// <c>IsVirtualItem = false</c> is what keeps this answering "is it on disk". With "display missing
    /// episodes" enabled, Jellyfin materialises a Season entity for every season the metadata provider
    /// knows about, including ones that were never downloaded. Counting those made a season the user
    /// does not have look present, so Next Seasons silently stopped suggesting it and the stub for it
    /// was deleted as "already in the library".
    /// </remarks>
    public HashSet<int> GetLocalSeasons(Series series)
    {
        var seasons = new HashSet<int>();

        var seasonItems = _libraryManager.GetItemList(new InternalItemsQuery
        {
            ParentId = series.Id,
            IncludeItemTypes = new[] { BaseItemKind.Season },
            IsVirtualItem = false,
            Recursive = false
        });

        foreach (var item in seasonItems.OfType<Season>())
        {
            // Skip virtual items created by this plugin
            if (item.Path?.Contains("jellynext-virtual", StringComparison.OrdinalIgnoreCase) == true)
            {
                continue;
            }

            if (item.IndexNumber.HasValue)
            {
                seasons.Add(item.IndexNumber.Value);
            }
        }

        return seasons;
    }

    /// <summary>
    /// Checks if a specific season exists locally for a series.
    /// </summary>
    /// <param name="tvdbId">The TVDB ID of the series, if known.</param>
    /// <param name="tmdbId">The TMDB ID of the series, if known.</param>
    /// <param name="imdbId">The IMDB ID of the series, if known.</param>
    /// <param name="seasonNumber">The season number to check.</param>
    /// <returns>True if the season exists locally, false otherwise.</returns>
    /// <remarks>
    /// Every ID is offered because a series matched by TMDB carries no TVDB ID: looking only for the
    /// latter made the show look absent, so a season the user already had was suggested again.
    /// </remarks>
    public bool DoesSeasonExist(int? tvdbId, int? tmdbId, string? imdbId, int seasonNumber)
    {
        var series = FindSeriesByAnyProviderId(tvdbId, tmdbId, imdbId);
        if (series == null)
        {
            return false;
        }

        var localSeasons = GetLocalSeasons(series);
        return localSeasons.Contains(seasonNumber);
    }
}
