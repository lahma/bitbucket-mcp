using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

using Bitbucket.Mcp.Configuration;
using Bitbucket.Mcp.Http.Models;

using Microsoft.Extensions.Logging;

namespace Bitbucket.Mcp.Authentication;

/// <summary>
/// The OAuth 2.0 browser-flow credential: an in-memory access token backed by a persisted,
/// rotating refresh token, and a sign-in as the last resort.
/// </summary>
/// <remarks>
/// <para>
/// Bitbucket's refresh tokens are <b>single-use</b>. A successful refresh returns a replacement and
/// invalidates the one just spent, which makes this the highest-risk logic in the server: lose a
/// rotation and the user is silently signed out; spend the same token twice — two processes, or two
/// concurrent tool calls — and one of them destroys a working grant. Everything below exists to
/// make those two failures impossible or recoverable, in the order the plan lays out:
/// </para>
/// <list type="number">
/// <item><description>a valid in-memory access token short-circuits everything (with a 60 s skew, D14);</description></item>
/// <item><description>a <see cref="SemaphoreSlim"/> collapses concurrent callers in this process onto one refresh;</description></item>
/// <item><description>a cross-process file lock does the same across processes, and failing to get it only warns;</description></item>
/// <item><description>the cache is re-read under the lock, because the process we waited for may already have rotated;</description></item>
/// <item><description>the refresh persists <c>{refreshToken = new ?? old, previousRefreshToken = old}</c> atomically
/// <em>before</em> the access token is handed out, and an <c>invalid_grant</c> is retried once with the previous
/// token — which is what recovers a rotation lost to a crash or to a racing process;</description></item>
/// <item><description>only when both refresh tokens are dead does the cache get deleted and an interactive
/// sign-in start, bounded by <c>BITBUCKET_MCP_AUTH_TIMEOUT_SECONDS</c> and the tool call's own token;</description></item>
/// <item><description>and if that cannot happen, an <see cref="AuthenticationRequiredException"/> says why.</description></item>
/// </list>
/// <para>
/// The constructor performs no I/O whatsoever — no disk, no network, no browser. Resolving this
/// service must stay free, because the MCP handshake has to complete on a machine that has never
/// been signed in.
/// </para>
/// </remarks>
internal sealed class OAuthCredentialProvider : ICredentialProvider, IDisposable
{
    /// <summary>
    /// Assumed access-token lifetime when the token endpoint omits <c>expires_in</c>. This is not
    /// the documented token lifetime — D14 forbids hard-coding that, and nothing here does. It is a
    /// deliberately short "we were not told, so check again soon" window.
    /// </summary>
    private static readonly TimeSpan UnknownLifetimeFallback = TimeSpan.FromMinutes(5);

    private readonly BitbucketMcpOptions _options;
    private readonly TokenStore _tokenStore;
    private readonly OAuthTokenClient _tokenClient;
    private readonly IInteractiveAuthenticator _interactiveAuthenticator;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    /// <summary>Collapses concurrent callers in this process onto a single refresh (step 2).</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>SHA-256 of the configured consumer key, or <see langword="null"/> if there is none.</summary>
    private readonly string? _consumerKeyFingerprint;

    /// <summary>The current token set, or <see langword="null"/> if none has been loaded yet.</summary>
    private TokenSet? _cached;

    /// <summary>
    /// An access token the server answered 401 for. Remembered so that re-reading the cache cannot
    /// hand the same rejected token straight back — without this, "invalidate" would only be a
    /// suggestion whenever the rejected token is also the one on disk.
    /// </summary>
    private volatile string? _rejectedAccessToken;

    /// <param name="options">Consumer credentials, browser policy and the interactive timeout.</param>
    /// <param name="tokenStore">The token cache. Only ever read and written from inside the gate.</param>
    /// <param name="tokenClient">The token endpoint. The only place a refresh token is spent.</param>
    /// <param name="interactiveAuthenticator">
    /// The browser flow. The null implementation is registered by default, so a headless server
    /// reports "no browser" instead of hanging.
    /// </param>
    /// <param name="timeProvider">Clock for expiry, skew and the interactive timeout.</param>
    /// <param name="loggerFactory">Source of the logger. Everything it writes goes to stderr.</param>
    internal OAuthCredentialProvider(
        BitbucketMcpOptions options,
        TokenStore tokenStore,
        OAuthTokenClient tokenClient,
        IInteractiveAuthenticator interactiveAuthenticator,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(tokenStore);
        ArgumentNullException.ThrowIfNull(tokenClient);
        ArgumentNullException.ThrowIfNull(interactiveAuthenticator);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _options = options;
        _tokenStore = tokenStore;
        _tokenClient = tokenClient;
        _interactiveAuthenticator = interactiveAuthenticator;
        _timeProvider = timeProvider;
        _logger = loggerFactory.CreateLogger<OAuthCredentialProvider>();

        // Hashing a string is not I/O; the constructor stays free.
        _consumerKeyFingerprint = ComputeFingerprint(options.OAuthKey);
    }

    /// <summary>Whether both halves of the OAuth consumer credential are present.</summary>
    internal bool IsConfigured =>
        _consumerKeyFingerprint is not null && !string.IsNullOrEmpty(_options.OAuthSecret);

    /// <summary>SHA-256, lowercase hex, of an OAuth consumer key — the value stamped into the cache.</summary>
    /// <remarks>
    /// The key itself is never written to disk. It is not a secret (it travels in the authorization
    /// URL), but a fingerprint answers the only question the cache has to ask — "were you obtained
    /// with the key that is configured now?" — without storing anything.
    /// </remarks>
    internal static string? ComputeFingerprint(string? consumerKey) =>
        string.IsNullOrEmpty(consumerKey)
            ? null
            : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(consumerKey)));

    /// <inheritdoc />
    public async ValueTask<AuthenticationHeaderValue> GetAuthenticationHeaderAsync(CancellationToken cancellationToken)
    {
        // (1) The overwhelmingly common path: a token already in memory with time left on it.
        if (Volatile.Read(ref _cached) is { } cached && IsUsable(cached))
        {
            return Bearer(cached);
        }

        // (2) One refresh per process, however many tool calls are in flight.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // Whoever held the gate may have just refreshed on our behalf.
            if (_cached is { } refreshedByAnother && IsUsable(refreshedByAnother))
            {
                return Bearer(refreshedByAnother);
            }

            var tokens = await AcquireAsync(forceInteractive: false, cancellationToken).ConfigureAwait(false);

            return Bearer(tokens);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Drops the in-memory token and remembers it as rejected, so the next call re-reads the cache
    /// and refreshes rather than handing back the credential the server has just refused.
    /// </remarks>
    public ValueTask InvalidateAsync(CancellationToken cancellationToken)
    {
        var dropped = Interlocked.Exchange(ref _cached, null);

        if (dropped?.AccessToken is { Length: > 0 } rejected)
        {
            _rejectedAccessToken = rejected;
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Reports in-memory state only — it is called from logging and from <c>status</c>, and neither
    /// should trigger disk access as a side effect of being described.
    /// </remarks>
    public string Describe()
    {
        if (!IsConfigured)
        {
            return "OAuth (not configured — set BITBUCKET_OAUTH_KEY and BITBUCKET_OAUTH_SECRET)";
        }

        // A prefix of the fingerprint, never the key: enough to tell two consumers apart in a log.
        var consumer = _consumerKeyFingerprint!.AsSpan(0, 12).ToString();
        var cached = Volatile.Read(ref _cached);

        if (cached is null)
        {
            return $"OAuth (consumer {consumer}…, no access token in memory)";
        }

        var expiry = cached.ExpiresAtUtc.ToString("u", CultureInfo.InvariantCulture);

        return IsUsable(cached)
            ? $"OAuth (consumer {consumer}…, access token valid until {expiry})"
            : $"OAuth (consumer {consumer}…, access token expired at {expiry})";
    }

    /// <summary>
    /// Runs the interactive sign-in unconditionally and persists the result — what
    /// <c>bitbucket-mcp login</c> does. Held tokens are replaced.
    /// </summary>
    /// <exception cref="AuthenticationRequiredException">
    /// OAuth is not configured, or the sign-in could not be started or completed.
    /// </exception>
    internal async Task<TokenSet> SignInAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await AcquireAsync(forceInteractive: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    /// <summary>
    /// Steps 3 to 7, always under <see cref="_gate"/>: cross-process lock, re-read, refresh,
    /// interactive sign-in, or an actionable failure.
    /// </summary>
    private async Task<TokenSet> AcquireAsync(bool forceInteractive, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            throw new AuthenticationRequiredException(
                AuthenticationRequiredReason.NotConfigured,
                message: "No Bitbucket credentials are configured. Set BITBUCKET_ACCESS_TOKEN, or BITBUCKET_EMAIL "
                    + "and BITBUCKET_API_TOKEN, or set BITBUCKET_OAUTH_KEY and BITBUCKET_OAUTH_SECRET and run "
                    + "`bitbucket-mcp login`.");
        }

        // (3) Cross-process exclusion. Null means it could not be taken in ten seconds, which is a
        // warning rather than a failure: the previous-refresh-token fallback exists precisely to
        // recover the one wasted rotation this can cause.
        var fileLock = await _tokenStore.AcquireLockAsync(cancellationToken).ConfigureAwait(false);

        if (fileLock is null)
        {
            _logger.LogWarning(
                "Proceeding without the cross-process token lock at {Path}; a concurrent sign-in may cost one refresh token.",
                _tokenStore.LockFilePath);
        }

        try
        {
            // (4) Re-read under the lock. Another process may have rotated while we queued.
            var stored = await LoadForCurrentConsumerAsync(cancellationToken).ConfigureAwait(false);

            if (!forceInteractive)
            {
                if (stored is not null && IsUsable(stored))
                {
                    Volatile.Write(ref _cached, stored);
                    return stored;
                }

                // (5) Refresh, with the single-use rotation persisted before anything is returned.
                if (stored?.RefreshToken is { Length: > 0 })
                {
                    var refreshed = await TryRefreshAsync(stored, cancellationToken).ConfigureAwait(false);

                    if (refreshed is not null)
                    {
                        return refreshed;
                    }
                }
            }

            // (6) and (7).
            return await AuthenticateInteractivelyAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (fileLock is not null)
            {
                await fileLock.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Reads the cache, discarding a token set that belongs to a different OAuth consumer — a
    /// fingerprint mismatch means the configured key changed, and those tokens can only produce
    /// confusing 401s.
    /// </summary>
    private async Task<TokenSet?> LoadForCurrentConsumerAsync(CancellationToken cancellationToken)
    {
        var stored = await _tokenStore.LoadAsync(cancellationToken).ConfigureAwait(false);

        if (stored is null)
        {
            return null;
        }

        if (!string.Equals(stored.ConsumerKeyFingerprint, _consumerKeyFingerprint, StringComparison.Ordinal))
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "The cached tokens at {Path} were obtained with a different OAuth consumer key; ignoring them.",
                    _tokenStore.FilePath);
            }

            return null;
        }

        return stored;
    }

    /// <summary>
    /// Step 5. Returns the new token set, or <see langword="null"/> when the grant is gone and the
    /// caller should sign in again.
    /// </summary>
    /// <exception cref="AuthenticationRequiredException">
    /// The endpoint refused for a reason a browser cannot fix (a wrong consumer secret, say). The
    /// cache is left intact in that case — the refresh token is probably still perfectly good.
    /// </exception>
    private async Task<TokenSet?> TryRefreshAsync(TokenSet stored, CancellationToken cancellationToken)
    {
        var current = stored.RefreshToken!;

        var result = await _tokenClient.RefreshAsync(current, cancellationToken).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            return await PersistAsync(result.Token!, current, cancellationToken).ConfigureAwait(false);
        }

        if (!result.IsInvalidGrant)
        {
            throw new AuthenticationRequiredException(
                AuthenticationRequiredReason.RefreshFailed,
                message: $"Bitbucket refused to refresh the access token: {result.DescribeFailure()}.");
        }

        var previous = stored.PreviousRefreshToken;

        // The one retry that recovers a rotation whose result never reached disk — a crash between
        // the token endpoint answering and the file being written, or a racing process that rotated
        // while we were reading. Pointless when the two tokens are the same value.
        if (!string.IsNullOrEmpty(previous) && !string.Equals(previous, current, StringComparison.Ordinal))
        {
            _logger.LogInformation("The cached refresh token was rejected; retrying once with the previous one.");

            var retry = await _tokenClient.RefreshAsync(previous, cancellationToken).ConfigureAwait(false);

            if (retry.IsSuccess)
            {
                return await PersistAsync(retry.Token!, previous, cancellationToken).ConfigureAwait(false);
            }

            if (!retry.IsInvalidGrant)
            {
                throw new AuthenticationRequiredException(
                    AuthenticationRequiredReason.RefreshFailed,
                    message: $"Bitbucket refused to refresh the access token: {retry.DescribeFailure()}.");
            }
        }

        // Both are dead: the grant has been revoked or has expired outright. Delete the cache so
        // nothing tries them again, and fall through to a sign-in.
        _logger.LogInformation("The cached OAuth grant is no longer valid; discarding the token cache.");

        _ = await _tokenStore.DeleteAsync(cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _cached, null);

        return null;
    }

    /// <summary>Step 6 — the browser flow — and step 7, the failure that explains itself.</summary>
    private async Task<TokenSet> AuthenticateInteractivelyAsync(CancellationToken cancellationToken)
    {
        if (_options.NoBrowser)
        {
            throw new AuthenticationRequiredException(
                AuthenticationRequiredReason.BrowserUnavailable,
                message: "An interactive sign-in is required but BITBUCKET_MCP_NO_BROWSER is set. Run "
                    + "`bitbucket-mcp login` on a machine with a browser, or set BITBUCKET_ACCESS_TOKEN.");
        }

        if (!_interactiveAuthenticator.IsAvailable)
        {
            throw new AuthenticationRequiredException(
                AuthenticationRequiredReason.BrowserUnavailable,
                message: "An interactive sign-in is required but this process cannot start one. Run "
                    + "`bitbucket-mcp login` in a terminal, or set BITBUCKET_ACCESS_TOKEN.");
        }

        // Both bounds apply: the configured wait, and the tool call's own cancellation. A sign-in
        // that nobody is going to complete must not pin the tool call open indefinitely.
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(_options.AuthTimeoutSeconds),
            _timeProvider);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        InteractiveAuthorizationResult authorization;

        try
        {
            authorization = await _interactiveAuthenticator
                .AuthorizeAsync(_options.OAuthKey!, linked.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new AuthenticationRequiredException(
                AuthenticationRequiredReason.InteractiveTimeout,
                message: $"The browser sign-in was not completed within {_options.AuthTimeoutSeconds.ToString(CultureInfo.InvariantCulture)} seconds "
                    + "(BITBUCKET_MCP_AUTH_TIMEOUT_SECONDS).");
        }

        // Deliberately on the caller's token, not the interactive one: the browser wait is over,
        // and dropping a fresh authorization code because the sign-in budget just ran out would
        // waste the whole flow.
        var result = await _tokenClient
            .ExchangeAuthorizationCodeAsync(authorization.Code, authorization.RedirectUri, cancellationToken)
            .ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            throw new AuthenticationRequiredException(
                AuthenticationRequiredReason.InteractiveFailed,
                message: $"Bitbucket rejected the authorization code: {result.DescribeFailure()}.");
        }

        return await PersistAsync(result.Token!, spentRefreshToken: null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Turns a token-endpoint response into a <see cref="TokenSet"/>, writes it, and only then
    /// publishes it in memory.
    /// </summary>
    /// <param name="response">The endpoint's answer. Its access token is already known non-empty.</param>
    /// <param name="spentRefreshToken">
    /// The refresh token this exchange consumed, or <see langword="null"/> for a fresh sign-in. It
    /// becomes <see cref="TokenSet.PreviousRefreshToken"/>, and it stays the current refresh token
    /// if the response did not include a replacement.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    private async Task<TokenSet> PersistAsync(
        OAuthTokenResponse response,
        string? spentRefreshToken,
        CancellationToken cancellationToken)
    {
        var tokens = new TokenSet
        {
            Version = TokenSet.CurrentVersion,
            ConsumerKeyFingerprint = _consumerKeyFingerprint,
            AccessToken = response.AccessToken,
            ExpiresAtUtc = _timeProvider.GetUtcNow() + ResolveLifetime(response.ExpiresIn),
            RefreshToken = response.RefreshToken ?? spentRefreshToken,
            PreviousRefreshToken = spentRefreshToken,
            Scopes = response.GrantedScopes,
        };

        try
        {
            // Before the token is handed out, not after: a crash in between then costs an access
            // token, which is replaceable, instead of the refresh chain, which is not.
            await _tokenStore.SaveAsync(tokens, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The rotation already happened at Bitbucket's end, so the token in hand is the only
            // copy of the grant that exists. Refusing to use it would strand the user for the rest
            // of the session on top of an unwritable file; using it costs a re-login after restart.
            _logger.LogError(
                ex,
                "Could not write the token cache at {Path}. Authentication will work until this process exits, "
                    + "after which a new sign-in will be required.",
                _tokenStore.FilePath);
        }

        _rejectedAccessToken = null;
        Volatile.Write(ref _cached, tokens);

        return tokens;
    }

    /// <summary>
    /// The lifetime to record, taken from <c>expires_in</c> (D14) or, if the endpoint omitted it,
    /// a short window that makes the next call check again rather than trust a guess.
    /// </summary>
    private TimeSpan ResolveLifetime(int? expiresIn)
    {
        if (expiresIn is > 0)
        {
            return TimeSpan.FromSeconds(expiresIn.GetValueOrDefault());
        }

        _logger.LogWarning(
            "The OAuth token endpoint did not report expires_in; treating the access token as valid for {Fallback} only.",
            UnknownLifetimeFallback);

        return UnknownLifetimeFallback;
    }

    /// <summary>
    /// Whether a token set can be handed out: unexpired by <see cref="TokenSet.ExpirySkew"/>, and
    /// not the one the API has just answered 401 for.
    /// </summary>
    private bool IsUsable(TokenSet tokens) =>
        tokens.IsAccessTokenValid(_timeProvider)
        && !string.Equals(tokens.AccessToken, _rejectedAccessToken, StringComparison.Ordinal);

    private static AuthenticationHeaderValue Bearer(TokenSet tokens) =>
        new("Bearer", tokens.AccessToken ?? throw new InvalidOperationException("The token set has no access token."));
}
