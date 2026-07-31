using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyNext.Models.Common;
using Jellyfin.Plugin.JellyNext.Models.Radarr;
using Jellyfin.Plugin.JellyNext.Models.Sonarr;
using Jellyfin.Plugin.JellyNext.Models.Trakt;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.JellyNext.Configuration;

/// <summary>
/// Download integration type.
/// </summary>
public enum DownloadIntegrationType
{
    /// <summary>
    /// Native integration using Radarr/Sonarr directly.
    /// </summary>
    Native = 0,

    /// <summary>
    /// Jellyseerr integration for downloads.
    /// </summary>
    Jellyseerr = 1,

    /// <summary>
    /// Webhook integration for custom download handling.
    /// </summary>
    Webhook = 2
}

/// <summary>
/// Represents a custom header for webhook requests.
/// </summary>
public class WebhookHeader
{
    /// <summary>
    /// Gets or sets the header name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the header value.
    /// </summary>
    public string Value { get; set; } = string.Empty;
}

/// <summary>
/// Plugin configuration for JellyNext.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the download integration type (Native or Jellyseerr).
    /// </summary>
    public DownloadIntegrationType DownloadIntegration { get; set; } = DownloadIntegrationType.Native;

    /// <summary>
    /// Gets or sets the Jellyseerr URL.
    /// </summary>
    public string JellyseerrUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Jellyseerr API Key.
    /// </summary>
    public string JellyseerrApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the selected Jellyseerr Radarr server ID.
    /// </summary>
    public int? JellyseerrRadarrServerId { get; set; }

    /// <summary>
    /// Gets or sets the selected Jellyseerr Radarr profile ID.
    /// </summary>
    public int? JellyseerrRadarrProfileId { get; set; }

    /// <summary>
    /// Gets or sets the selected Jellyseerr Sonarr server ID.
    /// </summary>
    public int? JellyseerrSonarrServerId { get; set; }

    /// <summary>
    /// Gets or sets the selected Jellyseerr Sonarr profile ID.
    /// </summary>
    public int? JellyseerrSonarrProfileId { get; set; }

    /// <summary>
    /// Gets or sets the selected Jellyseerr Sonarr anime profile ID (optional).
    /// </summary>
    public int? JellyseerrSonarrAnimeProfileId { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to use Jellyseerr's default Radarr configuration.
    /// </summary>
    public bool UseJellyseerrRadarrDefaults { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to use Jellyseerr's default Sonarr configuration.
    /// </summary>
    public bool UseJellyseerrSonarrDefaults { get; set; } = true;

    /// <summary>
    /// Gets or sets the Radarr URL.
    /// </summary>
    public string RadarrUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Radarr API Key.
    /// </summary>
    public string RadarrApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Radarr Quality Profile ID.
    /// </summary>
    public int? RadarrQualityProfileId { get; set; }

    /// <summary>
    /// Gets or sets the Radarr Root Folder Path.
    /// </summary>
    public string RadarrRootFolderPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Sonarr URL.
    /// </summary>
    public string SonarrUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Sonarr API Key.
    /// </summary>
    public string SonarrApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Sonarr Quality Profile ID.
    /// </summary>
    public int? SonarrQualityProfileId { get; set; }

    /// <summary>
    /// Gets or sets the Sonarr Root Folder Path.
    /// </summary>
    public string SonarrRootFolderPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Sonarr Anime Root Folder Path (optional, for separate anime library).
    /// </summary>
    public string SonarrAnimeRootFolderPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the webhook URL for movie downloads.
    /// Supports placeholders: {tmdbId}, {imdbId}, {title}, {year}, {jellyfinUserId}.
    /// </summary>
    public string WebhookMovieUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the webhook URL for TV show downloads.
    /// Supports placeholders: {tvdbId}, {tmdbId}, {imdbId}, {title}, {year}, {seasonNumber}, {isAnime}, {jellyfinUserId}.
    /// </summary>
    public string WebhookShowUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the HTTP method for webhook requests (GET or POST).
    /// </summary>
    public string WebhookMethod { get; set; } = "POST";

    /// <summary>
    /// Gets or sets the custom headers for webhook movie requests.
    /// </summary>
    public WebhookHeader[] WebhookMovieHeaders { get; set; } = Array.Empty<WebhookHeader>();

    /// <summary>
    /// Gets or sets the custom headers for webhook show requests.
    /// </summary>
    public WebhookHeader[] WebhookShowHeaders { get; set; } = Array.Empty<WebhookHeader>();

    /// <summary>
    /// Gets or sets the custom payload template for webhook movie requests (JSON format).
    /// Supports placeholders: {tmdbId}, {imdbId}, {title}, {year}, {jellyfinUserId}.
    /// </summary>
    public string WebhookMoviePayload { get; set; } = @"{
  ""tmdbId"": ""{tmdbId}"",
  ""imdbId"": ""{imdbId}"",
  ""title"": ""{title}"",
  ""year"": ""{year}"",
  ""jellyfinUserId"": ""{jellyfinUserId}""
}";

    /// <summary>
    /// Gets or sets the custom payload template for webhook show requests (JSON format).
    /// Supports placeholders: {tvdbId}, {tmdbId}, {imdbId}, {title}, {year}, {seasonNumber}, {isAnime}, {jellyfinUserId}.
    /// </summary>
    public string WebhookShowPayload { get; set; } = @"{
  ""tvdbId"": ""{tvdbId}"",
  ""tmdbId"": ""{tmdbId}"",
  ""imdbId"": ""{imdbId}"",
  ""title"": ""{title}"",
  ""year"": ""{year}"",
  ""seasonNumber"": {seasonNumber},
  ""isAnime"": {isAnime},
  ""jellyfinUserId"": ""{jellyfinUserId}""
}";

    /// <summary>
    /// Gets or sets the cache expiration interval in hours.
    /// </summary>
    public int CacheExpirationHours { get; set; } = 6;

    /// <summary>
    /// Gets or sets the delay in seconds before stopping playback of virtual items (default: 2 seconds).
    /// Some clients need time before playback can be stopped reliably.
    /// </summary>
    public int PlaybackStopDelaySeconds { get; set; } = 2;

    /// <summary>
    /// Gets or sets a value indicating whether to use short (2-second) dummy video for virtual items.
    /// When enabled, playback stops automatically after 2 seconds.
    /// When disabled, uses 1-hour dummy video (prevents "watched" status but requires manual stop on some clients).
    /// Default: true (enabled).
    /// </summary>
    public bool UseShortDummyVideo { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether trending movies feature is enabled.
    /// </summary>
    public bool TrendingMoviesEnabled { get; set; } = false;

    /// <summary>
    /// Gets or sets the user GUID from which to fetch trending movies.
    /// </summary>
    public Guid TrendingMoviesUserId { get; set; } = Guid.Empty;

    /// <summary>
    /// Gets or sets the limit for trending movies (1-100, default: 50).
    /// </summary>
    public int TrendingMoviesLimit { get; set; } = 50;

    /// <summary>
    /// Gets or sets a value indicating whether new-season email notifications are sent.
    /// </summary>
    public bool EmailNotificationsEnabled { get; set; } = false;

    /// <summary>
    /// Gets or sets the SMTP server host name.
    /// </summary>
    public string SmtpHost { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the SMTP server port.
    /// </summary>
    public int SmtpPort { get; set; } = 587;

    /// <summary>
    /// Gets or sets a value indicating whether the connection is upgraded to TLS with STARTTLS.
    /// </summary>
    /// <remarks>
    /// Implicit TLS (the SMTPS port 465) is not supported: JellyNext sends through the framework's
    /// <c>SmtpClient</c>, which only speaks STARTTLS, and pulling in a mail library is not an option
    /// because copying NuGet dependencies into the plugin output would also copy the
    /// <c>MediaBrowser.*</c> assemblies that must resolve from the host instead. Providers offering
    /// 465 practically always offer 587 as well.
    /// </remarks>
    public bool SmtpUseStartTls { get; set; } = true;

    /// <summary>
    /// Gets or sets the SMTP username. Empty sends without authentication.
    /// </summary>
    public string SmtpUsername { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the SMTP password.
    /// </summary>
    public string SmtpPassword { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the address notifications are sent from.
    /// </summary>
    public string SmtpFromAddress { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the display name notifications are sent from.
    /// </summary>
    public string SmtpFromName { get; set; } = "JellyNext";

    /// <summary>
    /// Gets or sets how many days after its premiere a season is still announced as new (1-365).
    /// </summary>
    /// <remarks>
    /// Independent of the per-user <c>NextSeasonsRecentDays</c> library filter: a user may want a
    /// wide library window but only be told about seasons that have genuinely just dropped.
    /// </remarks>
    public int NewSeasonNotificationWindowDays { get; set; } = 30;

    /// <summary>
    /// Gets or sets the OAuth application identity JellyNext presents to Trakt.
    /// Defaults to <see cref="TraktAuthMode.Standalone"/> so existing installations keep working.
    /// </summary>
    public TraktAuthMode TraktAuthMode { get; set; } = TraktAuthMode.Standalone;

    /// <summary>
    /// Gets or sets a value indicating whether JellyNext may refresh an expired token it borrowed
    /// from the official Trakt plugin.
    /// </summary>
    /// <remarks>
    /// Trakt refresh tokens are single use and rotate on every refresh, so only one owner may
    /// refresh. When enabled, JellyNext refreshes and immediately writes the rotated pair back into
    /// the official plugin's live configuration, keeping it the sole holder of valid tokens. When
    /// disabled, JellyNext skips the sync cycle and waits for the official plugin to refresh on its
    /// own. Only meaningful for <see cref="TraktAuthMode.SharedTraktPluginToken"/>.
    /// </remarks>
    public bool AllowSharedTokenRefresh { get; set; } = true;

    /// <summary>
    /// Gets or sets the array of per-user Trakt configurations.
    /// </summary>
    public TraktUser[] TraktUsers { get; set; } = Array.Empty<TraktUser>();

    /// <summary>
    /// Adds a new Trakt user configuration.
    /// </summary>
    /// <param name="userGuid">The Jellyfin user GUID.</param>
    public void AddUser(Guid userGuid)
    {
        var existingUser = TraktUsers.FirstOrDefault(u => u.LinkedMbUserId == userGuid);
        if (existingUser != null)
        {
            return;
        }

        var newUser = new TraktUser { LinkedMbUserId = userGuid };
        var userList = TraktUsers.ToList();
        userList.Add(newUser);
        TraktUsers = userList.ToArray();
    }

    /// <summary>
    /// Removes a Trakt user configuration.
    /// </summary>
    /// <param name="userGuid">The Jellyfin user GUID.</param>
    public void RemoveUser(Guid userGuid)
    {
        TraktUsers = TraktUsers.Where(u => u.LinkedMbUserId != userGuid).ToArray();
    }

    /// <summary>
    /// Gets all Trakt user configurations.
    /// </summary>
    /// <returns>A read-only list of Trakt users.</returns>
    public IReadOnlyList<TraktUser> GetAllTraktUsers()
    {
        return TraktUsers.ToList().AsReadOnly();
    }
}
