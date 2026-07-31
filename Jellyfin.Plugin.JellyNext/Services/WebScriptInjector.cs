using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyNext.Services;

/// <summary>
/// Adds and removes the widget's script tag in the Jellyfin web client's <c>index.html</c>.
/// </summary>
/// <remarks>
/// Jellyfin has no supported way for a plugin to add client side code to the web interface, so like
/// every other plugin that renders something outside the dashboard, JellyNext edits the served
/// <c>index.html</c>. The tag is rewritten on every start because a server upgrade replaces that file,
/// and removed again as soon as the widget is switched off, so disabling the feature leaves nothing
/// behind. Failures are never fatal: a read-only web root simply means the widget does not appear.
/// </remarks>
public class WebScriptInjector : IHostedService
{
    /// <summary>
    /// Marks the tag as ours so it can be found and replaced without disturbing other plugins' tags.
    /// </summary>
    private const string TagAttribute = "data-jellynext-widget";

    private static readonly Regex ExistingTagPattern = new(
        @"\s*<script[^>]*" + TagAttribute + @"[^>]*>\s*</script>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Startup and a configuration save can arrive at the same time; both read, edit and rewrite the
    // same file.
    private static readonly object FileLock = new();

    private readonly ILogger<WebScriptInjector> _logger;
    private readonly IApplicationPaths _applicationPaths;
    private readonly IServerConfigurationManager _serverConfigurationManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="WebScriptInjector"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="applicationPaths">The application paths.</param>
    /// <param name="serverConfigurationManager">The server configuration manager.</param>
    public WebScriptInjector(
        ILogger<WebScriptInjector> logger,
        IApplicationPaths applicationPaths,
        IServerConfigurationManager serverConfigurationManager)
    {
        _logger = logger;
        _applicationPaths = applicationPaths;
        _serverConfigurationManager = serverConfigurationManager;
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        Apply();

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

        return Task.CompletedTask;
    }

    private void OnConfigurationChanged(object? sender, BasePluginConfiguration configuration)
    {
        Apply();
    }

    private void Apply()
    {
        var enabled = Plugin.Instance?.Configuration.NextSeasonsWidgetEnabled == true;

        try
        {
            var indexFile = GetIndexFile();
            if (indexFile == null)
            {
                if (enabled)
                {
                    _logger.LogWarning(
                        "The New Seasons widget is enabled but the web client's index.html could not be found. "
                        + "This is expected when the server hosts no web content.");
                }

                return;
            }

            var scriptTag = enabled ? BuildScriptTag() : null;

            lock (FileLock)
            {
                var original = File.ReadAllText(indexFile);
                var updated = ApplyScriptTag(original, scriptTag);

                if (string.Equals(original, updated, StringComparison.Ordinal))
                {
                    return;
                }

                File.WriteAllText(indexFile, updated);
            }

            if (enabled)
            {
                _logger.LogInformation("Added the New Seasons widget script to {IndexFile}", indexFile);
            }
            else
            {
                _logger.LogInformation("Removed the New Seasons widget script from {IndexFile}", indexFile);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Could not update the web client's index.html. The New Seasons widget will not be shown. "
                + "Jellyfin needs write access to its web directory for this feature.");
        }
    }

    /// <summary>
    /// Removes the widget's script tag, if one is present.
    /// </summary>
    /// <param name="webPath">The web client directory.</param>
    /// <remarks>
    /// Called when the plugin is being uninstalled, at which point the hosted service no longer gets
    /// a chance to clean up and the tag would be left pointing at an endpoint that no longer exists.
    /// Best effort by design - an uninstall must not fail over this.
    /// </remarks>
    public static void RemoveScriptTag(string? webPath)
    {
        try
        {
            if (string.IsNullOrEmpty(webPath))
            {
                return;
            }

            var indexFile = Path.Combine(webPath, "index.html");
            if (!File.Exists(indexFile))
            {
                return;
            }

            lock (FileLock)
            {
                var original = File.ReadAllText(indexFile);
                var updated = ApplyScriptTag(original, null);

                if (!string.Equals(original, updated, StringComparison.Ordinal))
                {
                    File.WriteAllText(indexFile, updated);
                }
            }
        }
        catch (Exception)
        {
            // Nothing useful can be done during uninstall, and the logger is already gone.
        }
    }

    /// <summary>
    /// Removes any tag JellyNext previously wrote, then adds the given one back.
    /// </summary>
    /// <param name="html">The current contents of index.html.</param>
    /// <param name="scriptTag">The tag to write, or null to only remove.</param>
    /// <returns>The new contents.</returns>
    /// <remarks>
    /// Always strips first so an upgrade replaces the previous version's tag instead of stacking a
    /// second one, and so switching the widget off is a clean removal.
    /// </remarks>
    private static string ApplyScriptTag(string html, string? scriptTag)
    {
        var stripped = ExistingTagPattern.Replace(html, string.Empty);
        if (scriptTag == null)
        {
            return stripped;
        }

        var bodyEnd = stripped.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        if (bodyEnd < 0)
        {
            // No recognizable document end - appending still gets the script loaded.
            return stripped + "\n" + scriptTag;
        }

        return stripped.Insert(bodyEnd, scriptTag);
    }

    private string BuildScriptTag()
    {
        var version = Plugin.Instance?.Version.ToString() ?? "0";
        var baseUrl = string.Empty;

        try
        {
            baseUrl = _serverConfigurationManager.GetNetworkConfiguration().BaseUrl?.TrimEnd('/') ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read the configured base URL, assuming none");
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"    <script src=\"{baseUrl}/JellyNext/ClientScript?v={version}\" {TagAttribute} defer></script>\n");
    }

    private string? GetIndexFile()
    {
        var webPath = _applicationPaths.WebPath;
        if (string.IsNullOrEmpty(webPath))
        {
            return null;
        }

        var indexFile = Path.Combine(webPath, "index.html");
        return File.Exists(indexFile) ? indexFile : null;
    }
}
