using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyNext.Models.Trakt;

/// <summary>
/// A set of Trakt items, indexed by every ID Trakt supplies for them.
/// </summary>
/// <remarks>
/// Every ID is indexed rather than just the Trakt one because the two sides of a comparison are
/// separate Trakt payloads - a recommendation and a collected item. They agree on the Trakt ID in the
/// ordinary case, but indexing TMDB, TVDB and IMDB as well costs nothing and covers merged or
/// re-identified entries.
/// </remarks>
public sealed class TraktIdSet
{
    private readonly HashSet<int> _traktIds = new();
    private readonly HashSet<int> _tmdbIds = new();
    private readonly HashSet<int> _tvdbIds = new();
    private readonly HashSet<string> _imdbIds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the number of items added.
    /// </summary>
    public int Count { get; private set; }

    /// <summary>
    /// Adds an item's IDs to the set.
    /// </summary>
    /// <param name="ids">The item's IDs.</param>
    public void Add(TraktIds? ids)
    {
        Count++;

        if (ids == null)
        {
            return;
        }

        if (ids.Trakt > 0)
        {
            _traktIds.Add(ids.Trakt);
        }

        if (ids.Tmdb is > 0)
        {
            _tmdbIds.Add(ids.Tmdb.Value);
        }

        if (ids.Tvdb is > 0)
        {
            _tvdbIds.Add(ids.Tvdb.Value);
        }

        if (!string.IsNullOrEmpty(ids.Imdb))
        {
            _imdbIds.Add(ids.Imdb);
        }
    }

    /// <summary>
    /// Checks whether an item is in the set, matching on any ID the two sides share.
    /// </summary>
    /// <param name="ids">The item's IDs.</param>
    /// <returns>True when the item is in the set.</returns>
    public bool Contains(TraktIds? ids)
    {
        if (ids == null)
        {
            return false;
        }

        if (ids.Trakt > 0 && _traktIds.Contains(ids.Trakt))
        {
            return true;
        }

        if (ids.Tmdb is > 0 && _tmdbIds.Contains(ids.Tmdb.Value))
        {
            return true;
        }

        if (ids.Tvdb is > 0 && _tvdbIds.Contains(ids.Tvdb.Value))
        {
            return true;
        }

        return !string.IsNullOrEmpty(ids.Imdb) && _imdbIds.Contains(ids.Imdb);
    }
}
