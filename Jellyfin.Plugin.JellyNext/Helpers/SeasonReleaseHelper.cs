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
    /// Assumed gap between episodes of a season that is part-way through airing.
    /// </summary>
    /// <remarks>
    /// Trakt reports a season's premiere date and how many of its episodes have aired, but not when
    /// the most recent one did, so the release cadence has to be assumed to place it. Weekly is the
    /// overwhelmingly common one, and the estimate is only ever used to decide which side of a
    /// cut-off the season falls on.
    /// </remarks>
    private const double AssumedEpisodeIntervalDays = 7;

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
    /// <param name="airedEpisodes">How many of the season's episodes have aired.</param>
    /// <param name="windowDays">How many days of the season's latest release still count as new.</param>
    /// <returns>True when the season last released an episode inside the window.</returns>
    /// <remarks>
    /// The window is measured from the season's premiere, or - for a season part-way through its run -
    /// from its most recently aired episode, which keeps long and split-cour seasons in scope past a
    /// cut-off their premiere alone would fall outside of. Airing is deliberately not an unconditional
    /// pass: a weekly season stays "new" for as long as it is running, so a show the user has decided
    /// to skip could never be aged out however short a window they chose. A season with no premiere
    /// date is excluded - this filter is meant to exclude by default rather than leak the backlog
    /// back in.
    /// </remarks>
    public static bool IsRecentlyReleased(DateTime? firstAired, bool isAiring, int airedEpisodes, int windowDays)
    {
        if (!firstAired.HasValue)
        {
            return false;
        }

        var value = firstAired.Value;
        var premiered = value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value.ToUniversalTime();

        var now = DateTime.UtcNow;
        var latestRelease = premiered;

        if (isAiring && airedEpisodes > 1)
        {
            var estimated = premiered.AddDays((airedEpisodes - 1) * AssumedEpisodeIntervalDays);

            // An estimate in the future means the episodes went out faster than the assumed cadence -
            // a batch drop with later parts still to come is the usual case - so the cadence cannot be
            // trusted to place the last episode, and the premiere is the honest anchor. Taking the
            // estimate anyway would put every such season permanently inside the window.
            if (estimated <= now)
            {
                latestRelease = estimated;
            }
        }

        return latestRelease >= now.AddDays(-Math.Clamp(windowDays, 1, 3650));
    }
}
