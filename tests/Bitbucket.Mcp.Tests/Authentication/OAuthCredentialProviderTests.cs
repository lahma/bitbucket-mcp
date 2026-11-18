using System.Net;

using Bitbucket.Mcp.Authentication;
using Bitbucket.Mcp.Configuration;


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
    [Test]
    public async Task ValidCachedTokenIsUsedWithoutTouchingTheNetworkOrTheBrowser()
    {
        using var context = new OAuthContext();

        await context.SeedAsync(
            AuthTestSupport.TokenSetFor("access-cached", context.Now.AddHours(1)),
            TestContext.CurrentContext.CancellationToken);

        Assert.That(await context.AcquireAsync(), Is.EqualTo("access-cached"));
        Assert.That(await context.AcquireAsync(), Is.EqualTo("access-cached"));

        Assert.That(context.Handler.Requests, Is.Empty);
        Assert.That(context.Authenticator.Calls, Is.EqualTo(0));
    }

    /// <summary>
    /// D14's sixty-second skew, at the boundary: a token with less than a minute left is already
    /// expired as far as this server is concerned, because the request it would be used on has to
    /// survive its own flight time.
    /// </summary>
    [TestCase(3600, false)]
    [TestCase(61, false)]
    [TestCase(60, true)]
    [TestCase(59, true)]
    [TestCase(-1, true)]
    public async Task ExpirySkewDecidesWhetherACachedTokenIsStillGoodEnough(int secondsUntilExpiry, bool expectRefresh)
    {
        using var context = new OAuthContext();

        await context.SeedAsync(
            AuthTestSupport.TokenSetFor("access-cached", context.Now.AddSeconds(secondsUntilExpiry)),
            TestContext.CurrentContext.CancellationToken);

        context.Handler.EnqueueJson(AuthTestSupport.TokenResponseJson("access-refreshed"));

        var token = await context.AcquireAsync();

        if (expectRefresh)
        {
            Assert.That(token, Is.EqualTo("access-refreshed"));
            Assert.That(context.Handler.Requests, Has.Exactly(1).Items);
        }
        else
        {
            Assert.That(token, Is.EqualTo("access-cached"));
            Assert.That(context.Handler.Requests, Is.Empty);
        }
    }

    /// <summary>
    /// The rotation is written — atomically — before the new access token is handed out, and the
    /// token that was just spent is kept as <c>previousRefreshToken</c>. Losing either half is how
    /// a user gets silently signed out.
    /// </summary>
    [Test]
    public async Task SuccessfulRefreshPersistsTheRotationBeforeReturningTheToken()
    {
        using var context = new OAuthContext();

        await context.SeedAsync(
            AuthTestSupport.TokenSetFor("access-old", context.Now.AddSeconds(-30), refreshToken: "refresh-1"),
            TestContext.CurrentContext.CancellationToken);

        context.Handler.EnqueueJson(AuthTestSupport.TokenResponseJson("access-new", refreshToken: "refresh-2", expiresIn: 7200));

        Assert.That(await context.AcquireAsync(), Is.EqualTo("access-new"));

        var request = Expect.Single(context.Handler.Requests);
        var form = AuthTestSupport.ParseForm(request.Body);

        Assert.That(form["grant_type"], Is.EqualTo("refresh_token"));
        Assert.That(form["refresh_token"], Is.EqualTo("refresh-1"));

        var cached = await context.ReadCacheAsync();

        Assert.That(cached, Is.Not.Null);
        Assert.That(cached.AccessToken, Is.EqualTo("access-new"));
        Assert.That(cached.RefreshToken, Is.EqualTo("refresh-2"));
        Assert.That(cached.PreviousRefreshToken, Is.EqualTo("refresh-1"));
        Assert.That(cached.ExpiresAtUtc, Is.EqualTo(context.Now.AddSeconds(7200)));
        Assert.That(cached.ConsumerKeyFingerprint, Is.EqualTo(AuthTestSupport.ConsumerKeyFingerprint));
        Assert.That(context.Authenticator.Calls, Is.EqualTo(0));
    }

    [Test]
    public async Task RefreshWithoutAReplacementKeepsTheCurrentRefreshToken()
    {
        using var context = new OAuthContext();

        await context.SeedAsync(
            AuthTestSupport.TokenSetFor("access-old", context.Now.AddSeconds(-30), refreshToken: "refresh-1"),
            TestContext.CurrentContext.CancellationToken);

        context.Handler.EnqueueJson(AuthTestSupport.TokenResponseJson("access-new", refreshToken: null));

        Assert.That(await context.AcquireAsync(), Is.EqualTo("access-new"));

        var cached = await context.ReadCacheAsync();

        Assert.That(cached, Is.Not.Null);
        Assert.That(cached.RefreshToken, Is.EqualTo("refresh-1"));
        Assert.That(cached.PreviousRefreshToken, Is.EqualTo("refresh-1"));
    }

    /// <summary>
    /// D14 forbids hard-coding a lifetime, so an endpoint that omits <c>expires_in</c> gets a short
    /// "check again soon" window rather than an assumed hour.
    /// </summary>
    [Test]
    public async Task ResponseWithoutExpiresInGetsAFiveMinuteWindow()
    {
        using var context = new OAuthContext();

        await context.SeedAsync(
            AuthTestSupport.TokenSetFor("access-old", context.Now.AddSeconds(-30), refreshToken: "refresh-1"),
            TestContext.CurrentContext.CancellationToken);

        context.Handler.EnqueueJson(AuthTestSupport.TokenResponseJson("access-new", expiresIn: null));

        Assert.That(await context.AcquireAsync(), Is.EqualTo("access-new"));

        var cached = await context.ReadCacheAsync();

        Assert.That(cached, Is.Not.Null);
        Assert.That(cached.ExpiresAtUtc, Is.EqualTo(context.Now.AddMinutes(5)));
    }

    /// <summary>
    /// The recovery for a rotation that never reached disk — a crash between the endpoint answering
    /// and the file being written, or a racing process that rotated first. It must not cost a
    /// browser round trip.
    /// </summary>
    [Test]
    public async Task InvalidGrantRetriesOnceWithThePreviousRefreshToken()
    {
        using var context = new OAuthContext();

        await context.SeedAsync(
            AuthTestSupport.TokenSetFor(
                "access-old",
                context.Now.AddSeconds(-30),
                refreshToken: "refresh-2",
                previousRefreshToken: "refresh-1"),
            TestContext.CurrentContext.CancellationToken);

        context.Handler.Enqueue(HttpStatusCode.BadRequest, AuthTestSupport.ErrorResponseJson("invalid_grant"));
        context.Handler.EnqueueJson(AuthTestSupport.TokenResponseJson("access-new", refreshToken: "refresh-3"));

        Assert.That(await context.AcquireAsync(), Is.EqualTo("access-new"));

        Assert.That(context.Handler.Requests.Count, Is.EqualTo(2));
        Assert.That(AuthTestSupport.ParseForm(context.Handler.Requests[0].Body)["refresh_token"], Is.EqualTo("refresh-2"));
        Assert.That(AuthTestSupport.ParseForm(context.Handler.Requests[1].Body)["refresh_token"], Is.EqualTo("refresh-1"));
        Assert.That(context.Authenticator.Calls, Is.EqualTo(0));

        var cached = await context.ReadCacheAsync();

        Assert.That(cached, Is.Not.Null);
        Assert.That(cached.RefreshToken, Is.EqualTo("refresh-3"));
        Assert.That(cached.PreviousRefreshToken, Is.EqualTo("refresh-1"));
    }

    [Test]
    public async Task BothRefreshTokensDeadDiscardsTheCacheAndSignsInAgain()
    {
        using var context = new OAuthContext();

        await context.SeedAsync(
            AuthTestSupport.TokenSetFor(
                "access-old",
                context.Now.AddSeconds(-30),
                refreshToken: "refresh-2",
                previousRefreshToken: "refresh-1"),
            TestContext.CurrentContext.CancellationToken);

        context.Handler.Enqueue(HttpStatusCode.BadRequest, AuthTestSupport.ErrorResponseJson("invalid_grant"));
        context.Handler.Enqueue(HttpStatusCode.BadRequest, AuthTestSupport.ErrorResponseJson("invalid_grant"));
        context.Handler.EnqueueJson(AuthTestSupport.TokenResponseJson("access-fresh", refreshToken: "refresh-fresh"));

        Assert.That(await context.AcquireAsync(), Is.EqualTo("access-fresh"));

        Assert.That(context.Handler.Requests.Count, Is.EqualTo(3));
        Assert.That(context.Authenticator.Calls, Is.EqualTo(1));
        Assert.That(Expect.Single(context.Authenticator.ConsumerKeys), Is.EqualTo(AuthTestSupport.ConsumerKey));

        var exchange = AuthTestSupport.ParseForm(context.Handler.Requests[2].Body);

        Assert.That(exchange["grant_type"], Is.EqualTo("authorization_code"));
        Assert.That(exchange["code"], Is.EqualTo(context.Authenticator.Code));
        Assert.That(exchange["redirect_uri"], Is.EqualTo(context.Authenticator.RedirectUri));

        var cached = await context.ReadCacheAsync();

        Assert.That(cached, Is.Not.Null);
        Assert.That(cached.AccessToken, Is.EqualTo("access-fresh"));
        Assert.That(cached.RefreshToken, Is.EqualTo("refresh-fresh"));
        Assert.That(cached.PreviousRefreshToken, Is.Null);
    }

    [Test]
    public async Task DeadGrantWithoutABrowserDeletesTheCacheAndSaysWhy()
    {
        using var context = new OAuthContext(interactiveAvailable: false);

        await context.SeedAsync(
            AuthTestSupport.TokenSetFor(
                "access-old",
                context.Now.AddSeconds(-30),
                refreshToken: "refresh-2",
                previousRefreshToken: "refresh-1"),
            TestContext.CurrentContext.CancellationToken);

        context.Handler.Enqueue(HttpStatusCode.BadRequest, AuthTestSupport.ErrorResponseJson("invalid_grant"));
        context.Handler.Enqueue(HttpStatusCode.BadRequest, AuthTestSupport.ErrorResponseJson("invalid_grant"));

        var failure = await Expect.ThrowsAsync<AuthenticationRequiredException>(context.AcquireAsync);

        Assert.That(failure.Reason, Is.EqualTo(AuthenticationRequiredReason.BrowserUnavailable));
        Assert.That(File.Exists(context.Temp.TokenFilePath), Is.False, "A proven-dead grant must not be left on disk.");
    }

    /// <summary>
    /// A wrong consumer secret is not something a browser can fix, and the refresh token is
    /// probably still perfectly good — so the cache stays exactly as it was.
    /// </summary>
    [Test]
    public async Task RefreshFailureThatIsNotInvalidGrantKeepsTheCacheIntact()
    {
        using var context = new OAuthContext();

        await context.SeedAsync(
            AuthTestSupport.TokenSetFor("access-old", context.Now.AddSeconds(-30), refreshToken: "refresh-1"),
            TestContext.CurrentContext.CancellationToken);

        var before = await File.ReadAllBytesAsync(context.Temp.TokenFilePath, TestContext.CurrentContext.CancellationToken);

        context.Handler.Enqueue(
            HttpStatusCode.BadRequest,
            AuthTestSupport.ErrorResponseJson("invalid_client", "the consumer secret is wrong"));

        var failure = await Expect.ThrowsAsync<AuthenticationRequiredException>(context.AcquireAsync);

        Assert.That(failure.Reason, Is.EqualTo(AuthenticationRequiredReason.RefreshFailed));
        Assert.That(failure.Message, Does.Contain("invalid_client"));
        Assert.That(context.Handler.Requests, Has.Exactly(1).Items);
        Assert.That(context.Authenticator.Calls, Is.EqualTo(0));

        Assert.That(await File.ReadAllBytesAsync(context.Temp.TokenFilePath, TestContext.CurrentContext.CancellationToken), Is.EqualTo(before));
    }

    /// <summary>
    /// Tokens obtained with a different consumer key can only produce confusing 401s, so the cache
    /// counts as empty rather than being spent.
    /// </summary>
    [Test]
    public async Task CacheFromAnotherConsumerIsIgnoredAndTheUserSignsInAgain()
    {
        using var context = new OAuthContext();

        await context.SeedAsync(
            AuthTestSupport.TokenSetFor(
                "access-other",
                context.Now.AddHours(1),
                refreshToken: "refresh-other",
                consumerKeyFingerprint: OAuthCredentialProvider.ComputeFingerprint("a-different-consumer-key")),
            TestContext.CurrentContext.CancellationToken);

        context.Handler.EnqueueJson(AuthTestSupport.TokenResponseJson("access-fresh"));

        Assert.That(await context.AcquireAsync(), Is.EqualTo("access-fresh"));

        var request = Expect.Single(context.Handler.Requests);

        // Straight to the browser flow: the foreign refresh token is never spent.
        Assert.That(AuthTestSupport.ParseForm(request.Body)["grant_type"], Is.EqualTo("authorization_code"));
        Assert.That(context.Authenticator.Calls, Is.EqualTo(1));
    }

    [Test]
    public async Task NoBrowserModeReportsTheEnvironmentVariableThatCausedIt()
    {
        using var context = new OAuthContext(noBrowser: true);

        var failure = await Expect.ThrowsAsync<AuthenticationRequiredException>(context.AcquireAsync);

        Assert.That(failure.Reason, Is.EqualTo(AuthenticationRequiredReason.BrowserUnavailable));
        Assert.That(failure.Message, Does.Contain("BITBUCKET_MCP_NO_BROWSER"));
        Assert.That(context.Handler.Requests, Is.Empty);
        Assert.That(context.Authenticator.Calls, Is.EqualTo(0));
    }

    [Test]
    public async Task UnavailableAuthenticatorReportsHowToSignInInstead()
    {
        using var context = new OAuthContext(interactiveAvailable: false);

        var failure = await Expect.ThrowsAsync<AuthenticationRequiredException>(context.AcquireAsync);

        Assert.That(failure.Reason, Is.EqualTo(AuthenticationRequiredReason.BrowserUnavailable));
        Assert.That(failure.Message, Does.Contain("bitbucket-mcp login"));
        Assert.That(context.Authenticator.Calls, Is.EqualTo(0));
    }

    [Test]
    public async Task InteractiveSignInIsBoundedByTheConfiguredTimeout()
    {
        using var context = new OAuthContext(authTimeoutSeconds: 1);

        context.Authenticator.WaitForCancellation = true;

        var failure = await Expect.ThrowsAsync<AuthenticationRequiredException>(context.AcquireAsync);

        Assert.That(failure.Reason, Is.EqualTo(AuthenticationRequiredReason.InteractiveTimeout));
        Assert.That(failure.Message, Does.Contain("BITBUCKET_MCP_AUTH_TIMEOUT_SECONDS"));
        Assert.That(context.Handler.Requests, Is.Empty);
    }

    /// <summary>
    /// Single-use refresh tokens make a concurrent double refresh actively destructive: the second
    /// one would spend a token the first has already replaced.
    /// </summary>
    [Test]
    public async Task ConcurrentCallersProduceExactlyOneRefresh()
    {
        using var context = new OAuthContext();

        await context.SeedAsync(
            AuthTestSupport.TokenSetFor("access-old", context.Now.AddSeconds(-30), refreshToken: "refresh-1"),
            TestContext.CurrentContext.CancellationToken);

        // One responder only: a second request falls through to a fallback that does not exist and
        // fails the test loudly rather than silently succeeding.
        context.Handler.EnqueueJson(AuthTestSupport.TokenResponseJson("access-new", refreshToken: "refresh-2"));

        var callers = new Task<string>[8];

        for (var i = 0; i < callers.Length; i++)
        {
            callers[i] = Task.Run(context.AcquireAsync, TestContext.CurrentContext.CancellationToken);
        }

        var tokens = await Task.WhenAll(callers);

        Assert.That(tokens, Is.All.EqualTo("access-new"));
        Assert.That(context.Handler.Requests, Has.Exactly(1).Items);
    }

    /// <summary>
    /// Without remembering the rejected token, "invalidate" would be a suggestion: the very next
    /// call re-reads the cache and finds the same token the API just refused sitting in it.
    /// </summary>
    [Test]
    public async Task InvalidatedAccessTokenIsNotHandedBackFromTheCache()
    {
        using var context = new OAuthContext();

        await context.SeedAsync(
            AuthTestSupport.TokenSetFor("access-rejected", context.Now.AddHours(1), refreshToken: "refresh-1"),
            TestContext.CurrentContext.CancellationToken);

        Assert.That(await context.AcquireAsync(), Is.EqualTo("access-rejected"));
        Assert.That(context.Handler.Requests, Is.Empty);

        await context.Provider.InvalidateAsync(TestContext.CurrentContext.CancellationToken);

        context.Handler.EnqueueJson(AuthTestSupport.TokenResponseJson("access-replacement"));

        Assert.That(await context.AcquireAsync(), Is.EqualTo("access-replacement"));
        Assert.That(context.Handler.Requests, Has.Exactly(1).Items);
    }

    /// <summary>
    /// A network blip is not a revoked grant. It must surface as itself and leave the cached
    /// refresh chain exactly where it was.
    /// </summary>
    [Test]
    public async Task TransportFailurePropagatesAndLeavesTheCacheAlone()
    {
        using var context = new OAuthContext();

        await context.SeedAsync(
            AuthTestSupport.TokenSetFor("access-old", context.Now.AddSeconds(-30), refreshToken: "refresh-1"),
            TestContext.CurrentContext.CancellationToken);

        context.Handler.Fallback = _ => throw new HttpRequestException("the name could not be resolved");

        await Assert.ThrowsAsync<HttpRequestException>(context.AcquireAsync);

        var cached = await context.ReadCacheAsync();

        Assert.That(cached, Is.Not.Null);
        Assert.That(cached.AccessToken, Is.EqualTo("access-old"));
        Assert.That(cached.RefreshToken, Is.EqualTo("refresh-1"));
        Assert.That(context.Authenticator.Calls, Is.EqualTo(0));
    }

    [Test]
    public async Task SignInForcesTheBrowserFlowEvenWithAPerfectlyGoodCachedToken()
    {
        using var context = new OAuthContext();

        await context.SeedAsync(
            AuthTestSupport.TokenSetFor("access-cached", context.Now.AddHours(1), refreshToken: "refresh-1"),
            TestContext.CurrentContext.CancellationToken);

        context.Handler.EnqueueJson(AuthTestSupport.TokenResponseJson("access-interactive", refreshToken: "refresh-interactive"));

        var tokens = await context.Provider.SignInAsync(TestContext.CurrentContext.CancellationToken);

        Assert.That(tokens.AccessToken, Is.EqualTo("access-interactive"));
        Assert.That(context.Authenticator.Calls, Is.EqualTo(1));

        var request = Expect.Single(context.Handler.Requests);

        Assert.That(AuthTestSupport.ParseForm(request.Body)["grant_type"], Is.EqualTo("authorization_code"));

        var cached = await context.ReadCacheAsync();

        Assert.That(cached, Is.Not.Null);
        Assert.That(cached.AccessToken, Is.EqualTo("access-interactive"));
        Assert.That(cached.RefreshToken, Is.EqualTo("refresh-interactive"));
    }

    [Test]
    public async Task DescribeNeverLeaksTheConsumerKeyOrTheAccessToken()
    {
        using var context = new OAuthContext();

        Assert.That(context.Provider.Describe(), Does.Contain("no access token in memory"));

        await context.SeedAsync(
            AuthTestSupport.TokenSetFor("access-secret-value", context.Now.AddHours(1)),
            TestContext.CurrentContext.CancellationToken);

        Assert.That(await context.AcquireAsync(), Is.EqualTo("access-secret-value"));

        var description = context.Provider.Describe();

        Assert.That(description, Does.StartWith("OAuth (consumer "));
        Assert.That(description, Does.Not.Contain("access-secret-value"));
        Assert.That(description, Does.Not.Contain(AuthTestSupport.ConsumerKey));
        Assert.That(description, Does.Not.Contain(AuthTestSupport.ConsumerSecret));
    }

    [Test]
    public void DescribeSaysWhichVariablesAreMissingWhenNothingIsConfigured()
    {
        using var context = new OAuthContext(consumerKey: null, consumerSecret: null);

        var description = context.Provider.Describe();

        Assert.That(description, Does.Contain("BITBUCKET_OAUTH_KEY"));
        Assert.That(description, Does.Contain("BITBUCKET_OAUTH_SECRET"));
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
            AuthTestSupport.ReadCacheAsync(Temp.TokenFilePath, TestContext.CurrentContext.CancellationToken);

        /// <summary>Asks for a header and returns the bearer token in it.</summary>
        internal async Task<string> AcquireAsync()
        {
            var header = await Provider.GetAuthenticationHeaderAsync(TestContext.CurrentContext.CancellationToken);

            Assert.That(header.Scheme, Is.EqualTo("Bearer"));

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
