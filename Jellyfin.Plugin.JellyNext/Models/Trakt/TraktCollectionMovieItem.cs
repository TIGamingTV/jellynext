using System;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyNext.Models.Trakt;

/// <summary>
/// Represents a collected movie from the Trakt API.
/// </summary>
public class TraktCollectionMovieItem
{
    /// <summary>
    /// Gets or sets when the movie was collected.
    /// </summary>
    [JsonPropertyName("collected_at")]
    public DateTime CollectedAt { get; set; }

    /// <summary>
    /// Gets or sets the movie.
    /// </summary>
    [JsonPropertyName("movie")]
    public TraktMovie Movie { get; set; } = new TraktMovie();
}
