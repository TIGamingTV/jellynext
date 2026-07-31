using System;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyNext.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyNext.Api;

/// <summary>
/// API controller for notification settings.
/// </summary>
[ApiController]
[Route("JellyNext/Notifications")]
[Produces("application/json")]
public class NotificationsController : ControllerBase
{
    private readonly ILogger<NotificationsController> _logger;
    private readonly EmailService _emailService;

    /// <summary>
    /// Initializes a new instance of the <see cref="NotificationsController"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="emailService">The email service.</param>
    public NotificationsController(ILogger<NotificationsController> logger, EmailService emailService)
    {
        _logger = logger;
        _emailService = emailService;
    }

    /// <summary>
    /// Sends a test email using the saved SMTP settings.
    /// </summary>
    /// <param name="request">The test email request.</param>
    /// <returns>Success status.</returns>
    [HttpPost("TestEmail")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<object>> SendTestEmail([FromBody] TestEmailRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.To))
        {
            return BadRequest(new { error = "A recipient address is required." });
        }

        try
        {
            await _emailService.SendAsync(
                request.To,
                "JellyNext test email",
                "This is a test email from JellyNext. Your SMTP settings work.",
                "<html><body style=\"font-family:Helvetica,Arial,sans-serif;\">"
                + "<p>This is a test email from JellyNext. Your SMTP settings work.</p></body></html>",
                HttpContext.RequestAborted);

            _logger.LogInformation("Sent a test email to {Recipient}", request.To);

            return Ok(new { success = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send a test email to {Recipient}", request.To);
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Request body for the test email endpoint.
    /// </summary>
    public class TestEmailRequest
    {
        /// <summary>
        /// Gets or sets the recipient address.
        /// </summary>
        public string To { get; set; } = string.Empty;
    }
}
