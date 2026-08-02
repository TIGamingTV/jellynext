using System;

namespace Jellyfin.Plugin.JellyNext.Models.Common;

/// <summary>
/// What the Modular Home plugin passes to a section when it asks for its contents.
/// </summary>
/// <remarks>
/// Mirrors Modular Home's own <c>HomeScreenSectionPayload</c>. It deserializes its payload into
/// whatever type our handler method declares, using its own copy of Newtonsoft, so declaring the
/// shape here keeps JellyNext free of a JSON dependency and free of any reference to Modular Home's
/// assembly. The property names must match theirs exactly.
/// </remarks>
public class ModularHomeSectionPayload
{
    /// <summary>
    /// Gets or sets the Jellyfin user whose home screen is being built.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Gets or sets the free-form data supplied at registration time. Unused by JellyNext.
    /// </summary>
    public string? AdditionalData { get; set; }
}
