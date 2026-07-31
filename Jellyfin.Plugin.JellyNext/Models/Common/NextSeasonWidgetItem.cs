using System;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyNext.Models.Common;

/// <summary>
/// A single show shown by the New Seasons home screen widget.
/// </summary>
/// <remarks>
/// The property names are pinned to camel case because Jellyfin serializes member names as written,
/// and the widget script reads them directly.
/// </remarks>
public class NextSeasonWidgetItem
{
    /// <summary>
    /// Gets or sets the Trakt show ID, which the widget sends back when requesting the season.
    /// </summary>
    [JsonPropertyName("traktId")]
    public int TraktId { get; set; }

    /// <summary>
    /// Gets or sets the TVDB ID.
    /// </summary>
    [JsonPropertyName("tvdbId")]
    public int? TvdbId { get; set; }

    /// <summary>
    /// Gets or sets the show title.
    /// </summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the year the show premiered.
    /// </summary>
    [JsonPropertyName("year")]
    public int? Year { get; set; }

    /// <summary>
    /// Gets or sets the season being offered.
    /// </summary>
    [JsonPropertyName("seasonNumber")]
    public int SeasonNumber { get; set; }

    /// <summary>
    /// Gets or sets the total number of episodes the season will have.
    /// </summary>
    [JsonPropertyName("episodeCount")]
    public int? EpisodeCount { get; set; }

    /// <summary>
    /// Gets or sets the number of episodes of the season that have aired.
    /// </summary>
    [JsonPropertyName("airedEpisodes")]
    public int? AiredEpisodes { get; set; }

    /// <summary>
    /// Gets or sets the season's premiere date.
    /// </summary>
    [JsonPropertyName("firstAired")]
    public DateTime? FirstAired { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the season is still part-way through airing.
    /// </summary>
    [JsonPropertyName("isAiring")]
    public bool IsAiring { get; set; }

    /// <summary>
    /// Gets or sets the API path the widget loads the poster from, relative to the server root.
    /// </summary>
    /// <remarks>
    /// Resolved server side so the widget does not need to know whether the artwork comes from the
    /// Jellyfin library or from Trakt. Null when neither has a poster, which the widget renders as a
    /// plain tile.
    /// </remarks>
    [JsonPropertyName("imagePath")]
    public string? ImagePath { get; set; }

    /// <summary>
    /// Gets or sets the API path to try when <see cref="ImagePath"/> fails to load.
    /// </summary>
    /// <remarks>
    /// Set when the library holds artwork for the show but Trakt could stand in for it, so a stale
    /// or removed library image falls back to something rather than to a blank tile.
    /// </remarks>
    [JsonPropertyName("fallbackImagePath")]
    public string? FallbackImagePath { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this season has already been requested from the widget
    /// since the server started.
    /// </summary>
    [JsonPropertyName("requested")]
    public bool Requested { get; set; }
}
