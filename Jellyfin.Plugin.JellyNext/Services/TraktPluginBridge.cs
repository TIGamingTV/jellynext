using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.JellyNext.Configuration;
using Jellyfin.Plugin.JellyNext.Models.Trakt;
using MediaBrowser.Common.Plugins;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyNext.Services;

/// <summary>
/// Reads (and, when permitted, writes) the OAuth tokens held by the official Jellyfin Trakt plugin.
/// </summary>
/// <remarks>
/// <para>
/// Jellyfin loads every plugin into its own <see cref="System.Runtime.Loader.AssemblyLoadContext"/>,
/// so JellyNext cannot reference the Trakt plugin's assembly and cast to its types — the same type
/// name loaded in two contexts is two distinct types. Everything Trakt-specific here therefore goes
/// through reflection. The host abstractions used to reach the plugin (<see cref="IPluginManager"/>,
/// <see cref="IHasPluginConfiguration"/>) live in MediaBrowser.Common, which resolves from the
/// default load context in every plugin context, so those casts are type-safe.
/// </para>
/// <para>
/// Reads and writes both target the plugin's live in-memory configuration object rather than
/// Trakt.xml on disk. Writing the file directly would be silently overwritten the next time the
/// official plugin saves its own cached configuration.
/// </para>
/// </remarks>
public class TraktPluginBridge
{
    /// <summary>
    /// The plugin id of jellyfin/jellyfin-plugin-trakt.
    /// </summary>
    public static readonly Guid TraktPluginId = new Guid("4fe3201e-d6ae-4f2e-8917-e12bda571281");

    private const string TraktUsersPropertyName = "TraktUsers";
    private const string LinkedUserIdPropertyName = "LinkedMbUserId";
    private const string AccessTokenPropertyName = "AccessToken";
    private const string RefreshTokenPropertyName = "RefreshToken";
    private const string ExpirationPropertyName = "AccessTokenExpiration";

    private readonly ILogger<TraktPluginBridge> _logger;
    private readonly IPluginManager _pluginManager;
    private readonly ConcurrentDictionary<Type, TraktUserAccessors?> _accessorCache = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="TraktPluginBridge"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="pluginManager">The Jellyfin plugin manager.</param>
    public TraktPluginBridge(ILogger<TraktPluginBridge> logger, IPluginManager pluginManager)
    {
        _logger = logger;
        _pluginManager = pluginManager;
    }

    /// <summary>
    /// Gets a value indicating whether the official Trakt plugin is installed, enabled and readable.
    /// </summary>
    public bool IsAvailable => ResolveConfiguration() != null;

    /// <summary>
    /// Gets the version of the installed official Trakt plugin, or null when it is unavailable.
    /// </summary>
    public string? PluginVersion => FindTraktPlugin()?.Version?.ToString();

    /// <summary>
    /// Gets the Jellyfin user ids the official Trakt plugin holds an access token for.
    /// </summary>
    /// <returns>The linked user ids, empty when the plugin is unavailable.</returns>
    public IReadOnlyList<Guid> GetLinkedUserIds()
    {
        var config = ResolveConfiguration();
        if (config == null)
        {
            return Array.Empty<Guid>();
        }

        var linked = new List<Guid>();

        foreach (var traktUser in EnumerateTraktUsers(config))
        {
            var accessors = GetAccessors(traktUser.GetType());
            if (accessors == null)
            {
                continue;
            }

            if (accessors.LinkedUserId.GetValue(traktUser) is Guid userId
                && !userId.Equals(Guid.Empty)
                && !string.IsNullOrWhiteSpace(accessors.AccessToken.GetValue(traktUser) as string))
            {
                linked.Add(userId);
            }
        }

        return linked;
    }

    /// <summary>
    /// Reads the token the official Trakt plugin holds for a Jellyfin user.
    /// </summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <returns>The token snapshot, or null when the plugin or the user link is missing.</returns>
    public TraktPluginToken? GetToken(Guid userId)
    {
        var traktUser = FindTraktUser(userId, out var accessors);
        if (traktUser == null || accessors == null)
        {
            return null;
        }

        var token = new TraktPluginToken
        {
            AccessToken = accessors.AccessToken.GetValue(traktUser) as string ?? string.Empty,
            RefreshToken = accessors.RefreshToken.GetValue(traktUser) as string ?? string.Empty,
            AccessTokenExpiration = accessors.Expiration.GetValue(traktUser) as DateTime? ?? DateTime.MinValue
        };

        return token.HasAccessToken ? token : null;
    }

    /// <summary>
    /// Determines whether a rotated token pair could be written back to the official Trakt plugin.
    /// </summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <returns>True when the write path is available.</returns>
    /// <remarks>
    /// Checked before refreshing a borrowed token: Trakt refresh tokens are single use, so a refresh
    /// that cannot be persisted would leave the official plugin holding a dead refresh token.
    /// </remarks>
    public bool CanPersistToken(Guid userId)
    {
        var traktUser = FindTraktUser(userId, out var accessors);
        return traktUser != null
            && accessors != null
            && accessors.IsWritable
            && ResolveConfigurationHolder() != null;
    }

    /// <summary>
    /// Writes a rotated token pair into the official Trakt plugin's live configuration and persists it.
    /// </summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="accessToken">The new access token.</param>
    /// <param name="refreshToken">The new refresh token.</param>
    /// <param name="expiration">The new expiration timestamp, already including a safety buffer.</param>
    /// <returns>True when the values were written and saved.</returns>
    public bool TryPersistToken(Guid userId, string accessToken, string refreshToken, DateTime expiration)
    {
        var holder = ResolveConfigurationHolder();
        var traktUser = FindTraktUser(userId, out var accessors);

        if (holder == null || traktUser == null || accessors == null || !accessors.IsWritable)
        {
            _logger.LogError(
                "Cannot write refreshed Trakt tokens back to the Trakt plugin for user {UserId}",
                userId);
            return false;
        }

        try
        {
            // Mutating the live object first means the official plugin sees the new tokens even if
            // the save below fails.
            accessors.AccessToken.SetValue(traktUser, accessToken);
            accessors.RefreshToken.SetValue(traktUser, refreshToken);
            accessors.Expiration.SetValue(traktUser, expiration);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update the Trakt plugin's tokens for user {UserId}", userId);
            return false;
        }

        try
        {
            // UpdateConfiguration persists the same live instance the official plugin caches.
            holder.UpdateConfiguration(holder.Configuration);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Updated the Trakt plugin's tokens in memory for user {UserId} but could not persist "
                + "Trakt.xml. The rotated refresh token will be lost if Jellyfin restarts before the "
                + "Trakt plugin saves its configuration",
                userId);
            return false;
        }

        _logger.LogInformation(
            "Wrote refreshed Trakt tokens back to the Trakt plugin for user {UserId}",
            userId);
        return true;
    }

    /// <summary>
    /// Determines whether a user has a usable Trakt token under the configured authorization mode.
    /// </summary>
    /// <param name="traktUser">The JellyNext user configuration.</param>
    /// <returns>True when JellyNext can make authenticated Trakt calls for this user.</returns>
    public bool HasUsableToken(TraktUser? traktUser)
    {
        if (traktUser == null)
        {
            return false;
        }

        if (Plugin.Instance?.Configuration.TraktAuthMode == TraktAuthMode.SharedTraktPluginToken)
        {
            return GetToken(traktUser.LinkedMbUserId) != null;
        }

        return !string.IsNullOrWhiteSpace(traktUser.AccessToken);
    }

    private LocalPlugin? FindTraktPlugin()
    {
        return _pluginManager.Plugins
            .FirstOrDefault(plugin => plugin.Id.Equals(TraktPluginId) && plugin.IsEnabledAndSupported);
    }

    private IHasPluginConfiguration? ResolveConfigurationHolder()
    {
        return FindTraktPlugin()?.Instance as IHasPluginConfiguration;
    }

    private object? ResolveConfiguration()
    {
        return ResolveConfigurationHolder()?.Configuration;
    }

    private IEnumerable<object> EnumerateTraktUsers(object configuration)
    {
        var usersProperty = configuration.GetType().GetProperty(TraktUsersPropertyName);
        if (usersProperty?.GetValue(configuration) is not IEnumerable users)
        {
            _logger.LogWarning(
                "The Trakt plugin's configuration has no readable '{Property}' property. Its layout "
                + "may have changed in an incompatible way",
                TraktUsersPropertyName);
            return Array.Empty<object>();
        }

        return users.Cast<object?>().OfType<object>();
    }

    private object? FindTraktUser(Guid userId, out TraktUserAccessors? accessors)
    {
        accessors = null;

        var config = ResolveConfiguration();
        if (config == null)
        {
            return null;
        }

        foreach (var traktUser in EnumerateTraktUsers(config))
        {
            var candidateAccessors = GetAccessors(traktUser.GetType());
            if (candidateAccessors == null)
            {
                return null;
            }

            if (candidateAccessors.LinkedUserId.GetValue(traktUser) is Guid linkedId && linkedId.Equals(userId))
            {
                accessors = candidateAccessors;
                return traktUser;
            }
        }

        return null;
    }

    private TraktUserAccessors? GetAccessors(Type traktUserType)
    {
        return _accessorCache.GetOrAdd(traktUserType, BuildAccessors);
    }

    private TraktUserAccessors? BuildAccessors(Type traktUserType)
    {
        var linkedUserId = traktUserType.GetProperty(LinkedUserIdPropertyName);
        var accessToken = traktUserType.GetProperty(AccessTokenPropertyName);
        var refreshToken = traktUserType.GetProperty(RefreshTokenPropertyName);
        var expiration = traktUserType.GetProperty(ExpirationPropertyName);

        if (linkedUserId == null || accessToken == null || refreshToken == null || expiration == null)
        {
            _logger.LogWarning(
                "The Trakt plugin's user model is missing one of the expected properties "
                + "({Expected}). Shared Trakt authorization is unavailable",
                $"{LinkedUserIdPropertyName}, {AccessTokenPropertyName}, {RefreshTokenPropertyName}, {ExpirationPropertyName}");
            return null;
        }

        return new TraktUserAccessors(linkedUserId, accessToken, refreshToken, expiration);
    }

    private sealed class TraktUserAccessors
    {
        public TraktUserAccessors(
            PropertyInfo linkedUserId,
            PropertyInfo accessToken,
            PropertyInfo refreshToken,
            PropertyInfo expiration)
        {
            LinkedUserId = linkedUserId;
            AccessToken = accessToken;
            RefreshToken = refreshToken;
            Expiration = expiration;
        }

        public PropertyInfo LinkedUserId { get; }

        public PropertyInfo AccessToken { get; }

        public PropertyInfo RefreshToken { get; }

        public PropertyInfo Expiration { get; }

        public bool IsWritable =>
            AccessToken.CanWrite && RefreshToken.CanWrite && Expiration.CanWrite;
    }
}
