using System.IO;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyNext.Api;

/// <summary>
/// Serves the client side script that renders the New Seasons widget in the web interface.
/// </summary>
/// <remarks>
/// Anonymous by necessity - the script tag sits in the web client's <c>index.html</c> and is fetched
/// before anyone has signed in. The script itself contains no configuration; everything it displays
/// comes from authenticated calls it makes afterwards.
/// </remarks>
[ApiController]
[Route("JellyNext/ClientScript")]
public class ClientScriptController : ControllerBase
{
    /// <summary>
    /// Gets the widget script.
    /// </summary>
    /// <returns>The JavaScript served to the web client.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult GetClientScript()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("Jellyfin.Plugin.JellyNext.Web.jellynext-widget.js");

        if (stream == null)
        {
            return NotFound();
        }

        using var reader = new StreamReader(stream);
        return Content(reader.ReadToEnd(), "application/javascript");
    }
}
