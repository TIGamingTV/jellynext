namespace Jellyfin.Plugin.JellyNext.Configuration;

/// <summary>
/// Determines which OAuth application identity and token JellyNext presents to Trakt.
/// </summary>
/// <remarks>
/// Trakt's free tier allows a single connected community app per account, counted by distinct
/// OAuth client id. A free account therefore cannot run JellyNext and the official Jellyfin Trakt
/// plugin side by side under their own client ids. The non-default modes make JellyNext present
/// itself as the official Trakt plugin's app so both plugins occupy one connection slot.
/// </remarks>
public enum TraktAuthMode
{
    /// <summary>
    /// JellyNext uses its own OAuth application and its own per-user tokens. Requires a free
    /// Trakt app slot of its own (or a Trakt VIP account).
    /// </summary>
    Standalone = 0,

    /// <summary>
    /// JellyNext presents the official Trakt plugin's client id and borrows that plugin's stored
    /// per-user access token. No second authorization takes place, so Trakt only ever sees one
    /// app connection. Requires the official Trakt plugin to be installed and linked.
    /// </summary>
    SharedTraktPluginToken = 1,

    /// <summary>
    /// JellyNext presents the official Trakt plugin's client id but performs its own device
    /// authorization and owns its own token. Trakt sees one app with two independent tokens.
    /// Experimental: it is unverified whether Trakt keeps the official plugin's earlier token
    /// valid when the same client id is authorized a second time.
    /// </summary>
    SharedClientId = 2
}
