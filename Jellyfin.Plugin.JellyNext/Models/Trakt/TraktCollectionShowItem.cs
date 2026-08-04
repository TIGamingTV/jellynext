using System;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyNext.Models.Trakt;

/// <summary>
/// Represents a collected show from the Trakt API.
/// </summary>
public class TraktCollectionShowItem
{
    /// <summary>
    /// Gets or sets when the show was last collected.
    /// </summary>
    [JsonPropertyName("last_collected_at")]
    public DateTime LastCollectedAt { get; set; }

    /// <summary>
    /// Gets or sets the show.
    /// </summary>
    [JsonPropertyName("show")]
    public TraktShow Show { get; set; } = new TraktShow();
}
