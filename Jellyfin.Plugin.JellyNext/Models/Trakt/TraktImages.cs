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
    /// Gets or sets the 16:9 backdrop URLs, highest quality first.
    /// </summary>
    [JsonPropertyName("fanart")]
    public string[] Fanart { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Gets or sets the 16:9 thumbnail URLs.
    /// </summary>
    [JsonPropertyName("thumb")]
    public string[] Thumb { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Gets or sets the poster URLs, used only when no wide artwork exists.
    /// </summary>
    [JsonPropertyName("poster")]
    public string[] Poster { get; set; } = Array.Empty<string>();
}
