using System;

namespace Jellyfin.Plugin.JellyNext.Services;

/// <summary>
/// Thrown when a Trakt call cannot be authenticated for a user.
/// </summary>
/// <remarks>
/// Callers should treat this as "skip this sync cycle and retry later" rather than "this user has no
/// content". In particular, cached content must be left untouched: replacing it with an empty result
/// would tear down the user's virtual library over a transient token problem.
/// </remarks>
public class TraktAuthenticationException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TraktAuthenticationException"/> class.
    /// </summary>
    public TraktAuthenticationException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TraktAuthenticationException"/> class.
    /// </summary>
    /// <param name="message">The error message.</param>
    public TraktAuthenticationException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TraktAuthenticationException"/> class.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The inner exception.</param>
    public TraktAuthenticationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
