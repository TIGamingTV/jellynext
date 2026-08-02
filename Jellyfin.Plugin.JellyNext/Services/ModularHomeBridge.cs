using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyNext.Services;

/// <summary>
/// Registers JellyNext's New Seasons section with the Modular Home plugin.
/// </summary>
/// <remarks>
/// <para>
/// Modular Home (<c>IAmParadox27/jellyfin-plugin-home-sections</c>, published as "Home Screen
/// Sections") replaces the Jellyfin home screen wholesale, so the injected widget row cannot be
/// placed by its users. It exposes a static <c>PluginInterface.RegisterSection</c> for other plugins
/// to add a row of their own.
/// </para>
/// <para>
/// Everything here goes through reflection, for the same reason as
/// <see cref="TraktPluginBridge"/>: Jellyfin loads each plugin into its own
/// <see cref="AssemblyLoadContext"/>, so a direct reference would resolve to a different type
/// identity. A NuGet reference is doubly wrong - the published package is far behind the shipping
/// plugin, and adding any package reference would pull <c>CopyLocalLockFileAssemblies</c> in with it,
/// copying the <c>MediaBrowser.*</c> assemblies into the plugin output that must resolve from the
/// host instead.
/// </para>
/// </remarks>
public class ModularHomeBridge
{
    /// <summary>
    /// The section's identifier.
    /// </summary>
    /// <remarks>
    /// Modular Home uses this as its dictionary key, as the CSS class on the rendered row, and as the
    /// entry in every user's list of enabled sections. Changing it silently un-enables the section for
    /// everyone who had turned it on, so it is frozen.
    /// </remarks>
    public const string SectionId = "jellynext-new-seasons";

    private const string AssemblyNameFragment = ".HomeScreenSections";
    private const string PluginInterfaceTypeName = "Jellyfin.Plugin.HomeScreenSections.PluginInterface";
    private const string RegisterMethodName = "RegisterSection";
    private const string HandlerMethodName = nameof(ModularHomeSectionHandler.GetResults);

    private readonly ILogger<ModularHomeBridge> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ModularHomeBridge"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public ModularHomeBridge(ILogger<ModularHomeBridge> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets a value indicating whether the Modular Home plugin is loaded and exposes the registration
    /// entry point JellyNext needs.
    /// </summary>
    public bool IsAvailable => ResolveRegisterMethod() != null;

    /// <summary>
    /// Gets the version of the loaded Modular Home assembly, or null when it is not loaded.
    /// </summary>
    public string? PluginVersion => FindAssembly()?.GetName().Version?.ToString();

    /// <summary>
    /// Registers, or re-registers, the New Seasons section.
    /// </summary>
    /// <param name="quiet">Whether a success is only worth a debug line, as for a routine re-assert.</param>
    /// <returns>True when the section was handed to Modular Home.</returns>
    /// <remarks>
    /// Modular Home keeps registrations in a plain in-memory dictionary, so this is safe and
    /// necessary to repeat: re-registering replaces the previous entry, and a section registered
    /// before Modular Home reloaded is simply gone.
    /// </remarks>
    public bool TryRegisterSection(bool quiet = false)
    {
        var registerMethod = ResolveRegisterMethod();
        if (registerMethod == null)
        {
            return false;
        }

        try
        {
            var payload = BuildPayload(registerMethod);
            if (payload == null)
            {
                return false;
            }

            registerMethod.Invoke(null, new[] { payload });

            if (quiet)
            {
                _logger.LogDebug("Re-asserted the '{SectionId}' section with Modular Home", SectionId);
            }
            else
            {
                _logger.LogInformation(
                    "Registered the '{SectionId}' section with Modular Home. Each user still has to enable "
                    + "it in their own Modular Home settings before it appears",
                    SectionId);
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Could not register the New Seasons section with Modular Home. Its registration API "
                + "may have changed; the injected widget row is unaffected");
            return false;
        }
    }

    /// <summary>
    /// Builds the registration payload as Modular Home's own <c>JObject</c>.
    /// </summary>
    /// <param name="registerMethod">The resolved registration method.</param>
    /// <returns>The payload instance, or null when it could not be built.</returns>
    /// <remarks>
    /// The payload has to be *their* <c>JObject</c>, not one JellyNext constructed - across load
    /// contexts those are two unrelated types and the invoke would throw. Serializing to a string here
    /// and letting their <c>JObject.Parse</c> read it back sidesteps that, and means JellyNext needs
    /// no JSON dependency of its own.
    /// </remarks>
    private object? BuildPayload(MethodInfo registerMethod)
    {
        var payloadType = registerMethod.GetParameters().FirstOrDefault()?.ParameterType;
        var parse = payloadType?.GetMethod(
            "Parse",
            BindingFlags.Public | BindingFlags.Static,
            null,
            new[] { typeof(string) },
            null);

        if (parse == null)
        {
            _logger.LogWarning(
                "Modular Home's {Method} does not take a parsable payload. Its registration API has "
                + "changed in a way JellyNext does not understand",
                RegisterMethodName);
            return null;
        }

        var handlerType = typeof(ModularHomeSectionHandler);
        var title = Plugin.Instance?.Configuration.NextSeasonsWidgetTitle;

        // Keys are camel case because Modular Home's payload model is annotated that way.
        var json = JsonSerializer.Serialize(new
        {
            id = SectionId,
            displayText = string.IsNullOrWhiteSpace(title) ? "New Seasons" : title,
            limit = 1,
            resultsAssembly = handlerType.Assembly.FullName,
            resultsClass = handlerType.FullName,
            resultsMethod = HandlerMethodName
        });

        return parse.Invoke(null, new object[] { json });
    }

    private Assembly? FindAssembly()
    {
        try
        {
            return AssemblyLoadContext.All
                .SelectMany(context => context.Assemblies)
                .FirstOrDefault(assembly =>
                    assembly.FullName?.Contains(AssemblyNameFragment, StringComparison.OrdinalIgnoreCase) == true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not enumerate the loaded assemblies looking for Modular Home");
            return null;
        }
    }

    private MethodInfo? ResolveRegisterMethod()
    {
        try
        {
            var pluginInterface = FindAssembly()?.GetType(PluginInterfaceTypeName);
            return pluginInterface?.GetMethod(RegisterMethodName, BindingFlags.Public | BindingFlags.Static);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "Modular Home is loaded but {Type}.{Method} could not be resolved",
                PluginInterfaceTypeName,
                RegisterMethodName);
            return null;
        }
    }

    /// <summary>
    /// Describes the integration's state for the configuration page.
    /// </summary>
    /// <returns>A short human readable status line.</returns>
    public string DescribeStatus()
    {
        if (!IsAvailable)
        {
            return "Modular Home was not detected on this server.";
        }

        var version = PluginVersion;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"Modular Home detected{(version == null ? string.Empty : $" (version {version})")}.");
    }
}
