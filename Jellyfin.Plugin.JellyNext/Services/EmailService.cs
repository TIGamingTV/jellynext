using System;
using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyNext.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyNext.Services;

/// <summary>
/// Sends email through the SMTP server configured for the plugin.
/// </summary>
public class EmailService
{
    private const int SendTimeoutMilliseconds = 30000;

    private readonly ILogger<EmailService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="EmailService"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public EmailService(ILogger<EmailService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Checks whether enough SMTP settings are present to attempt a send.
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    /// <returns>True when a host, port and sender address are configured.</returns>
    public static bool IsConfigured(PluginConfiguration config)
    {
        return !string.IsNullOrWhiteSpace(config.SmtpHost)
               && config.SmtpPort > 0
               && !string.IsNullOrWhiteSpace(config.SmtpFromAddress);
    }

    /// <summary>
    /// Sends an email.
    /// </summary>
    /// <param name="toAddress">The recipient address.</param>
    /// <param name="subject">The subject line.</param>
    /// <param name="textBody">The plain text body.</param>
    /// <param name="htmlBody">The HTML body, sent as an alternative view.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <exception cref="InvalidOperationException">Thrown when SMTP is not configured.</exception>
    public async Task SendAsync(
        string toAddress,
        string subject,
        string textBody,
        string htmlBody,
        CancellationToken cancellationToken = default)
    {
        var config = Plugin.Instance?.Configuration
                     ?? throw new InvalidOperationException("Plugin configuration is not available.");

        if (!IsConfigured(config))
        {
            throw new InvalidOperationException(
                "SMTP is not configured. Set a server, port and sender address on the Notifications tab, "
                + "then save the configuration - sending uses the saved settings, not what is on screen.");
        }

        using var client = new SmtpClient(config.SmtpHost, config.SmtpPort)
        {
            DeliveryMethod = SmtpDeliveryMethod.Network,
            EnableSsl = config.SmtpUseStartTls,
            Timeout = SendTimeoutMilliseconds,
            UseDefaultCredentials = false
        };

        if (!string.IsNullOrWhiteSpace(config.SmtpUsername))
        {
            client.Credentials = new NetworkCredential(config.SmtpUsername, config.SmtpPassword);
        }

        // Encodings are set explicitly: left unset, the framework falls back to us-ascii and quietly
        // replaces every accented or non-Latin character in a show title with a question mark.
        using var message = new MailMessage
        {
            From = string.IsNullOrWhiteSpace(config.SmtpFromName)
                ? new MailAddress(config.SmtpFromAddress)
                : new MailAddress(config.SmtpFromAddress, config.SmtpFromName, Encoding.UTF8),
            Subject = subject,
            SubjectEncoding = Encoding.UTF8,
            Body = textBody,
            BodyEncoding = Encoding.UTF8,
            IsBodyHtml = false
        };

        message.To.Add(toAddress);
        message.AlternateViews.Add(
            AlternateView.CreateAlternateViewFromString(htmlBody, Encoding.UTF8, MediaTypeNames.Text.Html));

        _logger.LogDebug(
            "Sending mail to {Recipient} via {Host}:{Port} (STARTTLS: {StartTls})",
            toAddress,
            config.SmtpHost,
            config.SmtpPort,
            config.SmtpUseStartTls);

        await client.SendMailAsync(message, cancellationToken).ConfigureAwait(false);
    }
}
