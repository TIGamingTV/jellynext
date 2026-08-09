using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyNext.Services;

/// <summary>
/// Writes JellyNext's tag onto the library items its virtual libraries produce, and takes it off
/// again when the feature is switched off or the tag is renamed.
/// </summary>
/// <remarks>
/// Once Jellyfin has scanned a stub there is nothing on the item that says it is a recommendation
/// rather than something the server holds - the whole point on a client that offers no other way to
/// ask for a download, and misleading everywhere else. A tag is the only marker Jellyfin itself
/// understands: it is shown on the item, it can be searched and filtered on, a smart collection can
/// be built from it, and it is what the client script keys its badge and request icon off.
///
/// The tag is applied from two directions, because neither alone is enough. The <see
/// cref="ILibraryManager.ItemAdded"/> subscription catches items as they are created, including by
/// Jellyfin's own library scan which the plugin does not drive. The sweep catches everything else -
/// items that already existed when the feature was switched on, a renamed tag, and any item the
/// event was missed for. Both are cheap: an item that already carries the right tags is not written.
/// </remarks>
public class VirtualItemTagService : IHostedService
{
    /// <summary>
    /// The path segment every virtual library item is under. The plugin identifies its own items by
    /// path everywhere else too - see <c>PlaybackInterceptor</c> and <c>LocalLibraryService</c>.
    /// </summary>
    private const string VirtualPathMarker = "jellynext-virtual";

    private readonly ILogger<VirtualItemTagService> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;

    // A sweep runs from a scheduled task, from a configuration save and (indirectly) from startup;
    // two of them writing the same items at once would be pointless work at best.
    private readonly SemaphoreSlim _sweepLock = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="VirtualItemTagService"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="userManager">The user manager.</param>
    public VirtualItemTagService(
        ILogger<VirtualItemTagService> logger,
        ILibraryManager libraryManager,
        IUserManager userManager)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _userManager = userManager;
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemAdded;

        if (Plugin.Instance != null)
        {
            Plugin.Instance.ConfigurationChanged += OnConfigurationChanged;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemAdded;

        if (Plugin.Instance != null)
        {
            Plugin.Instance.ConfigurationChanged -= OnConfigurationChanged;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Brings every virtual library item's tags in line with the current configuration.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the sweep.</returns>
    /// <remarks>
    /// Runs after the content sync's library scan, so items created by that scan are tagged before
    /// anybody sees them, and whenever the configuration is saved, so switching the feature on or off
    /// takes effect immediately rather than in up to six hours.
    /// </remarks>
    public async Task ApplyAsync(CancellationToken cancellationToken)
    {
        var configuration = Plugin.Instance?.Configuration;
        if (configuration == null)
        {
            return;
        }

        var desiredTag = configuration.MediaTaggingEnabled ? NormalizeTag(configuration.MediaTagName) : null;
        var staleTags = new List<string>();
        AddTag(staleTags, configuration.LastAppliedMediaTag);

        if (desiredTag == null)
        {
            AddTag(staleTags, configuration.MediaTagName);
        }

        if (desiredTag == null && staleTags.Count == 0)
        {
            // Never been on, still off - there is nothing on any item to correct.
            return;
        }

        await _sweepLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var updated = 0;

            foreach (var (item, isVirtual) in CollectItems(staleTags))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var changed = await ApplyToItemAsync(item, isVirtual, desiredTag, staleTags, cancellationToken)
                    .ConfigureAwait(false);

                if (changed)
                {
                    updated++;
                }
            }

            if (updated > 0)
            {
                _logger.LogInformation(
                    "{Action} the {Tag} tag on {Count} virtual library items",
                    desiredTag == null ? "Removed" : "Applied",
                    desiredTag ?? string.Join(", ", staleTags),
                    updated);
            }

            RememberAppliedTag(desiredTag);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not update the tags on the virtual library items");
        }
        finally
        {
            _sweepLock.Release();
        }
    }

    /// <summary>
    /// Gets the ids of the tagged items the given user can see.
    /// </summary>
    /// <param name="userId">The Jellyfin user asking.</param>
    /// <returns>Item ids in hyphenless form, as the web client's card markup carries them.</returns>
    /// <remarks>
    /// The client cannot ask Jellyfin "is this card a JellyNext item" per card without a request per
    /// card, so it is given the whole set once and matches locally. The list is bounded by a limit
    /// rather than trusted to stay small: it is a decoration, and a user with an enormous virtual
    /// library is better served by an incomplete one than by a megabyte of ids on every page load.
    /// </remarks>
    public IReadOnlyList<string> GetTaggedItemIds(Guid userId)
    {
        var configuration = Plugin.Instance?.Configuration;
        var tag = NormalizeTag(configuration?.MediaTagName);

        if (configuration?.MediaTaggingEnabled != true || tag == null)
        {
            return Array.Empty<string>();
        }

        try
        {
            User? user = userId == Guid.Empty ? null : _userManager.GetUserById(userId);

            // Built from the user where there is one, so Jellyfin applies their parental and tag
            // restrictions rather than this handing back ids they are not allowed to see.
            var query = user == null ? new InternalItemsQuery() : new InternalItemsQuery(user);
            query.Tags = new[] { tag };
            query.Recursive = true;
            query.Limit = 10000;

            return _libraryManager.GetItemList(query)
                .Select(item => item.Id.ToString("N"))
                .ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not list the tagged virtual library items");
            return Array.Empty<string>();
        }
    }

    private static string? NormalizeTag(string? tag)
    {
        var trimmed = tag?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static void AddTag(List<string> tags, string? tag)
    {
        var normalized = NormalizeTag(tag);
        if (normalized != null && !tags.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            tags.Add(normalized);
        }
    }

    private static bool IsVirtualItem(BaseItem item)
    {
        return item.Path?.Contains(VirtualPathMarker, StringComparison.OrdinalIgnoreCase) == true;
    }

    private void OnConfigurationChanged(object? sender, BasePluginConfiguration configuration)
    {
        // Fire and forget: the configuration endpoint must not wait on a library sweep, and a failure
        // here is already logged and self-corrects on the next content sync.
        _ = Task.Run(() => ApplyAsync(CancellationToken.None));
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        var configuration = Plugin.Instance?.Configuration;
        if (configuration?.MediaTaggingEnabled != true)
        {
            return;
        }

        var tag = NormalizeTag(configuration.MediaTagName);
        if (tag == null || e.Item == null || !IsVirtualItem(e.Item))
        {
            return;
        }

        // Fire and forget: this runs on the library scan's thread, and a tag is not worth holding it
        // up or failing it for.
        var item = e.Item;
        _ = Task.Run(async () =>
        {
            try
            {
                await ApplyToItemAsync(item, true, tag, Array.Empty<string>(), CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not tag {Path}", item.Path);
            }
        });
    }

    /// <summary>
    /// Gathers the items a sweep has to consider.
    /// </summary>
    /// <param name="staleTags">Tags previously written by the plugin.</param>
    /// <returns>
    /// The distinct items under a virtual library, plus anything still carrying a stale tag, each
    /// paired with whether it is one of JellyNext's.
    /// </returns>
    /// <remarks>
    /// Two sources because neither covers the other. The virtual libraries are where the items that
    /// need the tag are, found by walking down from the folders Jellyfin was pointed at rather than
    /// by scanning the whole library. The tag query then picks up items that have since left those
    /// folders - a library the admin removed and re-added elsewhere, a stub whose content type was
    /// switched off - which would otherwise keep a tag nothing ever takes off again.
    ///
    /// Membership is carried out of the walk rather than re-derived from the path afterwards: a
    /// season Jellyfin inferred from an episode filename has no folder of its own and therefore no
    /// path to recognize, and those are exactly the seasons the Next Seasons library is made of.
    /// </remarks>
    private IEnumerable<(BaseItem Item, bool IsVirtual)> CollectItems(IReadOnlyCollection<string> staleTags)
    {
        var seen = new HashSet<Guid>();

        foreach (var item in QueryVirtualLibraryItems())
        {
            if (seen.Add(item.Id))
            {
                yield return (item, true);
            }
        }

        if (staleTags.Count == 0)
        {
            yield break;
        }

        foreach (var item in QueryTaggedItems(staleTags))
        {
            if (seen.Add(item.Id))
            {
                yield return (item, IsVirtualItem(item));
            }
        }
    }

    private IReadOnlyList<BaseItem> QueryVirtualLibraryItems()
    {
        try
        {
            var roots = _libraryManager.GetVirtualFolders()
                .SelectMany(folder => folder.Locations)
                .Where(location => location.Contains(VirtualPathMarker, StringComparison.OrdinalIgnoreCase))
                .Select(location => _libraryManager.FindByPath(location, isFolder: true))
                .OfType<Folder>()
                .Select(folder => folder.Id)
                .Distinct()
                .ToArray();

            if (roots.Length == 0)
            {
                return Array.Empty<BaseItem>();
            }

            return _libraryManager.GetItemList(new InternalItemsQuery
            {
                AncestorIds = roots,
                Recursive = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not list the virtual library items");
            return Array.Empty<BaseItem>();
        }
    }

    private IReadOnlyList<BaseItem> QueryTaggedItems(IReadOnlyCollection<string> tags)
    {
        try
        {
            return _libraryManager.GetItemList(new InternalItemsQuery
            {
                Tags = tags.ToArray(),
                Recursive = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not list the items carrying a JellyNext tag");
            return Array.Empty<BaseItem>();
        }
    }

    /// <summary>
    /// Sets one item's tags, writing only when they actually change.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="isVirtual">Whether the item is one of JellyNext's.</param>
    /// <param name="desiredTag">The tag it should carry, or null for none.</param>
    /// <param name="staleTags">Tags to take off it.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True when the item was written.</returns>
    private async Task<bool> ApplyToItemAsync(
        BaseItem item,
        bool isVirtual,
        string? desiredTag,
        IReadOnlyCollection<string> staleTags,
        CancellationToken cancellationToken)
    {
        var existing = item.Tags ?? Array.Empty<string>();

        var kept = existing
            .Where(tag => !staleTags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            .ToList();

        // The tag is only ever put on the plugin's own items. Anything the tag query dragged in that
        // is no longer one of them just gets the stale tag taken off.
        if (desiredTag != null
            && isVirtual
            && !kept.Contains(desiredTag, StringComparer.OrdinalIgnoreCase))
        {
            kept.Add(desiredTag);
        }

        if (kept.Count == existing.Length && !existing.Except(kept, StringComparer.Ordinal).Any())
        {
            return false;
        }

        var parent = item.GetParent();
        if (parent == null)
        {
            return false;
        }

        item.Tags = kept.ToArray();

        // ItemUpdateType.None, not MetadataEdit: anything at or above MetadataDownload sends the item
        // through Jellyfin's metadata savers and image writer, which for a library with the NFO saver
        // on - the default - would drop an .nfo beside every stub and pull artwork down onto disk.
        // Only the database row needs to change, and SaveItems runs whatever the reason given.
        await _libraryManager
            .UpdateItemAsync(item, parent, ItemUpdateType.None, cancellationToken)
            .ConfigureAwait(false);

        return true;
    }

    /// <summary>
    /// Records the tag that is now on the items.
    /// </summary>
    /// <param name="tag">The tag applied, or null when tagging is off.</param>
    /// <remarks>
    /// Saved rather than kept in memory because the case it exists for - the tag was renamed, or the
    /// feature switched off - is exactly the case where the previous value has to survive whatever
    /// happens between the change and the next sweep, including a restart.
    ///
    /// <c>SaveConfiguration</c> and not <c>UpdateConfiguration</c>: the latter raises
    /// <c>ConfigurationChanged</c>, which is what invokes the sweep, and a sweep that schedules
    /// another sweep is a loop.
    /// </remarks>
    private void RememberAppliedTag(string? tag)
    {
        var configuration = Plugin.Instance?.Configuration;
        if (configuration == null)
        {
            return;
        }

        var applied = tag ?? string.Empty;
        if (string.Equals(configuration.LastAppliedMediaTag, applied, StringComparison.Ordinal))
        {
            return;
        }

        configuration.LastAppliedMediaTag = applied;
        Plugin.Instance?.SaveConfiguration();
    }
}
