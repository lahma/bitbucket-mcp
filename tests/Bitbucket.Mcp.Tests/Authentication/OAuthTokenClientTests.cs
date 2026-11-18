using System.Net;
using System.Text;

using Bitbucket.Mcp.Authentication;


namespace Bitbucket.Mcp.Tests.Authentication;

/// <summary>
/// The token endpoint client. What matters on the wire is exact: the grant type, the redirect URI
/// repeated byte for byte, HTTP Basic client authentication, and a form body. What matters on the
/// way back is the classification — <c>invalid_grant</c> drives the refresh state machine's one
/// retry, and everything else must not.
/// </summary>
public sealed class OAuthTokenClientTests
{
    private static readonly string ExpectedBasicCredentials = Convert.ToBase64String(
        Encoding.UTF8.GetBytes($"{AuthTestSupport.ConsumerKey}:{AuthTestSupport.ConsumerSecret}"));

    [Test]
    public async Task ExchangeSendsTheAuthorizationCodeGrantAsAnAuthenticatedForm()
    {
        var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(AuthTestSupport.TokenResponseJson("access-1"));

        using var client = CreateClient(handler);

        const string RedirectUri = "http://127.0.0.1:33418/callback";

        var result = await client.ExchangeAuthorizationCodeAsync(
            "code from the callback",
            RedirectUri,
            TestContext.CurrentContext.CancellationToken);

        Assert.That(result.IsSuccess, Is.True);

        var request = Expect.Single(handler.Requests);

        Assert.That(request.Method, Is.EqualTo(HttpMethod.Post));
        Assert.That(request.Uri, Is.EqualTo(AuthTestSupport.TokenEndpoint));
        Assert.That(request.Headers["Authorization"], Is.EqualTo($"Basic {ExpectedBasicCredentials}"));
        Assert.That(request.Headers["Content-Type"], Is.EqualTo("application/x-www-form-urlencoded"));

        var form = AuthTestSupport.ParseForm(request.Body);

        Assert.That(form["grant_type"], Is.EqualTo("authorization_code"));
        Assert.That(form["code"], Is.EqualTo("code from the callback"));

        // Verbatim: the authorization server compares the two redirect URIs as strings and rejects
        // a difference as small as a trailing slash.
        Assert.That(form["redirect_uri"], Is.EqualTo(RedirectUri));
        Assert.That(form.Keys, Does.Not.Contain("refresh_token"));
    }

    [Test]
    public async Task RefreshSendsTheRefreshTokenGrantAsAnAuthenticatedForm()
    {
        var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(AuthTestSupport.TokenResponseJson("access-2"));

        using var client = CreateClient(handler);

        var result = await client.RefreshAsync("refresh+token/with=specials", TestContext.CurrentContext.CancellationToken);

        Assert.That(result.IsSuccess, Is.True);

        var request = Expect.Single(handler.Requests);

        Assert.That(request.Headers["Authorization"], Is.EqualTo($"Basic {ExpectedBasicCredentials}"));

        var form = AuthTestSupport.ParseForm(request.Body);

        Assert.That(form["grant_type"], Is.EqualTo("refresh_token"));
        Assert.That(form["refresh_token"], Is.EqualTo("refresh+token/with=specials"));
        Assert.That(form.Keys, Does.Not.Contain("code"));
    }

    [Test]
    public async Task SuccessfulResponseCarriesTheTokensAndTheirLifetime()
    {
        var handler = new StubHttpMessageHandler();

        handler.EnqueueJson(AuthTestSupport.TokenResponseJson(
            "access-3",
            refreshToken: "refresh-3",
            expiresIn: 7200,
            scopes: "pullrequest:write repository"));

        using var client = CreateClient(handler);

        var result = await client.RefreshAsync("refresh-2", TestContext.CurrentContext.CancellationToken);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Token, Is.Not.Null);
        Assert.That(result.Token.AccessToken, Is.EqualTo("access-3"));
        Assert.That(result.Token.RefreshToken, Is.EqualTo("refresh-3"));
        Assert.That(result.Token.ExpiresIn, Is.EqualTo(7200));
        Assert.That(result.Token.GrantedScopes, Is.EqualTo("pullrequest:write repository"));
        Assert.That(result.DescribeFailure(), Is.EqualTo("no failure"));
    }

    /// <summary>Bitbucket has used both spellings; the client must read whichever arrived.</summary>
    [Test]
    public async Task GrantedScopesFallBackToTheRfcSpelling()
    {
        var handler = new StubHttpMessageHandler();
        handler.EnqueueJson("""{"access_token":"access-4","expires_in":3600,"scope":"pullrequest"}""");

        using var client = CreateClient(handler);

        var result = await client.RefreshAsync("refresh-3", TestContext.CurrentContext.CancellationToken);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Token!.GrantedScopes, Is.EqualTo("pullrequest"));
    }

    [TestCase("invalid_grant", true)]
    [TestCase("invalid_client", false)]
    [TestCase("unauthorized_client", false)]
    [TestCase("invalid_request", false)]
    [TestCase("invalid_grant_type", false)]
    public async Task ErrorCodesAreClassifiedByWhetherTheGrantItselfIsGone(string error, bool expectedInvalidGrant)
    {
        var handler = new StubHttpMessageHandler();

        handler.Enqueue(
            HttpStatusCode.BadRequest,
            AuthTestSupport.ErrorResponseJson(error, "the server explains itself"));

        using var client = CreateClient(handler);

        var result = await client.RefreshAsync("refresh-4", TestContext.CurrentContext.CancellationToken);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.IsInvalidGrant, Is.EqualTo(expectedInvalidGrant));
        Assert.That(result.Error, Is.EqualTo(error));
        Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(result.DescribeFailure(), Is.EqualTo($"HTTP 400 {error} (the server explains itself)"));

        // A 400 is a deterministic answer: reissuing it would only spend rate-limit budget.
        Assert.That(handler.Requests, Has.Exactly(1).Items);
    }

    [Test]
    public async Task ErrorWithoutADescriptionStillDescribesItself()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.BadRequest, AuthTestSupport.ErrorResponseJson("invalid_grant"));

        using var client = CreateClient(handler);

        var result = await client.RefreshAsync("refresh-5", TestContext.CurrentContext.CancellationToken);

        Assert.That(result.IsInvalidGrant, Is.True);
        Assert.That(result.DescribeFailure(), Is.EqualTo("HTTP 400 invalid_grant"));
    }

    /// <summary>
    /// Bitbucket answers the token endpoint with an HTML error page often enough that it cannot be
    /// an exception path: the status code and a collapsed excerpt are all there is to report, and
    /// the caller has to be able to put that in a message.
    /// </summary>
    [Test]
    public async Task HtmlErrorPageIsToleratedAndCollapsedIntoOneLine()
    {
        var handler = new StubHttpMessageHandler
        {
            Fallback = _ => StubHttpMessageHandler.CreateResponse(
                HttpStatusCode.BadGateway,
                "<html>\n  <head><title>502 Bad Gateway</title></head>\n  <body>\n\n<h1>Bad gateway</h1>\n</body>\n</html>",
                "text/html"),
        };

        using var client = CreateClient(handler);

        var result = await client.RefreshAsync("refresh-6", TestContext.CurrentContext.CancellationToken);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.IsInvalidGrant, Is.False);
        Assert.That(result.Error, Is.Null);
        Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.BadGateway));

        var failure = result.DescribeFailure();

        Assert.That(failure, Does.StartWith("HTTP 502 ("));
        Assert.That(failure, Does.Contain("502 Bad Gateway"));
        Assert.That(failure, Does.Not.Contain("\n"));

        // 502 is transient, so the client retried the whole budget before giving up.
        Assert.That(handler.Requests.Count, Is.EqualTo(4));
    }

    [Test]
    public async Task SuccessWithoutAnAccessTokenIsAFailure()
    {
        var handler = new StubHttpMessageHandler();
        handler.EnqueueJson("""{"token_type":"bearer","expires_in":7200}""");

        using var client = CreateClient(handler);

        var result = await client.RefreshAsync("refresh-7", TestContext.CurrentContext.CancellationToken);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.IsInvalidGrant, Is.False);
        Assert.That(result.DescribeFailure(), Does.Contain("no access token"));
    }

    [Test]
    public async Task SuccessWithABodyThatIsNotJsonIsAFailure()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, "<html>not json</html>", "text/html");

        using var client = CreateClient(handler);

        var result = await client.RefreshAsync("refresh-8", TestContext.CurrentContext.CancellationToken);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.DescribeFailure(), Does.Contain("not valid JSON"));
    }

    [Test]
    public async Task WithoutConsumerCredentialsNothingIsSentAndTheReasonIsActionable()
    {
        var handler = new StubHttpMessageHandler();

        using var client = CreateClient(handler, consumerKey: null, consumerSecret: null);

        Assert.That(client.IsConfigured, Is.False);

        var failure = await Expect.ThrowsAsync<AuthenticationRequiredException>(() => client.RefreshAsync("refresh-9", TestContext.CurrentContext.CancellationToken));

        Assert.That(failure.Reason, Is.EqualTo(AuthenticationRequiredReason.NotConfigured));
        Assert.That(failure.Message, Does.Contain("BITBUCKET_OAUTH_KEY"));
        Assert.That(handler.Requests, Is.Empty);
    }

    [Test]
    public async Task HalfConfiguredConsumerCredentialsCountAsUnconfigured()
    {
        var handler = new StubHttpMessageHandler();

        using var client = CreateClient(handler, consumerSecret: null);

        Assert.That(client.IsConfigured, Is.False);

        await Assert.ThrowsAsync<AuthenticationRequiredException>(() => client.ExchangeAuthorizationCodeAsync(
                "code",
                "http://127.0.0.1:33418/callback",
                TestContext.CurrentContext.CancellationToken));

        Assert.That(handler.Requests, Is.Empty);
    }

    private static OAuthTokenClient CreateClient(
        StubHttpMessageHandler handler,
        string? consumerKey = AuthTestSupport.ConsumerKey,
        string? consumerSecret = AuthTestSupport.ConsumerSecret) =>
        new(
            AuthTestSupport.OAuthOptions(
                Path.Combine(Path.GetTempPath(), "bitbucket-mcp-tests", "unused-tokens.json"),
                consumerKey,
                consumerSecret),
            AuthTestSupport.Loggers,
            handler,
            AuthTestSupport.TokenEndpoint,
            // The retry backoff is real time we are not going to spend on a test.
            new TestTimeProvider { ExpireTimersImmediately = true });
}
