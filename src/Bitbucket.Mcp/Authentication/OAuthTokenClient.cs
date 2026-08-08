using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Bitbucket.Mcp.Configuration;
using Bitbucket.Mcp.Http;
using Bitbucket.Mcp.Http.Models;

using Microsoft.Extensions.Logging;

namespace Bitbucket.Mcp.Authentication;

/// <summary>
/// The only thing that talks to Bitbucket's OAuth 2.0 token endpoint: it exchanges an authorization
/// code for tokens, and it spends a refresh token for a new pair.
/// </summary>
/// <remarks>
/// <para>
/// This is a confidential client (D13): the consumer key and secret authenticate the request with
/// HTTP Basic, and no PKCE is sent because Bitbucket Cloud's support for it is unconfirmed. The
/// user creates their own consumer, so the secret is genuinely theirs and never leaves the machine.
/// </para>
/// <para>
/// It owns a small <see cref="HttpClient"/> of its own rather than sharing
/// <c>BitbucketApiClient</c>'s: that one is wired through
/// <c>AuthenticationHandler</c>, and running the token endpoint through the thing that asks for
/// tokens would be a cycle. The chain here is <see cref="RetryHandler"/> over a
/// <see cref="SocketsHttpHandler"/> — form bodies derive from <see cref="ByteArrayContent"/> and so
/// are re-sendable, which is what makes retrying a 429 or a 503 safe.
/// </para>
/// <para>
/// Redirects are <em>not</em> followed. The token endpoint has no reason to redirect, and a POST
/// carrying a client secret is not something to replay at a URL the response chose.
/// </para>
/// </remarks>
internal sealed class OAuthTokenClient : IDisposable
{
    /// <summary>Bitbucket Cloud's OAuth 2.0 token endpoint.</summary>
    internal static readonly Uri DefaultTokenEndpoint = new("https://bitbucket.org/site/oauth2/access_token");

    /// <summary>
    /// Bitbucket Cloud's OAuth 2.0 authorization endpoint — where the browser flow starts. Kept
    /// beside its token counterpart so the pair cannot drift apart.
    /// </summary>
    internal static readonly Uri AuthorizeEndpoint = new("https://bitbucket.org/site/oauth2/authorize");

    /// <summary>The RFC 6749 error code that means "this refresh token is spent, revoked or wrong".</summary>
    internal const string InvalidGrantError = "invalid_grant";

    /// <summary>
    /// Whole-request timeout. Short: a sign-in already blocks a tool call, and the token endpoint
    /// either answers quickly or is not going to.
    /// </summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Cap on the error body retained for diagnostics.</summary>
    private const int MaxErrorBodyLength = 4 * 1024;

    private readonly HttpClient _httpClient;
    private readonly Uri _tokenEndpoint;
    private readonly ILogger _logger;
    private readonly AuthenticationHeaderValue? _clientAuthentication;

    /// <summary>
    /// The production constructor: builds its own transport. Nothing here touches the network.
    /// </summary>
    internal OAuthTokenClient(BitbucketMcpOptions options, ILoggerFactory loggerFactory, TimeProvider? timeProvider = null)
        : this(options, loggerFactory, CreateTransport(), tokenEndpoint: null, timeProvider)
    {
    }

    /// <summary>The test constructor: takes the innermost handler and, optionally, a different endpoint.</summary>
    /// <param name="options">Supplies the consumer key and secret.</param>
    /// <param name="loggerFactory">Source of the logger and of the retry handler's logger.</param>
    /// <param name="transport">The innermost handler. Disposed with this client.</param>
    /// <param name="tokenEndpoint">Overrides <see cref="DefaultTokenEndpoint"/>.</param>
    /// <param name="timeProvider">Clock and delay source for <see cref="RetryHandler"/>.</param>
    internal OAuthTokenClient(
        BitbucketMcpOptions options,
        ILoggerFactory loggerFactory,
        HttpMessageHandler transport,
        Uri? tokenEndpoint = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(transport);

        _logger = loggerFactory.CreateLogger<OAuthTokenClient>();
        _tokenEndpoint = tokenEndpoint ?? DefaultTokenEndpoint;
        _clientAuthentication = BuildClientAuthentication(options.OAuthKey, options.OAuthSecret);

        var retry = new RetryHandler(loggerFactory.CreateLogger<RetryHandler>(), timeProvider)
        {
            InnerHandler = transport,
        };

        _httpClient = new HttpClient(retry, disposeHandler: true)
        {
            Timeout = RequestTimeout,
        };

        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        _ = _httpClient.DefaultRequestHeaders.UserAgent.TryParseAdd(
            $"{ServerVersion.Name}/{ServerVersion.Value} (+https://github.com/lahma/bitbucket-mcp)");
    }

    /// <summary>
    /// Whether a consumer key and secret are configured at all. <see langword="false"/> means every
    /// request here would fail with <c>invalid_client</c>, so callers report the missing
    /// configuration instead of asking.
    /// </summary>
    internal bool IsConfigured => _clientAuthentication is not null;

    /// <summary>
    /// Trades an authorization code from the loopback callback for an access and refresh token.
    /// </summary>
    /// <param name="code">The <c>code</c> query parameter Bitbucket sent to the callback.</param>
    /// <param name="redirectUri">
    /// The redirect URI used to obtain <paramref name="code"/>, byte for byte — the authorization
    /// server compares it and rejects the exchange if it differs.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <exception cref="AuthenticationRequiredException">No consumer key and secret are configured.</exception>
    /// <exception cref="HttpRequestException">The endpoint could not be reached.</exception>
    internal Task<OAuthTokenResult> ExchangeAuthorizationCodeAsync(
        string code,
        string redirectUri,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(redirectUri);

        return PostAsync(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
            },
            "authorization code exchange",
            cancellationToken);
    }

    /// <summary>
    /// Spends a refresh token for a new access token, and usually a new refresh token with it —
    /// Bitbucket's refresh tokens are single-use, which is what makes the caller's atomic
    /// persistence mandatory rather than tidy.
    /// </summary>
    /// <param name="refreshToken">The refresh token to spend.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <exception cref="AuthenticationRequiredException">No consumer key and secret are configured.</exception>
    /// <exception cref="HttpRequestException">The endpoint could not be reached.</exception>
    internal Task<OAuthTokenResult> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);

        return PostAsync(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
            },
            "token refresh",
            cancellationToken);
    }

    /// <inheritdoc />
    public void Dispose() => _httpClient.Dispose();

    private async Task<OAuthTokenResult> PostAsync(
        Dictionary<string, string> form,
        string operation,
        CancellationToken cancellationToken)
    {
        if (_clientAuthentication is null)
        {
            throw new AuthenticationRequiredException(
                AuthenticationRequiredReason.NotConfigured,
                message: "BITBUCKET_OAUTH_KEY and BITBUCKET_OAUTH_SECRET are not set, so the OAuth token endpoint cannot be used.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, _tokenEndpoint)
        {
            // FormUrlEncodedContent is a ByteArrayContent, so RetryHandler considers it
            // re-sendable and a 429 or 503 is retried rather than surfaced.
            Content = new FormUrlEncodedContent(form),
        };

        request.Headers.Authorization = _clientAuthentication;

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {
            return await ReadSuccessAsync(response, operation, cancellationToken).ConfigureAwait(false);
        }

        return await ReadFailureAsync(response, operation, cancellationToken).ConfigureAwait(false);
    }

    private async Task<OAuthTokenResult> ReadSuccessAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        OAuthTokenResponse? token;

        try
        {
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            await using (stream.ConfigureAwait(false))
            {
                token = await JsonSerializer
                    .DeserializeAsync(stream, BitbucketWireJsonContext.Default.OAuthTokenResponse, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "The OAuth {Operation} succeeded but the response was not valid JSON.", operation);

            return OAuthTokenResult.Failure(
                response.StatusCode,
                error: null,
                errorDescription: "the token endpoint returned a body that is not valid JSON");
        }

        if (token is null || string.IsNullOrEmpty(token.AccessToken))
        {
            return OAuthTokenResult.Failure(
                response.StatusCode,
                error: null,
                errorDescription: "the token endpoint returned no access token");
        }

        return OAuthTokenResult.Success(token);
    }

    private async Task<OAuthTokenResult> ReadFailureAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        var body = await ReadBoundedBodyAsync(response, cancellationToken).ConfigureAwait(false);

        OAuthErrorResponse? error = null;

        if (body.Length > 0)
        {
            try
            {
                error = JsonSerializer.Deserialize(body, BitbucketWireJsonContext.Default.OAuthErrorResponse);
            }
            catch (JsonException)
            {
                // Bitbucket occasionally answers the token endpoint with an HTML error page. The
                // status code and the collapsed body are then all there is to report.
            }
        }

        var result = OAuthTokenResult.Failure(
            response.StatusCode,
            error?.Error,
            error?.ErrorDescription ?? (error is null ? Collapse(body) : null));

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("The OAuth {Operation} failed: {Failure}.", operation, result.DescribeFailure());
        }

        return result;
    }

    private static async Task<string> ReadBoundedBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            await using (stream.ConfigureAwait(false))
            {
                var buffer = new byte[MaxErrorBodyLength];
                var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken)
                    .ConfigureAwait(false);

                return Encoding.UTF8.GetString(buffer.AsSpan(0, read));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return string.Empty;
        }
    }

    /// <summary>Squashes a body to a single short line so an HTML page does not become the message.</summary>
    private static string? Collapse(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        var collapsed = new StringBuilder(Math.Min(body.Length, 200));
        var lastWasSpace = false;

        foreach (var c in body)
        {
            var isSpace = char.IsWhiteSpace(c);

            if (isSpace && lastWasSpace)
            {
                continue;
            }

            collapsed.Append(isSpace ? ' ' : c);
            lastWasSpace = isSpace;

            if (collapsed.Length >= 200)
            {
                break;
            }
        }

        var text = collapsed.ToString().Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>
    /// Builds the HTTP Basic header once. The values go in as given: Bitbucket consumer keys and
    /// secrets are alphanumeric, so the form-encoding step RFC 6749 §2.3.1 describes is a no-op for
    /// every credential this can actually receive, and skipping it keeps the header identical to
    /// what <c>curl -u key:secret</c> would send.
    /// </summary>
    private static AuthenticationHeaderValue? BuildClientAuthentication(string? key, string? secret)
    {
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(secret))
        {
            return null;
        }

        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{key}:{secret}"));

        return new AuthenticationHeaderValue("Basic", credentials);
    }

    private static SocketsHttpHandler CreateTransport() => new()
    {
        // A POST carrying the client secret is not replayed at a URL the response picked.
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(15),
    };
}

/// <summary>
/// The outcome of one call to the token endpoint: either tokens, or an error the caller has to
/// branch on.
/// </summary>
/// <remarks>
/// A failed exchange is modelled as a value rather than an exception because exactly one failure —
/// <c>invalid_grant</c> — is an expected, routine part of the refresh state machine: it is how a
/// single-use refresh token reports that it has already been spent, and it drives the retry with
/// the previous token. Failures that are <em>not</em> answers from the server (DNS, TLS, a socket
/// dropping) stay exceptions, so that a network blip is never mistaken for a revoked grant and
/// never costs the user their cached tokens.
/// </remarks>
internal sealed class OAuthTokenResult
{
    private OAuthTokenResult(
        OAuthTokenResponse? token,
        HttpStatusCode statusCode,
        string? error,
        string? errorDescription)
    {
        Token = token;
        StatusCode = statusCode;
        Error = error;
        ErrorDescription = errorDescription;
    }

    /// <summary>The tokens, when the endpoint returned some.</summary>
    internal OAuthTokenResponse? Token { get; }

    /// <summary>The status the endpoint answered with. <c>200</c> on success.</summary>
    internal HttpStatusCode StatusCode { get; }

    /// <summary>The RFC 6749 error code, when the body carried one.</summary>
    internal string? Error { get; }

    /// <summary>Whatever elaboration was available: the server's description, or a body excerpt.</summary>
    internal string? ErrorDescription { get; }

    /// <summary>Whether the call produced usable tokens.</summary>
    internal bool IsSuccess => Token is not null;

    /// <summary>
    /// Whether the grant itself was rejected — a spent, revoked or expired refresh token, or a
    /// stale authorization code. The one failure the state machine can do something about.
    /// </summary>
    internal bool IsInvalidGrant =>
        string.Equals(Error, OAuthTokenClient.InvalidGrantError, StringComparison.Ordinal);

    internal static OAuthTokenResult Success(OAuthTokenResponse token) =>
        new(token, HttpStatusCode.OK, error: null, errorDescription: null);

    internal static OAuthTokenResult Failure(HttpStatusCode statusCode, string? error, string? errorDescription) =>
        new(token: null, statusCode, error, errorDescription);

    /// <summary>
    /// One line describing the failure, safe to log and to put in an exception message: it quotes
    /// the server's own error code and text, neither of which contains a credential.
    /// </summary>
    internal string DescribeFailure()
    {
        if (IsSuccess)
        {
            return "no failure";
        }

        var status = ((int) StatusCode).ToString(CultureInfo.InvariantCulture);

        return (Error, ErrorDescription) switch
        {
            (not null, not null) => $"HTTP {status} {Error} ({ErrorDescription})",
            (not null, null) => $"HTTP {status} {Error}",
            (null, not null) => $"HTTP {status} ({ErrorDescription})",
            _ => $"HTTP {status}",
        };
    }
}
