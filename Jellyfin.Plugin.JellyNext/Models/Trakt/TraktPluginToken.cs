using System;

namespace Jellyfin.Plugin.JellyNext.Models.Trakt;

/// <summary>
/// A snapshot of the OAuth token the official Jellyfin Trakt plugin holds for a Jellyfin user.
/// </summary>
public class TraktPluginToken
{
    /// <summary>
    /// Gets or sets the access token.
    /// </summary>
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the refresh token.
    /// </summary>
    public string RefreshToken { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the access token expiration timestamp.
    /// </summary>
    /// <remarks>
    /// The official plugin stores this as a local <see cref="DateTime"/> already reduced by its own
    /// 75% safety buffer, so it is compared against <see cref="DateTime.Now"/>, not UTC.
    /// </remarks>
    public DateTime AccessTokenExpiration { get; set; }

    /// <summary>
    /// Gets a value indicating whether a usable access token is present.
    /// </summary>
    public bool HasAccessToken => !string.IsNullOrWhiteSpace(AccessToken);

    /// <summary>
    /// Gets a value indicating whether the access token has passed its expiration timestamp.
    /// </summary>
    public bool IsExpired => DateTime.Now >= AccessTokenExpiration;
}
