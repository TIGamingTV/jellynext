using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyNext.Configuration;
using Jellyfin.Plugin.JellyNext.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.JellyNext;

/// <summary>
/// The main plugin class for JellyNext.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    /// <param name="applicationHost">Instance of the <see cref="IServerApplicationHost"/> interface.</param>
    public Plugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer,
        IServerApplicationHost applicationHost)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        ApplicationHost = applicationHost;
        PollingTasks = new ConcurrentDictionary<Guid, Task<bool>>();
    }

    /// <inheritdoc />
    public override string Name => "JellyNext";

    /// <inheritdoc />
    /// <remarks>
    /// Declared here rather than left to <see cref="BasePlugin.Description"/>'s empty default, which
    /// the Jellyfin 12 dashboard treats as absent and replaces with the *catalogue* entry's text
    /// (<c>usePluginDetails.ts</c>: <c>pluginInfo?.Description || packageInfo?.description</c>).
    /// A plugin that does not describe itself therefore shows whatever the repository says, which is
    /// how another plugin's description reached this one's card - see the GUID note on <see cref="Id"/>.
    /// </remarks>
    public override string Description =>
        "Trakt-powered discovery for Jellyfin. Creates per-user virtual libraries for personalized "
        + "Trakt recommendations and next seasons, with one-click downloads through Jellyseerr, "
        + "Radarr, Sonarr or a custom webhook.";

    /// <inheritdoc />
    /// <remarks>
    /// Must be unique across every plugin repository a server has enabled, not merely within this one.
    /// Jellyfin identifies plugins by this GUID alone - <c>InstallationManager.FilterPackages</c> matches
    /// on it and ignores the name, then merges same-GUID catalogue entries into one, keeping the
    /// name, description and image of whichever repository is ordered first. The previous value,
    /// <c>a4df60c5-6ab4-412a-8f79-2cab93fb2bc5</c>, was also used by <c>jellyfin-plugin-openlibrary</c>,
    /// which ships in the default repository; on Jellyfin 12 servers JellyNext consequently appeared
    /// under OpenLibrary's name and description while still opening JellyNext's settings page, since
    /// the configuration page is resolved from the loaded assembly rather than from the catalogue.
    /// Never reuse this GUID, and check a candidate against repo.jellyfin.org before changing it.
    /// </remarks>
    public override Guid Id => Guid.Parse("ce392429-21cb-43a1-b02d-be316b54bdf3");

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <summary>
    /// Gets the application host.
    /// </summary>
    public IServerApplicationHost ApplicationHost { get; }

    /// <summary>
    /// Gets the dictionary of active OAuth polling tasks keyed by user GUID.
    /// </summary>
    public ConcurrentDictionary<Guid, Task<bool>> PollingTasks { get; }

    /// <inheritdoc />
    /// <remarks>
    /// Takes the widget's script tag back out of the web client, which would otherwise be left
    /// pointing at an endpoint this plugin no longer serves.
    /// </remarks>
    public override void OnUninstalling()
    {
        WebScriptInjector.RemoveScriptTag(ApplicationPaths.WebPath);
        base.OnUninstalling();
    }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html"
            }
        };
    }
}
