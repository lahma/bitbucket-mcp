namespace Bitbucket.Mcp.Authentication;

/// <summary>
/// Runs the interactive half of the OAuth browser flow: open the authorization URL, wait on a
/// loopback callback, and come back with the authorization code.
/// </summary>
/// <remarks>
/// <para>
/// The split is deliberate. Everything that touches the token endpoint or the token cache stays in
/// <see cref="OAuthCredentialProvider"/>, so there is exactly one place where a single-use refresh
/// token can be spent or persisted. An implementation of this interface owns only the parts that
/// need a user and a machine with a browser: the listener, the <c>state</c> round-trip, and
/// launching the browser.
/// </para>
/// <para>
/// The caller bounds the wait with <c>BITBUCKET_MCP_AUTH_TIMEOUT_SECONDS</c> and its own
/// cancellation token, and passes the combination into
/// <see cref="AuthorizeAsync"/> — an implementation does not need to time itself out, it only has
/// to honour the token it is given.
/// </para>
/// </remarks>
internal interface IInteractiveAuthenticator
{
    /// <summary>
    /// Whether an interactive sign-in can be attempted at all in this process. Checked before the
    /// flow starts so that a headless server reports "no browser" rather than blocking for three
    /// minutes on a callback that will never arrive.
    /// </summary>
    /// <remarks>Must not perform I/O — it is consulted on the error path and by <c>status</c>.</remarks>
    bool IsAvailable { get; }

    /// <summary>
    /// Drives the browser flow to completion and returns the authorization code.
    /// </summary>
    /// <param name="consumerKey">
    /// The OAuth consumer key, which becomes <c>client_id</c> in the authorization URL. It is not a
    /// secret — it travels in a URL the user's browser opens.
    /// </param>
    /// <param name="cancellationToken">
    /// Already carries the configured authentication timeout. Cancellation means the sign-in did
    /// not complete in time.
    /// </param>
    /// <exception cref="AuthenticationRequiredException">
    /// The flow could not be started or did not complete — no browser, a <c>state</c> mismatch, or
    /// an error redirect from the authorization server.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> fired before the callback arrived.
    /// </exception>
    ValueTask<InteractiveAuthorizationResult> AuthorizeAsync(string consumerKey, CancellationToken cancellationToken);
}

/// <summary>
/// A completed authorization: the code Bitbucket handed to the loopback callback, and the redirect
/// URI it was handed to.
/// </summary>
/// <param name="Code">The single-use authorization code.</param>
/// <param name="RedirectUri">
/// The redirect URI exactly as sent in the authorization request. It has to be repeated verbatim in
/// the token exchange — the authorization server compares the two strings and rejects a mismatch,
/// including one as small as a trailing slash.
/// </param>
internal sealed record InteractiveAuthorizationResult(string Code, string RedirectUri);
