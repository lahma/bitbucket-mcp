using System.Net.Http.Headers;

namespace Bitbucket.Mcp.Authentication;

/// <summary>
/// Supplies the <c>Authorization</c> header for a Bitbucket request. Implementations are either
/// static (a bearer token or Basic <c>email:api-token</c> straight from the environment) or the
/// OAuth browser flow with its cached, rotating token set.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here runs at startup: the factory that picks an implementation performs environment
/// reads only, and the first network, disk or browser interaction happens inside
/// <see cref="GetAuthenticationHeaderAsync"/> — that is, inside a tool call that can report a
/// useful error. A server that cannot authenticate must still complete the MCP handshake.
/// </para>
/// <para>
/// The returned header is applied per request, never on
/// <see cref="HttpClient.DefaultRequestHeaders"/>. <c>SocketsHttpHandler</c> strips a per-request
/// <c>Authorization</c> header on <em>every</em> automatic redirect and does not re-apply a default
/// one, so the HTTP pipeline turns automatic redirects off entirely and re-attaches this header per
/// hop — and only for <c>https://api.bitbucket.org</c> (D16). Keeping the header off the client
/// means nothing can leak by default if that ever changes.
/// </para>
/// </remarks>
internal interface ICredentialProvider
{
    /// <summary>
    /// Returns the header to attach to the next request, refreshing or acquiring credentials if
    /// necessary.
    /// </summary>
    /// <exception cref="AuthenticationRequiredException">
    /// Credentials are missing, expired beyond repair, or need an interactive sign-in that could
    /// not be performed.
    /// </exception>
    ValueTask<AuthenticationHeaderValue> GetAuthenticationHeaderAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Discards whatever credential was last handed out, after the server rejected it with a 401.
    /// The next <see cref="GetAuthenticationHeaderAsync"/> call must re-acquire rather than reuse.
    /// </summary>
    ValueTask InvalidateAsync(CancellationToken cancellationToken);

    /// <summary>
    /// A short, human-readable description of the configured credential for logs and the
    /// <c>status</c> CLI command — for example <c>"OAuth (consumer ABCD…, expires 12:04Z)"</c>.
    /// </summary>
    /// <remarks>
    /// Implementations must never return a token, secret or password, in whole or in a truncated
    /// form that still narrows a search. This string is printed to stdout by the CLI and written
    /// to logs that users paste into issue reports.
    /// </remarks>
    string Describe();
}
