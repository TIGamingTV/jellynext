using System;

namespace Jellyfin.Plugin.JellyNext.Models.Common;

/// <summary>
/// Records that a user has already been notified about a season's release.
/// </summary>
/// <remarks>
/// Persisted with the user's configuration rather than kept in memory: a premiere is a one-time
/// event, so an in-memory set would re-send the same announcement after every Jellyfin restart.
/// Entries are pruned once they are far older than any release window, so the list stays bounded.
/// </remarks>
public class NotifiedSeason
{
    /// <summary>
    /// Gets or sets the show's TVDB ID.
    /// </summary>
    public int TvdbId { get; set; }

    /// <summary>
    /// Gets or sets the season number.
    /// </summary>
    public int SeasonNumber { get; set; }

    /// <summary>
    /// Gets or sets the timestamp the notification was sent at.
    /// </summary>
    public DateTime NotifiedAt { get; set; }
}
