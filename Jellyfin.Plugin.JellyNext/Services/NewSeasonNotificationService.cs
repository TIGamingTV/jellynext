using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyNext.Helpers;
using Jellyfin.Plugin.JellyNext.Models.Common;
using Jellyfin.Plugin.JellyNext.Models.Trakt;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyNext.Services;

/// <summary>
/// Emails users when a new season of a show they watch is released.
/// </summary>
/// <remarks>
/// Runs off the Next Seasons content that has just been synced, so a season is announced under the
/// same conditions that put it in the user's library: it is the next season they have not watched,
/// it has aired, and it is not already in Jellyfin. Only seasons that are genuinely new - premiered
/// inside the notification window, or still airing - are announced, because progressing through an
/// old show also produces a "next season" and that is not something dropping.
/// </remarks>
public class NewSeasonNotificationService
{
    // Sent notifications are kept far longer than any release window so that a season which airs for
    // months is not announced a second time part-way through, while the list still stays bounded.
    private static readonly TimeSpan NotificationHistoryRetention = TimeSpan.FromDays(400);

    private readonly ILogger<NewSeasonNotificationService> _logger;
    private readonly ContentCacheService _contentCache;
    private readonly EmailService _emailService;
    private readonly IUserManager _userManager;

    // Users are synced in parallel, and recording a notification writes the shared plugin
    // configuration, so the read-modify-save has to be serialized.
    private readonly SemaphoreSlim _configLock = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="NewSeasonNotificationService"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="contentCache">The content cache holding freshly synced Next Seasons items.</param>
    /// <param name="emailService">The email service.</param>
    /// <param name="userManager">The Jellyfin user manager.</param>
    public NewSeasonNotificationService(
        ILogger<NewSeasonNotificationService> logger,
        ContentCacheService contentCache,
        EmailService emailService,
        IUserManager userManager)
    {
        _logger = logger;
        _contentCache = contentCache;
        _emailService = emailService;
        _userManager = userManager;
    }

    /// <summary>
    /// Sends a digest of newly released seasons to a user, if there is anything new to report.
    /// </summary>
    /// <param name="userId">The Jellyfin user ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task NotifyUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null || !config.EmailNotificationsEnabled)
        {
            return;
        }

        var traktUser = UserHelper.GetTraktUser(userId);
        if (traktUser == null || !traktUser.NotifyNewSeasonsByEmail)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(traktUser.NotificationEmail))
        {
            _logger.LogWarning(
                "New season notifications are enabled for user {UserId} but no email address is set",
                userId);
            return;
        }

        if (!EmailService.IsConfigured(config))
        {
            _logger.LogWarning(
                "New season notifications are enabled for user {UserId} but SMTP is not configured",
                userId);
            return;
        }

        var newSeasons = GetUnannouncedSeasons(userId, traktUser, config.NewSeasonNotificationWindowDays);
        if (newSeasons.Count == 0)
        {
            _logger.LogDebug("No newly released seasons to announce for user {UserId}", userId);
            return;
        }

        var userName = _userManager.GetUserById(userId)?.Username;

        try
        {
            await _emailService.SendAsync(
                traktUser.NotificationEmail,
                BuildSubject(newSeasons),
                BuildTextBody(newSeasons, userName),
                BuildHtmlBody(newSeasons, userName),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Nothing is recorded, so the same seasons are retried on the next sync.
            _logger.LogError(
                ex,
                "Failed to email {Count} new season(s) to {Recipient} for user {UserId}",
                newSeasons.Count,
                traktUser.NotificationEmail,
                userId);
            return;
        }

        await RecordNotifiedAsync(traktUser, newSeasons).ConfigureAwait(false);

        _logger.LogInformation(
            "Emailed {Count} newly released season(s) to {Recipient} for user {UserId}: {Seasons}",
            newSeasons.Count,
            traktUser.NotificationEmail,
            userId,
            string.Join(", ", newSeasons.Select(Describe)));
    }

    /// <summary>
    /// Picks the seasons that are new releases and have not been announced yet.
    /// </summary>
    private List<ContentItem> GetUnannouncedSeasons(Guid userId, TraktUser traktUser, int windowDays)
    {
        var alreadyNotified = traktUser.NotifiedSeasons
            .Select(n => (n.TvdbId, n.SeasonNumber))
            .ToHashSet();

        return _contentCache.GetCachedContent(userId, "nextseasons")
            .Where(item => item.TvdbId.HasValue && item.SeasonNumber.HasValue)
            .Where(item => SeasonReleaseHelper.IsRecentlyReleased(
                item.SeasonFirstAired,
                item.SeasonIsAiring,
                windowDays))
            .Where(item => !alreadyNotified.Contains((item.TvdbId!.Value, item.SeasonNumber!.Value)))
            .OrderByDescending(item => item.SeasonFirstAired ?? DateTime.MinValue)
            .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Records the announced seasons and drops entries that have aged out.
    /// </summary>
    private async Task RecordNotifiedAsync(TraktUser traktUser, IEnumerable<ContentItem> seasons)
    {
        await _configLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var cutoff = DateTime.UtcNow - NotificationHistoryRetention;
            var notified = traktUser.NotifiedSeasons
                .Where(n => n.NotifiedAt >= cutoff)
                .ToList();

            notified.AddRange(seasons.Select(item => new NotifiedSeason
            {
                TvdbId = item.TvdbId!.Value,
                SeasonNumber = item.SeasonNumber!.Value,
                NotifiedAt = DateTime.UtcNow
            }));

            traktUser.NotifiedSeasons = notified.ToArray();
            Plugin.Instance?.SaveConfiguration();
        }
        finally
        {
            _configLock.Release();
        }
    }

    private static string BuildSubject(IReadOnlyList<ContentItem> seasons)
    {
        if (seasons.Count == 1)
        {
            var item = seasons[0];
            return string.Create(
                CultureInfo.InvariantCulture,
                $"New season available: {item.Title} - Season {item.SeasonNumber}");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{seasons.Count} new seasons available");
    }

    private static string BuildTextBody(IReadOnlyList<ContentItem> seasons, string? userName)
    {
        var lines = new List<string>
        {
            string.IsNullOrEmpty(userName) ? "Hi," : $"Hi {userName},",
            string.Empty,
            seasons.Count == 1
                ? "A new season of a show you watch has been released:"
                : "New seasons of shows you watch have been released:",
            string.Empty
        };

        lines.AddRange(seasons.Select(item => "  - " + Describe(item)));
        lines.Add(string.Empty);
        lines.Add("Play the matching item in your JellyNext library to download it.");
        lines.Add("- JellyNext");

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildHtmlBody(IReadOnlyList<ContentItem> seasons, string? userName)
    {
        var rows = string.Concat(seasons.Select(item => string.Create(
            CultureInfo.InvariantCulture,
            $"<li style=\"margin-bottom:8px;\"><strong>{WebUtility.HtmlEncode(item.Title)}</strong>"
            + $"{WebUtility.HtmlEncode(FormatYear(item))} &ndash; Season {item.SeasonNumber}"
            + $"<br /><span style=\"color:#888;font-size:13px;\">{WebUtility.HtmlEncode(FormatRelease(item))}</span></li>")));

        var greeting = string.IsNullOrEmpty(userName) ? "Hi," : $"Hi {WebUtility.HtmlEncode(userName)},";
        var intro = seasons.Count == 1
            ? "A new season of a show you watch has been released:"
            : "New seasons of shows you watch have been released:";

        return "<html><body style=\"font-family:Helvetica,Arial,sans-serif;font-size:15px;color:#222;\">"
               + $"<p>{greeting}</p>"
               + $"<p>{intro}</p>"
               + $"<ul style=\"padding-left:20px;\">{rows}</ul>"
               + "<p style=\"color:#888;font-size:13px;\">Play the matching item in your JellyNext library to download it.</p>"
               + "<p style=\"color:#888;font-size:13px;\">&mdash; JellyNext</p>"
               + "</body></html>";
    }

    private static string Describe(ContentItem item)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{item.Title}{FormatYear(item)} - Season {item.SeasonNumber} ({FormatRelease(item)})");
    }

    private static string FormatYear(ContentItem item)
    {
        return item.Year.HasValue
            ? string.Create(CultureInfo.InvariantCulture, $" ({item.Year})")
            : string.Empty;
    }

    private static string FormatRelease(ContentItem item)
    {
        if (!item.SeasonFirstAired.HasValue)
        {
            return item.SeasonIsAiring ? "currently airing" : "release date unknown";
        }

        var premiered = "premiered " + item.SeasonFirstAired.Value.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);
        return item.SeasonIsAiring ? premiered + ", still airing" : premiered;
    }
}
