using System.Buffers.Text;
using System.Globalization;
using System.Net.Sockets;
using System.Security.Cryptography;

using Bitbucket.Mcp.Configuration;

using Microsoft.Extensions.Logging;

namespace Bitbucket.Mcp.Authentication;

/// <summary>
/// The real browser flow: build the authorization URL, put a loopback listener behind its redirect
/// URI, open a browser at it, and wait for Bitbucket to send the authorization code back.
/// </summary>
/// <remarks>
/// <para>
/// The order matters and is the whole reason this class exists rather than two calls at a call site:
/// the listener is bound <em>before</em> the browser is launched. A fast redirect landing on a port
/// nobody has bound yet is a connection-refused page and a wasted authorization code.
/// </para>
/// <para>
/// Nothing here exchanges or stores a token. This produces an authorization code and the redirect
/// URI it was issued against, and <see cref="OAuthCredentialProvider"/> does the rest — so there
/// stays exactly one place in the server where a single-use refresh token can be spent or persisted.
/// </para>
/// <para>
/// No PKCE (D13): Bitbucket Cloud's support for it is unconfirmed, and this is a confidential client
/// whose secret never leaves the machine. If that changes, the hook is small and local — a
/// <c>code_challenge</c> pair added to <see cref="BuildAuthorizeUrl"/> and carried to the token
/// exchange through <see cref="InteractiveAuthorizationResult"/> (risk R4).
/// </para>
/// </remarks>
internal sealed class InteractiveAuthenticator : IInteractiveAuthenticator
{
    /// <summary>Bits of entropy in the <c>state</c> parameter.</summary>
    private const int StateByteCount = 16;

    private readonly BitbucketMcpOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly Func<string, ILogger, bool> _openBrowser;

    /// <param name="options">Consumer key, callback host and port, and the no-browser flag.</param>
    /// <param name="loggerFactory">Source of loggers. Everything they write goes to stderr.</param>
    /// <param name="openBrowser">
    /// How to open a URL, defaulting to <see cref="BrowserLauncher.TryOpen"/>. A seam, so the flow
    /// can be driven end to end in a test without a desktop session.
    /// </param>
    internal InteractiveAuthenticator(
        BitbucketMcpOptions options,
        ILoggerFactory loggerFactory,
        Func<string, ILogger, bool>? openBrowser = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _options = options;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<InteractiveAuthenticator>();
        _openBrowser = openBrowser ?? BrowserLauncher.TryOpen;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Configuration only — no probing of the port, the display or a browser binary. It is consulted
    /// on the error path and by <c>status</c>, where a disk or network touch would be a surprise.
    /// </remarks>
    public bool IsAvailable =>
        !_options.NoBrowser
        && !string.IsNullOrEmpty(_options.OAuthKey)
        && !string.IsNullOrEmpty(_options.OAuthSecret);

    /// <inheritdoc />
    public async ValueTask<InteractiveAuthorizationResult> AuthorizeAsync(string consumerKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerKey);

        if (_options.NoBrowser)
        {
            throw new AuthenticationRequiredException(
                AuthenticationRequiredReason.BrowserUnavailable,
                message: "BITBUCKET_MCP_NO_BROWSER is set, so no interactive sign-in can be started. Run "
                    + "`bitbucket-mcp login` on a machine with a browser, or set BITBUCKET_ACCESS_TOKEN.");
        }

        // 128 bits from the CSPRNG. This is what ties the callback we accept to the request we made:
        // anything can connect to a loopback port, and the state value is the only thing that
        // distinguishes Bitbucket's redirect from a local process guessing at one.
        var state = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(StateByteCount));
        var redirectUri = BuildRedirectUri(_options);
        var authorizeUrl = BuildAuthorizeUrl(consumerKey, state, redirectUri);

        using var listener = new LoopbackCallbackListener(
            _options.OAuthCallbackPort,
            LoopbackCallbackListener.DefaultCallbackPath,
            _loggerFactory.CreateLogger<LoopbackCallbackListener>());

        try
        {
            // Bound first, browser second: a redirect that arrives before the bind is a
            // connection-refused page and a burnt authorization code.
            listener.Start();
        }
        catch (SocketException ex)
        {
            throw new AuthenticationRequiredException(
                AuthenticationRequiredReason.InteractiveFailed,
                authorizeUrl,
                $"Could not listen on {redirectUri} for the OAuth callback ({ex.Message}). Free the port, or set "
                    + "BITBUCKET_OAUTH_CALLBACK_PORT to another one and register the matching callback URL on the "
                    + "OAuth consumer.",
                ex);
        }

        // Both branches name the URL, and both go to stderr: the console logger is what writes
        // there, and nothing outside Cli/ may touch Console at all (AGENTS.md rule 3) because in
        // server mode a stray write to the wrong stream corrupts the protocol channel.
        if (_openBrowser(authorizeUrl, _logger))
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Waiting for the Bitbucket authorization callback on {RedirectUri}. If no browser appeared, open this URL to authorize: {AuthorizeUrl}", redirectUri, authorizeUrl);
            }
        }
        else
        {
            _logger.LogWarning("No browser could be opened. Open this URL to authorize: {AuthorizeUrl} (the callback is expected on {RedirectUri})", authorizeUrl, redirectUri);
        }

        var callback = await listener.WaitForCallbackAsync(state, cancellationToken).ConfigureAwait(false);

        if (callback.Error is { Length: > 0 } error)
        {
            var detail = string.IsNullOrWhiteSpace(callback.ErrorDescription)
                ? error
                : $"{error} ({callback.ErrorDescription})";

            throw new AuthenticationRequiredException(
                AuthenticationRequiredReason.InteractiveFailed,
                authorizeUrl,
                $"Bitbucket did not grant the authorization: {detail}.");
        }

        if (callback.Code is not { Length: > 0 } code)
        {
            throw new AuthenticationRequiredException(
                AuthenticationRequiredReason.InteractiveFailed,
                authorizeUrl,
                "The OAuth callback arrived without an authorization code.");
        }

        return new InteractiveAuthorizationResult(code, redirectUri);
    }

    /// <summary>
    /// The redirect URI, which must match the consumer's registered callback URL exactly — the
    /// authorization server compares the strings, and it is repeated verbatim in the token exchange.
    /// </summary>
    /// <remarks>
    /// Internal because <c>bitbucket-mcp status</c> prints it: the single most common first-run
    /// failure is a consumer registered with a callback URL that differs from this string.
    /// </remarks>
    internal static string BuildRedirectUri(BitbucketMcpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var host = options.OAuthCallbackHost;

        // An IPv6 literal has to be bracketed in a URL authority; `localhost` and `127.0.0.1` do not.
        if (host.Contains(':', StringComparison.Ordinal) && !host.StartsWith('['))
        {
            host = $"[{host}]";
        }

        var port = options.OAuthCallbackPort.ToString(CultureInfo.InvariantCulture);

        return $"http://{host}:{port}{LoopbackCallbackListener.DefaultCallbackPath}";
    }

    /// <summary>Builds the authorization URL the browser is sent to.</summary>
    private static string BuildAuthorizeUrl(string consumerKey, string state, string redirectUri) =>
        $"{OAuthTokenClient.AuthorizeEndpoint}"
        + $"?client_id={Uri.EscapeDataString(consumerKey)}"
        + "&response_type=code"
        + $"&state={Uri.EscapeDataString(state)}"
        + $"&redirect_uri={Uri.EscapeDataString(redirectUri)}";
}
