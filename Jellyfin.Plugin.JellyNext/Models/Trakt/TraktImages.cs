using System;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyNext.Models.Trakt;

/// <summary>
/// Image URLs returned by Trakt for <c>extended=images</c>.
/// </summary>
/// <remarks>
/// The URLs are protocol relative (<c>walter.trakt.tv/...</c>), so a scheme has to be added before
/// they can be used.
/// </remarks>
public class TraktImages
{
    /// <summary>
    /// Gets or sets the poster URLs, highest quality first.
    /// </summary>
    [JsonPropertyName("poster")]
    public string[] Poster { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Gets or sets the thumbnail URLs, used when no poster is available.
    /// </summary>
    [JsonPropertyName("thumb")]
    public string[] Thumb { get; set; } = Array.Empty<string>();
}
