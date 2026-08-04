using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyNext.Models.Trakt;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyNext.Services;

/// <summary>
/// Supplies each user's Trakt collection so recommendations can be filtered against it locally.
/// </summary>
/// <remarks>
/// <para>
/// Trakt's own <c>ignore_collected=true</c> on the recommendation endpoints is unreliable: it
/// regularly returns titles that are in the user's collection, which the virtual library then offers
/// for download. Filtering again with the collection in hand is the only way to make the setting mean
/// what it says.
/// </para>
/// <para>
/// Movies and shows are fetched and cached separately so a user who only syncs one of them never pays
/// for the other. Cached for <see cref="CacheDuration"/> per user and kind, which is long enough for
/// one sync run - where the recommendation fetch and the trending fetch both consult it - and short
/// enough that a title collected today is gone from the suggestions by the next run.
/// </para>
/// <para>
/// A failed fetch is deliberately not cached, and answers <c>null</c> rather than an empty set: an
/// empty collection means "nothing to filter", which is right for a user with no collection and wrong
/// for a user Trakt just failed to answer for.
/// </para>
/// </remarks>
public class TraktCollectionService
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(30);

    private readonly ILogger<TraktCollectionService> _logger;
    private readonly TraktApi _traktApi;
    private readonly ConcurrentDictionary<(Guid UserId, string Kind), CacheEntry> _cache = new();
    private readonly ConcurrentDictionary<(Guid UserId, string Kind), SemaphoreSlim> _locks = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="TraktCollectionService"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="traktApi">The Trakt API service.</param>
    public TraktCollectionService(ILogger<TraktCollectionService> logger, TraktApi traktApi)
    {
        _logger = logger;
        _traktApi = traktApi;
    }

    /// <summary>
    /// Gets the IDs of every movie in the user's Trakt collection.
    /// </summary>
    /// <param name="traktUser">The Trakt user configuration.</param>
    /// <returns>
    /// The collected movies, or null when Trakt could not be asked - in which case the caller must
    /// leave the content unfiltered rather than assume an empty collection.
    /// </returns>
    /// <exception cref="TraktAuthenticationException">Thrown when Trakt rejected the credentials.</exception>
    public Task<TraktIdSet?> GetCollectedMovieIdsAsync(TraktUser traktUser)
    {
        return GetOrFetchAsync(traktUser, "movies", async user =>
        {
            var set = new TraktIdSet();
            foreach (var movie in await _traktApi.GetCollectedMovies(user))
            {
                set.Add(movie.Ids);
            }

            return set;
        });
    }

    /// <summary>
    /// Gets the IDs of every show in the user's Trakt collection.
    /// </summary>
    /// <param name="traktUser">The Trakt user configuration.</param>
    /// <returns>
    /// The collected shows, or null when Trakt could not be asked - in which case the caller must
    /// leave the content unfiltered rather than assume an empty collection.
    /// </returns>
    /// <exception cref="TraktAuthenticationException">Thrown when Trakt rejected the credentials.</exception>
    public Task<TraktIdSet?> GetCollectedShowIdsAsync(TraktUser traktUser)
    {
        return GetOrFetchAsync(traktUser, "shows", async user =>
        {
            var set = new TraktIdSet();
            foreach (var show in await _traktApi.GetCollectedShows(user))
            {
                set.Add(show.Ids);
            }

            return set;
        });
    }

    private async Task<TraktIdSet?> GetOrFetchAsync(
        TraktUser traktUser,
        string kind,
        Func<TraktUser, Task<TraktIdSet>> fetch)
    {
        var key = (traktUser.LinkedMbUserId, kind);

        if (TryGetCached(key, out var cached))
        {
            return cached;
        }

        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();

        try
        {
            if (TryGetCached(key, out cached))
            {
                return cached;
            }

            var ids = await fetch(traktUser);
            _cache[key] = new CacheEntry(ids, DateTime.UtcNow);

            _logger.LogDebug(
                "Cached {Count} collected {Kind} for user {UserId}",
                ids.Count,
                kind,
                traktUser.LinkedMbUserId);

            return ids;
        }
        catch (TraktAuthenticationException)
        {
            // Surface auth failures so the caller skips the cycle instead of caching an empty result.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Could not read the collected {Kind} for user {UserId}; content will not be filtered against the Trakt collection this cycle",
                kind,
                traktUser.LinkedMbUserId);
            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    private bool TryGetCached((Guid UserId, string Kind) key, out TraktIdSet? ids)
    {
        if (_cache.TryGetValue(key, out var entry) && DateTime.UtcNow - entry.FetchedAt < CacheDuration)
        {
            ids = entry.Ids;
            return true;
        }

        ids = null;
        return false;
    }

    private sealed record CacheEntry(TraktIdSet Ids, DateTime FetchedAt);
}
