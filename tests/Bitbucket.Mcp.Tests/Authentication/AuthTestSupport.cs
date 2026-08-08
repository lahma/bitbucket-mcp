using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

using Bitbucket.Mcp.Authentication;
using Bitbucket.Mcp.Configuration;
using Bitbucket.Mcp.Http.Models;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bitbucket.Mcp.Tests.Authentication;

/// <summary>
/// Shared scaffolding for the authentication tests: a hand-rolled clock, a throwaway token
/// directory, a stub interactive authenticator, and the small amount of JSON plumbing the token
/// endpoint and the token cache need.
/// </summary>
/// <remarks>
/// Nothing here uses a mocking or assertion library, and nothing mutates the process environment or
/// the real per-OS token location (AGENTS.md: the package budget is closed, and the tests have to
/// stay parallel-safe).
/// </remarks>
internal static class AuthTestSupport
{
    /// <summary>The consumer key every test signs in with, unless it is testing a mismatch.</summary>
    internal const string ConsumerKey = "test-consumer-key";

    /// <summary>The consumer secret paired with <see cref="ConsumerKey"/>.</summary>
    internal const string ConsumerSecret = "test-consumer-secret";

    /// <summary>
    /// A token endpoint that resolves to nothing. Every test replaces the transport, so this only
    /// has to be a distinctive absolute URL: if a request ever escapes the stub, the failure names
    /// this host rather than quietly reaching Bitbucket.
    /// </summary>
    internal static readonly Uri TokenEndpoint = new("https://token-endpoint.invalid/site/oauth2/access_token");

    /// <summary>Logger factory for components under test. Their output is not the subject here.</summary>
    internal static ILoggerFactory Loggers => NullLoggerFactory.Instance;

    /// <summary>The fingerprint the provider stamps into the cache for <see cref="ConsumerKey"/>.</summary>
    internal static string ConsumerKeyFingerprint =>
        OAuthCredentialProvider.ComputeFingerprint(ConsumerKey)
        ?? throw new InvalidOperationException("The consumer key fingerprint must be computable.");

    /// <summary>Options for an OAuth-configured server whose cache lives at <paramref name="tokenFilePath"/>.</summary>
    internal static BitbucketMcpOptions OAuthOptions(
        string tokenFilePath,
        string? consumerKey = ConsumerKey,
        string? consumerSecret = ConsumerSecret,
        bool noBrowser = false,
        int authTimeoutSeconds = BitbucketMcpOptions.DefaultAuthTimeoutSeconds) =>
        new()
        {
            OAuthKey = consumerKey,
            OAuthSecret = consumerSecret,
            TokenFilePath = tokenFilePath,
            NoBrowser = noBrowser,
            AuthTimeoutSeconds = authTimeoutSeconds,
        };

    /// <summary>A token store over an arbitrary path, used both to seed and to read back a cache.</summary>
    internal static TokenStore StoreFor(string tokenFilePath, TimeProvider? timeProvider = null) =>
        new(new BitbucketMcpOptions { TokenFilePath = tokenFilePath }, Loggers, timeProvider);

    /// <summary>A token set stamped with the current layout version and consumer fingerprint.</summary>
    internal static TokenSet TokenSetFor(
        string accessToken,
        DateTimeOffset expiresAtUtc,
        string? refreshToken = "refresh-1",
        string? previousRefreshToken = null,
        string? consumerKeyFingerprint = null) =>
        new()
        {
            Version = TokenSet.CurrentVersion,
            ConsumerKeyFingerprint = consumerKeyFingerprint ?? ConsumerKeyFingerprint,
            AccessToken = accessToken,
            ExpiresAtUtc = expiresAtUtc,
            RefreshToken = refreshToken,
            PreviousRefreshToken = previousRefreshToken,
            Scopes = "pullrequest:write repository:write",
        };

    /// <summary>A success body from the token endpoint, in Bitbucket's shape.</summary>
    internal static string TokenResponseJson(
        string accessToken,
        string? refreshToken = "refresh-2",
        int? expiresIn = 7200,
        string? scopes = "pullrequest:write")
    {
        var builder = new StringBuilder("{\"access_token\":\"").Append(accessToken).Append("\",\"token_type\":\"bearer\"");

        if (expiresIn is { } seconds)
        {
            builder.Append(",\"expires_in\":").Append(seconds.ToString(CultureInfo.InvariantCulture));
        }

        if (refreshToken is not null)
        {
            builder.Append(",\"refresh_token\":\"").Append(refreshToken).Append('"');
        }

        if (scopes is not null)
        {
            builder.Append(",\"scopes\":\"").Append(scopes).Append('"');
        }

        return builder.Append('}').ToString();
    }

    /// <summary>An RFC 6749 error body from the token endpoint.</summary>
    internal static string ErrorResponseJson(string error, string? description = null) =>
        description is null
            ? $"{{\"error\":\"{error}\"}}"
            : $"{{\"error\":\"{error}\",\"error_description\":\"{description}\"}}";

    /// <summary>Writes a token cache through the production store, so the at-rest format is the real one.</summary>
    internal static async Task SeedCacheAsync(string tokenFilePath, TokenSet tokens, CancellationToken cancellationToken) =>
        await StoreFor(tokenFilePath).SaveAsync(tokens, cancellationToken);

    /// <summary>Reads a token cache back through the production store.</summary>
    internal static async Task<TokenSet?> ReadCacheAsync(string tokenFilePath, CancellationToken cancellationToken) =>
        await StoreFor(tokenFilePath).LoadAsync(cancellationToken);

    /// <summary>Reads the raw on-disk envelope, without decoding or decrypting the payload.</summary>
    internal static async Task<TokenFileEnvelope> ReadEnvelopeAsync(string tokenFilePath, CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(tokenFilePath, cancellationToken);

        return JsonSerializer.Deserialize(bytes, BitbucketWireJsonContext.Default.TokenFileEnvelope)
            ?? throw new InvalidOperationException("The token file did not contain an envelope.");
    }

    /// <summary>Splits an <c>application/x-www-form-urlencoded</c> body into its decoded pairs.</summary>
    internal static IReadOnlyDictionary<string, string> ParseForm(string? body)
    {
        var form = new Dictionary<string, string>(StringComparer.Ordinal);

        if (string.IsNullOrEmpty(body))
        {
            return form;
        }

        foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);

            if (separator < 0)
            {
                continue;
            }

            form[WebUtility.UrlDecode(pair[..separator])] = WebUtility.UrlDecode(pair[(separator + 1)..]);
        }

        return form;
    }

    /// <summary>The decoded value of a query parameter in an absolute URL.</summary>
    internal static string QueryValue(string url, string name)
    {
        var separator = url.IndexOf('?', StringComparison.Ordinal);

        if (separator < 0)
        {
            throw new InvalidOperationException($"'{url}' has no query string.");
        }

        var form = ParseForm(url[(separator + 1)..]);

        return form.TryGetValue(name, out var value)
            ? value
            : throw new InvalidOperationException($"'{url}' has no '{name}' parameter.");
    }

    /// <summary>
    /// Binds the loopback callback listener on a random high port, retrying past a port another
    /// process happens to hold.
    /// </summary>
    /// <remarks>
    /// Port <c>0</c> is not usable here: the flow needs the port up front, to build the redirect URI
    /// the authorization server is given. The production default (33418) is deliberately never
    /// bound by a test — a developer signing in in another window must not be disturbed.
    /// </remarks>
    internal static (LoopbackCallbackListener Listener, int Port) StartListener(
        string expectedPath = LoopbackCallbackListener.DefaultCallbackPath)
    {
        for (var attempt = 0; attempt < 32; attempt++)
        {
            var port = NextScratchPort();
            var listener = new LoopbackCallbackListener(port, expectedPath, NullLogger.Instance);

            try
            {
                listener.Start();
                return (listener, port);
            }
            catch (SocketException)
            {
                listener.Dispose();
            }
        }

        throw new InvalidOperationException("No free loopback port could be bound for the test.");
    }

    /// <summary>
    /// A high port that is free right now, for a component that binds the port itself. There is an
    /// unavoidable window between the probe and the real bind; the range is wide enough that losing
    /// it is a curiosity rather than a flake.
    /// </summary>
    internal static int FreeScratchPort()
    {
        for (var attempt = 0; attempt < 32; attempt++)
        {
            var port = NextScratchPort();
            var probe = new TcpListener(IPAddress.Loopback, port);

            try
            {
                probe.Start();
                return port;
            }
            catch (SocketException)
            {
                continue;
            }
            finally
            {
                probe.Dispose();
            }
        }

        throw new InvalidOperationException("No free loopback port could be found for the test.");
    }

    /// <summary>Issues one GET and buffers the whole response, so the client can be disposed at once.</summary>
    internal static async Task<(HttpStatusCode Status, string Body)> GetAsync(string url, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using var response = await client.GetAsync(new Uri(url), cancellationToken);

        return (response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken));
    }

    private static int NextScratchPort()
    {
        int port;

        do
        {
            port = Random.Shared.Next(40_000, 60_000);
        }
        while (port == BitbucketMcpOptions.DefaultOAuthCallbackPort);

        return port;
    }
}

/// <summary>
/// A <see cref="TimeProvider"/> whose clock is under the test's control, hand-rolled because the
/// package budget has no room for <c>Microsoft.Extensions.TimeProvider.Testing</c>.
/// </summary>
/// <remarks>
/// Timers are delegated to the real <see cref="TimeProvider.System"/> by default rather than being
/// queued against the fake clock: the code under test uses them only for short backoffs and for the
/// interactive timeout, and a timer that never fires would hang a test rather than fail it. Set
/// <see cref="ExpireTimersImmediately"/> where the delay itself is the thing that must not be paid
/// (the retry handler's exponential backoff).
/// </remarks>
internal sealed class TestTimeProvider : TimeProvider
{
    /// <summary>The instant every test clock starts at.</summary>
    internal static readonly DateTimeOffset DefaultNow = new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);

    private readonly Lock _gate = new();
    private DateTimeOffset _utcNow;

    internal TestTimeProvider()
        : this(DefaultNow)
    {
    }

    internal TestTimeProvider(DateTimeOffset utcNow) => _utcNow = utcNow;

    /// <summary>
    /// How far the clock moves on every read. Drives a loop that polls <see cref="GetUtcNow"/>
    /// against a deadline — the token store's lock backoff — to its timeout in a few milliseconds.
    /// </summary>
    internal TimeSpan AutoAdvance { get; set; }

    /// <summary>Whether created timers fire at once instead of being scheduled.</summary>
    internal bool ExpireTimersImmediately { get; set; }

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            var now = _utcNow;
            _utcNow = now + AutoAdvance;
            return now;
        }
    }

    /// <summary>Moves the clock forward.</summary>
    internal void Advance(TimeSpan delta)
    {
        lock (_gate)
        {
            _utcNow += delta;
        }
    }

    /// <inheritdoc />
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);

        return ExpireTimersImmediately
            ? new ImmediateTimer(callback, state)
            : TimeProvider.System.CreateTimer(callback, state, dueTime, period);
    }

    /// <summary>A timer that fires once, as soon as it exists.</summary>
    private sealed class ImmediateTimer : ITimer
    {
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private int _fired;

        internal ImmediateTimer(TimerCallback callback, object? state)
        {
            _callback = callback;
            _state = state;
            Fire();
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Fire();
            return true;
        }

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private void Fire()
        {
            if (Interlocked.Exchange(ref _fired, 1) != 0)
            {
                return;
            }

            // Never inline: the callback completes the task that is awaiting this timer, and the
            // field holding it may not have been assigned yet.
            ThreadPool.QueueUserWorkItem(static timer => timer._callback(timer._state), this, preferLocal: false);
        }
    }
}

/// <summary>
/// A private directory for one test's token cache. The directory is deliberately <em>not</em>
/// created: several tests assert that the code under test touches no disk at all.
/// </summary>
internal sealed class TempTokenDirectory : IDisposable
{
    internal TempTokenDirectory()
    {
        DirectoryPath = Path.Combine(Path.GetTempPath(), "bitbucket-mcp-tests", Path.GetRandomFileName());
        TokenFilePath = Path.Combine(DirectoryPath, TokenStore.TokenFileName);
        LockFilePath = Path.Combine(DirectoryPath, TokenStore.LockFileName);
    }

    /// <summary>The directory that would hold the cache.</summary>
    internal string DirectoryPath { get; }

    /// <summary>The token cache path to hand to <c>BITBUCKET_MCP_TOKEN_FILE</c>.</summary>
    internal string TokenFilePath { get; }

    /// <summary>The cross-process lock path the store derives from <see cref="TokenFilePath"/>.</summary>
    internal string LockFilePath { get; }

    /// <summary>Creates the directory, for a test that needs it to exist before the store does.</summary>
    internal void Create() => Directory.CreateDirectory(DirectoryPath);

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory is not worth failing a green test over.
        }
    }
}

/// <summary>
/// Stands in for the browser flow: records that it was asked, and answers with a fixed
/// authorization code, a failure, or an indefinite wait for the caller's timeout to fire.
/// </summary>
internal sealed class StubInteractiveAuthenticator : IInteractiveAuthenticator
{
    private readonly Lock _gate = new();
    private readonly List<string> _consumerKeys = [];

    /// <inheritdoc />
    public bool IsAvailable { get; set; } = true;

    /// <summary>The code handed back on success.</summary>
    internal string Code { get; set; } = "authorization-code";

    /// <summary>The redirect URI handed back on success, repeated verbatim in the token exchange.</summary>
    internal string RedirectUri { get; set; } = "http://127.0.0.1:44444/callback";

    /// <summary>Thrown instead of returning, when set.</summary>
    internal Exception? Failure { get; set; }

    /// <summary>Whether to block until the caller's token is cancelled, to exercise the timeout.</summary>
    internal bool WaitForCancellation { get; set; }

    /// <summary>The consumer keys the provider asked to authorize, in order.</summary>
    internal IReadOnlyList<string> ConsumerKeys
    {
        get
        {
            lock (_gate)
            {
                return [.. _consumerKeys];
            }
        }
    }

    /// <summary>How many sign-ins were started.</summary>
    internal int Calls
    {
        get
        {
            lock (_gate)
            {
                return _consumerKeys.Count;
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask<InteractiveAuthorizationResult> AuthorizeAsync(string consumerKey, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _consumerKeys.Add(consumerKey);
        }

        if (Failure is { } failure)
        {
            throw failure;
        }

        if (WaitForCancellation)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();

        return new InteractiveAuthorizationResult(Code, RedirectUri);
    }
}
