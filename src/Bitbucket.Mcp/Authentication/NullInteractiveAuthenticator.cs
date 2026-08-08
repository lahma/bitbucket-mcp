namespace Bitbucket.Mcp.Authentication;

/// <summary>
/// The interactive authenticator used when there is nothing to be interactive with: it reports that
/// no sign-in is possible and, if asked anyway, says so in a form the tool-layer error funnel can
/// turn into instructions.
/// </summary>
/// <remarks>
/// It is the registered default so that the server builds and runs with no browser, no listener and
/// no OAuth consumer at all — a headless deployment using <c>BITBUCKET_ACCESS_TOKEN</c> must never
/// pay for machinery it will not use, and the MCP handshake must complete regardless. The real
/// implementation (loopback listener plus browser launcher) replaces this registration.
/// </remarks>
internal sealed class NullInteractiveAuthenticator : IInteractiveAuthenticator
{
    /// <summary>The shared instance. It has no state.</summary>
    internal static NullInteractiveAuthenticator Instance { get; } = new();

    /// <inheritdoc />
    public bool IsAvailable => false;

    /// <inheritdoc />
    public ValueTask<InteractiveAuthorizationResult> AuthorizeAsync(string consumerKey, CancellationToken cancellationToken)
    {
        throw new AuthenticationRequiredException(
            AuthenticationRequiredReason.BrowserUnavailable,
            message: "This process cannot start an interactive sign-in. Run `bitbucket-mcp login` in a terminal, "
                + "or set BITBUCKET_ACCESS_TOKEN (or BITBUCKET_EMAIL and BITBUCKET_API_TOKEN) in the server's environment.");
    }
}
