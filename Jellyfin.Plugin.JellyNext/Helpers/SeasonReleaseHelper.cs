using System;
using Jellyfin.Plugin.JellyNext.Models.Common;

namespace Jellyfin.Plugin.JellyNext.Helpers;

/// <summary>
/// Decides whether a season counts as a new release.
/// </summary>
/// <remarks>
/// Shared by the per-user "only newly released seasons" library filter and by new-season email
/// notifications. Both answer the same question over different windows, and they must agree on what
/// "new" means - otherwise the library and the announcements would disagree about the same season.
/// </remarks>
public static class SeasonReleaseHelper
{
    /// <summary>
    /// Determines whether a season is currently airing.
    /// </summary>
    /// <param name="show">The cached show.</param>
    /// <param name="season">The season metadata.</param>
    /// <returns>True when the season has started but not finished airing.</returns>
    /// <remarks>
    /// Ended and canceled shows are excluded: their unaired episode counts are leftovers from a
    /// cancellation rather than episodes still to come.
    /// </remarks>
    public static bool IsAiring(ShowCacheEntry show, SeasonMetadata season)
    {
        return !show.IsEnded && season.AiredEpisodes > 0 && season.EpisodeCount > season.AiredEpisodes;
    }

    /// <summary>
    /// Determines whether a season counts as a new release.
    /// </summary>
    /// <param name="firstAired">The season's premiere date, if known.</param>
    /// <param name="isAiring">Whether the season is part-way through airing.</param>
    /// <param name="windowDays">How many days after its premiere a season still counts as new.</param>
    /// <returns>True when the season premiered inside the window or is still airing.</returns>
    /// <remarks>
    /// A season part-way through its run is airing right now whatever its premiere date says, which
    /// keeps long and split-cour seasons in scope past the cut-off. A season with no premiere date
    /// is excluded - this filter is meant to exclude by default rather than leak the backlog back in.
    /// </remarks>
    public static bool IsRecentlyReleased(DateTime? firstAired, bool isAiring, int windowDays)
    {
        if (isAiring)
        {
            return true;
        }

        if (!firstAired.HasValue)
        {
            return false;
        }

        var value = firstAired.Value;
        var firstAiredUtc = value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value.ToUniversalTime();

        return firstAiredUtc >= DateTime.UtcNow.AddDays(-Math.Clamp(windowDays, 1, 3650));
    }
}
