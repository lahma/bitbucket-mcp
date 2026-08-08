namespace Bitbucket.Mcp.Authentication;

/// <summary>Why a request could not be authenticated without the user doing something.</summary>
internal enum AuthenticationRequiredReason
{
    /// <summary>No credentials of any kind are configured.</summary>
    NotConfigured,

    /// <summary>OAuth is configured but there is no usable cached token set.</summary>
    NoCachedToken,

    /// <summary>The refresh token was rejected (single-use rotation lost, revoked, or expired).</summary>
    RefreshFailed,

    /// <summary>
    /// An interactive sign-in was needed but browser launching is disabled or failed.
    /// </summary>
    BrowserUnavailable,

    /// <summary>The browser sign-in was started but nobody completed it in time.</summary>
    InteractiveTimeout,

    /// <summary>
    /// The browser sign-in completed abnormally — state mismatch, an error from the authorization
    /// server, or a callback that never arrived.
    /// </summary>
    InteractiveFailed,
}

/// <summary>
/// Signals that the caller must authenticate before the request can proceed. Deliberately
/// data-bearing: the user-facing text (authorize URL, <c>bitbucket-mcp login</c>, the environment
/// variable escape hatch) is composed in one place by the tool-layer error funnel, so that every
/// tool reports the same thing.
/// </summary>
internal sealed class AuthenticationRequiredException : Exception
{
    /// <param name="reason">What went wrong, for the error funnel to switch on.</param>
    /// <param name="authorizeUrl">
    /// The URL the user should open to complete sign-in, when one exists — an interrupted browser
    /// flow has one, a missing environment variable does not.
    /// </param>
    /// <param name="message">
    /// Optional terse text for logs. The user-facing message is composed downstream; this only has
    /// to be intelligible in a stderr line.
    /// </param>
    /// <param name="innerException">The underlying failure, if any.</param>
    internal AuthenticationRequiredException(
        AuthenticationRequiredReason reason,
        string? authorizeUrl = null,
        string? message = null,
        Exception? innerException = null)
        : base(message ?? DefaultMessage(reason), innerException)
    {
        Reason = reason;
        AuthorizeUrl = authorizeUrl;
    }

    /// <summary>What went wrong.</summary>
    internal AuthenticationRequiredReason Reason { get; }

    /// <summary>
    /// The authorization URL to open, or <see langword="null"/> when no flow was started (for
    /// example because nothing is configured yet).
    /// </summary>
    internal string? AuthorizeUrl { get; }

    private static string DefaultMessage(AuthenticationRequiredReason reason) => reason switch
    {
        AuthenticationRequiredReason.NotConfigured => "No Bitbucket credentials are configured.",
        AuthenticationRequiredReason.NoCachedToken => "No cached OAuth token is available.",
        AuthenticationRequiredReason.RefreshFailed => "The cached OAuth refresh token was rejected.",
        AuthenticationRequiredReason.BrowserUnavailable => "An interactive sign-in is required but no browser could be opened.",
        AuthenticationRequiredReason.InteractiveTimeout => "The interactive sign-in timed out.",
        AuthenticationRequiredReason.InteractiveFailed => "The interactive sign-in did not complete.",
        _ => "Authentication is required.",
    };
}
