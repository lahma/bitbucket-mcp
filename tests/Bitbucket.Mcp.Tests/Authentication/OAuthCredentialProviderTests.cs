using System.Net;

using Bitbucket.Mcp.Authentication;
using Bitbucket.Mcp.Configuration;

using Xunit;

namespace Bitbucket.Mcp.Tests.Authentication;

/// <summary>
/// The refresh state machine — the highest-risk logic in the server, because Bitbucket's refresh
/// tokens are single-use. Each test below pins one step of it: a valid token costs nothing, a
/// rotation is persisted before it is used, a lost rotation is recovered from the previous token,
/// a dead grant falls back to the browser, and a failure that a browser cannot fix never destroys a
/// working cache.
/// </summary>
public sealed class OAuthCredentialProviderTests
{
    [Fact]
    public async Task ValidCachedTokenIsUsedWithoutTouchingTheNetworkOrTheBrowser()
    {
        using var context = new OAuthContext();

        await context.SeedAsync(
            AuthTestSupport.TokenSetFor("access-cached", context.Now.AddHours(1)),
            TestContext.Current.CancellationToken);

        Assert.Equal("access-cached", await context.AcquireAsync());
        Assert.Equal("access-cached", await context.AcquireAsync());

        Assert.Empty(context.Handler.Requests);
        Assert.Equal(0, context.Authenticator.Calls);
    }

    /// <summary>
    /// D14's sixty-second skew, at the boundary: a token with less than a minute left is already
    /// expired as far as this server is concerned, because the request it would be used on has to
    /// survive its own flight time.
    /// </summary>
    [Theory]
    [InlineData(3600, false)]
    [InlineData(61, false)]
    [InlineData(60, true)]
    [InlineData(59, true)]
    [InlineData(-1, true)]
    public async Task ExpirySkewDecidesWhetherACachedTokenIsStillGoodEnough(int secondsUntilExpiry, bool expectRefresh)
    {
        using var context = new OAuthContext();

        await context.SeedAsync(
            AuthTestSupport.TokenSetFor("access-cached", context.Now.AddSeconds(secondsUntilExpiry)),
            TestContext.Current.CancellationToken);

        context.Handler.EnqueueJson(AuthTestSupport.TokenResponseJson("access-refreshed"));

        var token = await context.AcquireAsync();

        if (expectRefresh)
        {
            Assert.Equal("access-refreshed", token);
            Assert.Single(context.Handler.Requests);
        }
        else
        {
            Assert.Equal("access-cached", token);
            Assert.Empty(context.Handler.Requests);
        }
    }

    /// <summary>
    /// The rotation is written — atomically — before the new access token is handed out, and the
    /// token that was just spent is kept as <c>previousRefreshToken</c>. Losing either half is how
    /// a user gets silently signed out.
    /// </summary>
    [Fact]
    public async Task SuccessfulRefreshPersistsTheRotationBeforeReturningTheToken()
    {
        using var context = new OAuthContext();

        await context.SeedAsync(
            AuthTestSupport.TokenSetFor("access-old", context.Now.AddSeconds(-30), refreshToken: "refresh-1"),
            TestContext.Current.CancellationToken);

        context.Handler.EnqueueJson(AuthTestSupport.TokenResponseJson("access-new", refreshToken: "refresh-2", expiresIn: 7200));

        Assert.Equal("access-new", await context.AcquireAsync());

        var request = Assert.Single(context.Handler.Requests);
        var form = AuthTestSupport.ParseForm(request.Body);

        Assert.Equal("refresh_token", form["grant_type"]);
        Assert.Equal("refresh-1", form["refresh_token"]);

        var cached = await context.ReadCacheAsync();

        Assert.NotNull(cached);
        Assert.Equal("access-new", cached.AccessToken);
        Assert.Equal("refresh-2", cached.RefreshToken);
        Assert.Equal("refresh-1", cached.PreviousRefreshToken);
        Assert.Equal(context.Now.AddSeconds(7200), cached.ExpiresAtUtc);
        Assert.Equal(AuthTestSupport.ConsumerKeyFingerprint, cached.ConsumerKeyFingerprint);
        Assert.Equal(0, context.Authenticator.Calls);
    }

    [Fact]
    public async Task RefreshWithoutAReplacementKeepsTheCurrentRefreshToken()
    {
        using var context = new OAuthContext();

        await context.SeedAsync(
            AuthTestSupport.TokenSetFor("access-old", context.Now.AddSeconds(-30), refreshToken: "refresh-1"),
            TestContext.Current.CancellationToken);

        context.Handler.EnqueueJson(AuthTestSupport.TokenResponseJson("access-new", refreshToken: null));

        Assert.Equal("access-new", await context.AcquireAsync());

        var cached = await context.ReadCacheAsync();

        Assert.NotNull(cached);
        Assert.Equal("refresh-1", cached.RefreshToken);
        Assert.Equal("refresh-1", cached.PreviousRefreshToken);
    }

    /// <summary>
    /// D14 forbids hard-coding a lifetime, so an endpoint that omits <c>expires_in</c> gets a short
    /// "check again soon" window rather than an assumed hour.
    /// </summary>
    [Fact]
    public async Task ResponseWithoutExpiresInGetsAFiveMinuteWindow()
    {
        using var context = new OAuthContext();

        await context.SeedAsync(
            AuthTestSupport.TokenSetFor("access-old", context.Now.AddSeconds(-30), refreshToken: "refresh-1"),
            TestContext.Current.CancellationToken);

        context.Handler.EnqueueJson(AuthTestSupport.TokenResponseJson("access-new", expiresIn: null));

        Assert.Equal("access-new", await context.AcquireAsync());

        var cached = await context.ReadCacheAsync();

        Assert.NotNull(cached);
        Assert.Equal(context.Now.AddMinutes(5), cached.ExpiresAtUtc);
    }

    /// <summary>
    /// The recovery for a rotation that never reached disk — a crash between the endpoint answering
    /// and the file being written, or a racing process that rotated first. It must not cost a
    /// browser round trip.
    /// </summary>
    [Fact]
    public async Task InvalidGrantRetriesOnceWithThePreviousRefreshToken()
    {
        using var context = new OAuthContext();

        await context.SeedAsync(
            AuthTestSupport.TokenSetFor(
                "access-old",
                context.Now.AddSeconds(-30),
                refreshToken: "refresh-2",
                previousRefreshToken: "refresh-1"),
            TestContext.Current.CancellationToken);

        context.Handler.Enqueue(HttpStatusCode.BadRequest, AuthTestSupport.ErrorResponseJson("invalid_grant"));
        context.Handler.EnqueueJson(AuthTestSupport.TokenResponseJson("access-new", refreshToken: "refresh-3"));

        Assert.Equal("access-new", await context.AcquireAsync());

        Assert.Equal(2, context.Handler.Requests.Count);
        Assert.Equal("refresh-2", AuthTestSupport.ParseForm(context.Handler.Requests[0].Body)["refresh_token"]);
        Assert.Equal("refresh-1", AuthTestSupport.ParseForm(context.Handler.Requests[1].Body)["refresh_token"]);
        Assert.Equal(0, context.Authenticator.Calls);

        var cached = await context.ReadCacheAsync();

        Assert.NotNull(cached);
        Assert.Equal("refresh-3", cached.RefreshToken);
        Assert.Equal("refresh-1", cached.PreviousRefreshToken);
    }

    [Fact]
    public async Task BothRefreshTokensDeadDiscardsTheCacheAndSignsInAgain()
    {
        using var context = new OAuthContext();

        await context.SeedAsync(
            AuthTestSupport.TokenSetFor(
                "access-old",
                context.Now.AddSeconds(-30),
                refreshToken: "refresh-2",
                previousRefreshToken: "refresh-1"),
            TestContext.Current.CancellationToken);

        context.Handler.Enqueue(HttpStatusCode.BadRequest, AuthTestSupport.ErrorResponseJson("invalid_grant"));
        context.Handler.Enqueue(HttpStatusCode.BadRequest, AuthTestSupport.ErrorResponseJson("invalid_grant"));
        context.Handler.EnqueueJson(AuthTestSupport.TokenResponseJson("access-fresh", refreshToken: "refresh-fresh"));

        Assert.Equal("access-fresh", await context.AcquireAsync());

        Assert.Equal(3, context.Handler.Requests.Count);
        Assert.Equal(1, context.Authenticator.Calls);
        Assert.Equal(AuthTestSupport.ConsumerKey, Assert.Single(context.Authenticator.ConsumerKeys));

        var exchange = AuthTestSupport.ParseForm(context.Handler.Requests[2].Body);

        Assert.Equal("authorization_code", exchange["grant_type"]);
        Assert.Equal(context.Authenticator.Code, exchange["code"]);
        Assert.Equal(context.Authenticator.RedirectUri, exchange["redirect_uri"]);

        var cached = await context.ReadCacheAsync();

        Assert.NotNull(cached);
        Assert.Equal("access-fresh", cached.AccessToken);
        Assert.Equal("refresh-fresh", cached.RefreshToken);
        Assert.Null(cached.PreviousRefreshToken);
    }

    [Fact]
    public async Task DeadGrantWithoutABrowserDeletesTheCacheAndSaysWhy()
    {
        using var context = new OAuthContext(interactiveAvailable: false);

        await context.SeedAsync(
            AuthTestSupport.TokenSetFor(
                "access-old",
                context.Now.AddSeconds(-30),
                refreshToken: "refresh-2",
                previousRefreshToken: "refresh-1"),
            TestContext.Current.CancellationToken);

        context.Handler.Enqueue(HttpStatusCode.BadRequest, AuthTestSupport.ErrorResponseJson("invalid_grant"));
        context.Handler.Enqueue(HttpStatusCode.BadRequest, AuthTestSupport.ErrorResponseJson("invalid_grant"));

        var failure = await Assert.ThrowsAsync<AuthenticationRequiredException>(context.AcquireAsync);

        Assert.Equal(AuthenticationRequiredReason.BrowserUnavailable, failure.Reason);
        Assert.False(File.Exists(context.Temp.TokenFilePath), "A proven-dead grant must not be left on disk.");
    }

    /// <summary>
    /// A wrong consumer secret is not something a browser can fix, and the refresh token is
    /// probably still perfectly good — so the cache stays exactly as it was.
    /// </summary>
    [Fact]
    public async Task RefreshFailureThatIsNotInvalidGrantKeepsTheCacheIntact()
    {
        using var context = new OAuthContext();

        await context.SeedAsync(
            AuthTestSupport.TokenSetFor("access-old", context.Now.AddSeconds(-30), refreshToken: "refresh-1"),
            TestContext.Current.CancellationToken);

        var before = await File.ReadAllBytesAsync(context.Temp.TokenFilePath, TestContext.Current.CancellationToken);

        context.Handler.Enqueue(
            HttpStatusCode.BadRequest,
            AuthTestSupport.ErrorResponseJson("invalid_client", "the consumer secret is wrong"));

        var failure = await Assert.ThrowsAsync<AuthenticationRequiredException>(context.AcquireAsync);

        Assert.Equal(AuthenticationRequiredReason.RefreshFailed, failure.Reason);
        Assert.Contains("invalid_client", failure.Message, StringComparison.Ordinal);
        Assert.Single(context.Handler.Requests);
        Assert.Equal(0, context.Authenticator.Calls);

        Assert.Equal(before, await File.ReadAllBytesAsync(context.Temp.TokenFilePath, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Tokens obtained with a different consumer key can only produce confusing 401s, so the cache
    /// counts as empty rather than being spent.
    /// </summary>
    [Fact]
    public async Task CacheFromAnotherConsumerIsIgnoredAndTheUserSignsInAgain()
    {
        using var context = new OAuthContext();

        await context.SeedAsync(
            AuthTestSupport.TokenSetFor(
                "access-other",
                context.Now.AddHours(1),
                refreshToken: "refresh-other",
                consumerKeyFingerprint: OAuthCredentialProvider.ComputeFingerprint("a-different-consumer-key")),
            TestContext.Current.CancellationToken);

        context.Handler.EnqueueJson(AuthTestSupport.TokenResponseJson("access-fresh"));

        Assert.Equal("access-fresh", await context.AcquireAsync());

        var request = Assert.Single(context.Handler.Requests);

        // Straight to the browser flow: the foreign refresh token is never spent.
        Assert.Equal("authorization_code", AuthTestSupport.ParseForm(request.Body)["grant_type"]);
        Assert.Equal(1, context.Authenticator.Calls);
    }

    [Fact]
    public async Task NoBrowserModeReportsTheEnvironmentVariableThatCausedIt()
    {
        using var context = new OAuthContext(noBrowser: true);

        var failure = await Assert.ThrowsAsync<AuthenticationRequiredException>(context.AcquireAsync);

        Assert.Equal(AuthenticationRequiredReason.BrowserUnavailable, failure.Reason);
        Assert.Contains("BITBUCKET_MCP_NO_BROWSER", failure.Message, StringComparison.Ordinal);
        Assert.Empty(context.Handler.Requests);
        Assert.Equal(0, context.Authenticator.Calls);
    }

    [Fact]
    public async Task UnavailableAuthenticatorReportsHowToSignInInstead()
    {
        using var context = new OAuthContext(interactiveAvailable: false);

        var failure = await Assert.ThrowsAsync<AuthenticationRequiredException>(context.AcquireAsync);

        Assert.Equal(AuthenticationRequiredReason.BrowserUnavailable, failure.Reason);
        Assert.Contains("bitbucket-mcp login", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, context.Authenticator.Calls);
    }

    [Fact]
    public async Task InteractiveSignInIsBoundedByTheConfiguredTimeout()
    {
        using var context = new OAuthContext(authTimeoutSeconds: 1);

        context.Authenticator.WaitForCancellation = true;

        var failure = await Assert.ThrowsAsync<AuthenticationRequiredException>(context.AcquireAsync);

        Assert.Equal(AuthenticationRequiredReason.InteractiveTimeout, failure.Reason);
        Assert.Contains("BITBUCKET_MCP_AUTH_TIMEOUT_SECONDS", failure.Message, StringComparison.Ordinal);
        Assert.Empty(context.Handler.Requests);
    }

    /// <summary>
    /// Single-use refresh tokens make a concurrent double refresh actively destructive: the second
    /// one would spend a token the first has already replaced.
    /// </summary>
    [Fact]
    public async Task ConcurrentCallersProduceExactlyOneRefresh()
    {
        using var context = new OAuthContext();

        await context.SeedAsync(
            AuthTestSupport.TokenSetFor("access-old", context.Now.AddSeconds(-30), refreshToken: "refresh-1"),
            TestContext.Current.CancellationToken);

        // One responder only: a second request falls through to a fallback that does not exist and
        // fails the test loudly rather than silently succeeding.
        context.Handler.EnqueueJson(AuthTestSupport.TokenResponseJson("access-new", refreshToken: "refresh-2"));

        var callers = new Task<string>[8];

        for (var i = 0; i < callers.Length; i++)
        {
            callers[i] = Task.Run(context.AcquireAsync, TestContext.Current.CancellationToken);
        }

        var tokens = await Task.WhenAll(callers);

        Assert.All(tokens, token => Assert.Equal("access-new", token));
        Assert.Single(context.Handler.Requests);
    }

    /// <summary>
    /// Without remembering the rejected token, "invalidate" would be a suggestion: the very next
    /// call re-reads the cache and finds the same token the API just refused sitting in it.
    /// </summary>
    [Fact]
    public async Task InvalidatedAccessTokenIsNotHandedBackFromTheCache()
    {
        using var context = new OAuthContext();

        await context.SeedAsync(
            AuthTestSupport.TokenSetFor("access-rejected", context.Now.AddHours(1), refreshToken: "refresh-1"),
            TestContext.Current.CancellationToken);

        Assert.Equal("access-rejected", await context.AcquireAsync());
        Assert.Empty(context.Handler.Requests);

        await context.Provider.InvalidateAsync(TestContext.Current.CancellationToken);

        context.Handler.EnqueueJson(AuthTestSupport.TokenResponseJson("access-replacement"));

        Assert.Equal("access-replacement", await context.AcquireAsync());
        Assert.Single(context.Handler.Requests);
    }

    /// <summary>
    /// A network blip is not a revoked grant. It must surface as itself and leave the cached
    /// refresh chain exactly where it was.
    /// </summary>
    [Fact]
    public async Task TransportFailurePropagatesAndLeavesTheCacheAlone()
    {
        using var context = new OAuthContext();

        await context.SeedAsync(
            AuthTestSupport.TokenSetFor("access-old", context.Now.AddSeconds(-30), refreshToken: "refresh-1"),
            TestContext.Current.CancellationToken);

        context.Handler.Fallback = _ => throw new HttpRequestException("the name could not be resolved");

        await Assert.ThrowsAsync<HttpRequestException>(context.AcquireAsync);

        var cached = await context.ReadCacheAsync();

        Assert.NotNull(cached);
        Assert.Equal("access-old", cached.AccessToken);
        Assert.Equal("refresh-1", cached.RefreshToken);
        Assert.Equal(0, context.Authenticator.Calls);
    }

    [Fact]
    public async Task SignInForcesTheBrowserFlowEvenWithAPerfectlyGoodCachedToken()
    {
        using var context = new OAuthContext();

        await context.SeedAsync(
            AuthTestSupport.TokenSetFor("access-cached", context.Now.AddHours(1), refreshToken: "refresh-1"),
            TestContext.Current.CancellationToken);

        context.Handler.EnqueueJson(AuthTestSupport.TokenResponseJson("access-interactive", refreshToken: "refresh-interactive"));

        var tokens = await context.Provider.SignInAsync(TestContext.Current.CancellationToken);

        Assert.Equal("access-interactive", tokens.AccessToken);
        Assert.Equal(1, context.Authenticator.Calls);

        var request = Assert.Single(context.Handler.Requests);

        Assert.Equal("authorization_code", AuthTestSupport.ParseForm(request.Body)["grant_type"]);

        var cached = await context.ReadCacheAsync();

        Assert.NotNull(cached);
        Assert.Equal("access-interactive", cached.AccessToken);
        Assert.Equal("refresh-interactive", cached.RefreshToken);
    }

    [Fact]
    public async Task DescribeNeverLeaksTheConsumerKeyOrTheAccessToken()
    {
        using var context = new OAuthContext();

        Assert.Contains("no access token in memory", context.Provider.Describe(), StringComparison.Ordinal);

        await context.SeedAsync(
            AuthTestSupport.TokenSetFor("access-secret-value", context.Now.AddHours(1)),
            TestContext.Current.CancellationToken);

        Assert.Equal("access-secret-value", await context.AcquireAsync());

        var description = context.Provider.Describe();

        Assert.StartsWith("OAuth (consumer ", description, StringComparison.Ordinal);
        Assert.DoesNotContain("access-secret-value", description, StringComparison.Ordinal);
        Assert.DoesNotContain(AuthTestSupport.ConsumerKey, description, StringComparison.Ordinal);
        Assert.DoesNotContain(AuthTestSupport.ConsumerSecret, description, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeSaysWhichVariablesAreMissingWhenNothingIsConfigured()
    {
        using var context = new OAuthContext(consumerKey: null, consumerSecret: null);

        var description = context.Provider.Describe();

        Assert.Contains("BITBUCKET_OAUTH_KEY", description, StringComparison.Ordinal);
        Assert.Contains("BITBUCKET_OAUTH_SECRET", description, StringComparison.Ordinal);
    }

    /// <summary>
    /// One provider wired to a throwaway token file, a stubbed token endpoint and a stubbed browser
    /// flow. The clock is fixed, so every expiry assertion is exact.
    /// </summary>
    private sealed class OAuthContext : IDisposable
    {
        internal OAuthContext(
            bool interactiveAvailable = true,
            bool noBrowser = false,
            string? consumerKey = AuthTestSupport.ConsumerKey,
            string? consumerSecret = AuthTestSupport.ConsumerSecret,
            int authTimeoutSeconds = BitbucketMcpOptions.DefaultAuthTimeoutSeconds)
        {
            Temp = new TempTokenDirectory();
            Clock = new TestTimeProvider();

            var options = AuthTestSupport.OAuthOptions(
                Temp.TokenFilePath,
                consumerKey,
                consumerSecret,
                noBrowser,
                authTimeoutSeconds);

            Handler = new StubHttpMessageHandler();
            Authenticator = new StubInteractiveAuthenticator { IsAvailable = interactiveAvailable };

            TokenClient = new OAuthTokenClient(
                options,
                AuthTestSupport.Loggers,
                Handler,
                AuthTestSupport.TokenEndpoint,
                new TestTimeProvider { ExpireTimersImmediately = true });

            Provider = new OAuthCredentialProvider(
                options,
                new TokenStore(options, AuthTestSupport.Loggers, Clock),
                TokenClient,
                Authenticator,
                Clock,
                AuthTestSupport.Loggers);
        }

        internal TempTokenDirectory Temp { get; }

        internal TestTimeProvider Clock { get; }

        internal StubHttpMessageHandler Handler { get; }

        internal StubInteractiveAuthenticator Authenticator { get; }

        internal OAuthTokenClient TokenClient { get; }

        internal OAuthCredentialProvider Provider { get; }

        /// <summary>The fixed instant the provider's clock reports.</summary>
        internal DateTimeOffset Now => Clock.GetUtcNow();

        internal Task SeedAsync(TokenSet tokens, CancellationToken cancellationToken) =>
            AuthTestSupport.SeedCacheAsync(Temp.TokenFilePath, tokens, cancellationToken);

        internal Task<TokenSet?> ReadCacheAsync() =>
            AuthTestSupport.ReadCacheAsync(Temp.TokenFilePath, TestContext.Current.CancellationToken);

        /// <summary>Asks for a header and returns the bearer token in it.</summary>
        internal async Task<string> AcquireAsync()
        {
            var header = await Provider.GetAuthenticationHeaderAsync(TestContext.Current.CancellationToken);

            Assert.Equal("Bearer", header.Scheme);

            return header.Parameter ?? throw new InvalidOperationException("The bearer header carried no token.");
        }

        public void Dispose()
        {
            Provider.Dispose();
            TokenClient.Dispose();
            Temp.Dispose();
        }
    }
}
