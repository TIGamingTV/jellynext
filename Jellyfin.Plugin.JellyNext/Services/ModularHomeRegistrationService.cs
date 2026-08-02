using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyNext.Services;

/// <summary>
/// Keeps JellyNext's New Seasons section registered with the Modular Home plugin.
/// </summary>
/// <remarks>
/// <para>
/// Registration has to be repeated rather than done once. Jellyfin does not order plugin startup, so
/// Modular Home may not be loaded yet when JellyNext starts; and Modular Home holds its registrations
/// only in memory, so a plugin reload or upgrade drops the section without telling anyone. A quiet
/// periodic re-register covers both, and is cheap - it is a dictionary write.
/// </para>
/// <para>
/// Nothing here is fatal. A server without Modular Home simply never gets a successful registration,
/// which is the correct outcome for an optional integration.
/// </para>
/// </remarks>
public class ModularHomeRegistrationService : IHostedService, IDisposable
{
    /// <summary>
    /// How long to wait before the first attempt, giving other plugins a chance to load.
    /// </summary>
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How often the registration is re-asserted.
    /// </summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    private readonly ILogger<ModularHomeRegistrationService> _logger;
    private readonly ModularHomeBridge _bridge;

    private Timer? _timer;
    private bool _registered;
    private bool _reportedUnavailable;

    /// <summary>
    /// Initializes a new instance of the <see cref="ModularHomeRegistrationService"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="bridge">The Modular Home bridge.</param>
    public ModularHomeRegistrationService(ILogger<ModularHomeRegistrationService> logger, ModularHomeBridge bridge)
    {
        _logger = logger;
        _bridge = bridge;
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer = new Timer(_ => Apply(), null, InitialDelay, Interval);

        if (Plugin.Instance != null)
        {
            Plugin.Instance.ConfigurationChanged += OnConfigurationChanged;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (Plugin.Instance != null)
        {
            Plugin.Instance.ConfigurationChanged -= OnConfigurationChanged;
        }

        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases the resources used by this service.
    /// </summary>
    /// <param name="disposing">Whether managed resources should be released.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    private void OnConfigurationChanged(object? sender, BasePluginConfiguration configuration)
    {
        // The section's heading comes from the widget title, so a configuration save can change what
        // was registered. Re-registering replaces the existing entry.
        _registered = false;
        Apply();
    }

    private void Apply()
    {
        try
        {
            if (Plugin.Instance?.Configuration.ModularHomeIntegrationEnabled != true)
            {
                // Modular Home offers no way to withdraw a section, so an already registered one stays
                // until the server restarts. It answers with nothing while the setting is off - see
                // ModularHomeSectionHandler.GetResults - so the row is empty rather than stale.
                _registered = false;
                return;
            }

            if (!_bridge.IsAvailable)
            {
                if (!_reportedUnavailable)
                {
                    _logger.LogInformation(
                        "The Modular Home integration is enabled but Modular Home is not loaded. "
                        + "JellyNext will keep checking in case it is installed later");
                    _reportedUnavailable = true;
                }

                _registered = false;
                return;
            }

            _reportedUnavailable = false;
            _registered = _bridge.TryRegisterSection(quiet: _registered);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The Modular Home registration check failed");
        }
    }
}
