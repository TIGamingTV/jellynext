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
    /// <param name="windowDays">How many days after its release a season still counts as new.</param>
    /// <returns>True when the season was released inside the window.</returns>
    /// <remarks>
    /// The window runs from the season's release and nothing else, so a season always disappears once
    /// it is that many days old. Airing used to extend it - first as an unconditional pass, then
    /// measured from the latest episode - and both readings mean a weekly season outlives the window
    /// the user set, which is the one thing this setting exists to prevent. A season with no premiere
    /// date is excluded: the filter is meant to exclude by default rather than leak the backlog back
    /// in.
    /// </remarks>
    public static bool IsRecentlyReleased(DateTime? firstAired, int windowDays)
    {
        if (!firstAired.HasValue)
        {
            return false;
        }

        var value = firstAired.Value;
        var released = value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value.ToUniversalTime();

        return released >= DateTime.UtcNow.AddDays(-Math.Clamp(windowDays, 1, 3650));
    }
}
