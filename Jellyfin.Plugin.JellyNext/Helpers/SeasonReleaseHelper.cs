using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyNext.Models.Common;
using Jellyfin.Plugin.JellyNext.Models.Trakt;

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
    // The shortest gap between two consecutive episodes that counts as a mid-season break. Weekly
    // shows routinely skip a week or two around holidays; a split season is off air for months. Four
    // weeks separates the two without needing per-show knowledge.
    private static readonly TimeSpan MidSeasonBreak = TimeSpan.FromDays(28);

    /// <summary>
    /// Finds the part of a season that came back from a mid-season break after the user's last episode.
    /// </summary>
    /// <param name="episodes">The season's episodes with their air dates.</param>
    /// <param name="lastWatchedEpisode">The highest episode of the season the user has watched.</param>
    /// <returns>The first episode of the resumed part and the date it aired, or null when there is none.</returns>
    /// <remarks>
    /// A part only counts when the user stopped right at the break - the episode after their last one
    /// aired at least <see cref="MidSeasonBreak"/> later. Without that, a weekly episode the user simply
    /// has not watched yet would read as a new release every week, and so would every episode of a
    /// show they dropped half-way through.
    /// </remarks>
    public static (int FromEpisode, DateTime Released)? FindResumedPart(
        IReadOnlyList<TraktSeasonEpisode> episodes, int lastWatchedEpisode)
    {
        var now = DateTime.UtcNow;
        var aired = episodes
            .Where(e => e.FirstAired.HasValue && ToUtc(e.FirstAired.Value) <= now)
            .OrderBy(e => e.Number)
            .ToList();

        var last = aired.LastOrDefault(e => e.Number <= lastWatchedEpisode);
        var next = aired.FirstOrDefault(e => e.Number > lastWatchedEpisode);
        if (last == null || next == null)
        {
            return null;
        }

        var resumed = ToUtc(next.FirstAired!.Value);
        return resumed - ToUtc(last.FirstAired!.Value) >= MidSeasonBreak
            ? (next.Number, resumed)
            : null;
    }

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

        return ToUtc(firstAired.Value) >= DateTime.UtcNow.AddDays(-Math.Clamp(windowDays, 1, 3650));
    }

    private static DateTime ToUtc(DateTime value)
    {
        return value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value.ToUniversalTime();
    }
}
