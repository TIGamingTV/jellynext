namespace Jellyfin.Plugin.JellyNext.Models.Common;

/// <summary>
/// How far a user has got through a show.
/// </summary>
/// <remarks>
/// The episode is kept alongside the season because a season can stop part-way at a mid-season finale
/// and come back months later. The season number alone cannot tell "finished part one, waiting for the
/// rest" from "finished the season", and only the first deserves to have the rest of it offered.
/// </remarks>
public sealed record WatchProgress
{
    /// <summary>
    /// Gets the highest season the user has watched at least one episode of.
    /// </summary>
    public int Season { get; init; }

    /// <summary>
    /// Gets the highest episode the user has watched in <see cref="Season"/>.
    /// </summary>
    public int Episode { get; init; }
}
