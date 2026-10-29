using System;

namespace Jellyfin.Plugin.JellyNext.Models.Common;

/// <summary>
/// Represents a content item (movie or show) from an external source.
/// </summary>
public class ContentItem
{
    /// <summary>
    /// Gets or sets the content type.
    /// </summary>
    public ContentType Type { get; set; }

    /// <summary>
    /// Gets or sets the title.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the year.
    /// </summary>
    public int? Year { get; set; }

    /// <summary>
    /// Gets or sets the TMDB ID.
    /// </summary>
    public int? TmdbId { get; set; }

    /// <summary>
    /// Gets or sets the IMDB ID.
    /// </summary>
    public string? ImdbId { get; set; }

    /// <summary>
    /// Gets or sets the TVDB ID (for shows).
    /// </summary>
    public int? TvdbId { get; set; }

    /// <summary>
    /// Gets or sets the provider-specific data.
    /// </summary>
    public string ProviderName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the timestamp when this item was added.
    /// </summary>
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Gets or sets the Trakt ID.
    /// </summary>
    public int TraktId { get; set; }

    /// <summary>
    /// Gets or sets the season number (for next seasons provider).
    /// </summary>
    public int? SeasonNumber { get; set; }

    /// <summary>
    /// Gets or sets the number of seasons that have aired (for shows).
    /// </summary>
    public int? AiredSeasonCount { get; set; }

    /// <summary>
    /// Gets or sets the release date of what is being offered, if known: the premiere of
    /// <see cref="SeasonNumber"/>, or the date it came back when <see cref="SeasonResumesAtEpisode"/> is set.
    /// </summary>
    /// <remarks>
    /// A resumed season carries its return date rather than its premiere so the new-release window,
    /// the widget's ordering and the email announcement all treat the second half as the release it is.
    /// Measured from the premiere, a season that broke for three months would already be outside every
    /// window by the time it came back.
    /// </remarks>
    public DateTime? SeasonFirstAired { get; set; }

    /// <summary>
    /// Gets or sets the first episode of the part being offered when <see cref="SeasonNumber"/> has come
    /// back from a mid-season break; null when the whole season is offered.
    /// </summary>
    public int? SeasonResumesAtEpisode { get; set; }

    /// <summary>
    /// Gets or sets the total number of episodes <see cref="SeasonNumber"/> will have, if known.
    /// </summary>
    public int? SeasonEpisodeCount { get; set; }

    /// <summary>
    /// Gets or sets the number of episodes of <see cref="SeasonNumber"/> that have aired, if known.
    /// </summary>
    public int? SeasonAiredEpisodes { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether <see cref="SeasonNumber"/> is part-way through airing.
    /// </summary>
    /// <remarks>
    /// Carried on the item because the season a user is being pointed at is often absent from the
    /// season cache - an ongoing show's incomplete season is fetched on demand and deliberately not
    /// cached - and that is exactly the season a "new season" notification is about.
    /// </remarks>
    public bool SeasonIsAiring { get; set; }

    /// <summary>
    /// Gets or sets the genres.
    /// </summary>
    public string[] Genres { get; set; } = Array.Empty<string>();
}
