using System.Net;
using System.Text;

using Bitbucket.Mcp.Authentication;

using Xunit;

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

    [Fact]
    public async Task ExchangeSendsTheAuthorizationCodeGrantAsAnAuthenticatedForm()
    {
        var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(AuthTestSupport.TokenResponseJson("access-1"));

        using var client = CreateClient(handler);

        const string RedirectUri = "http://127.0.0.1:33418/callback";

        var result = await client.ExchangeAuthorizationCodeAsync(
            "code from the callback",
            RedirectUri,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);

        var request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(AuthTestSupport.TokenEndpoint, request.Uri);
        Assert.Equal($"Basic {ExpectedBasicCredentials}", request.Headers["Authorization"]);
        Assert.Equal("application/x-www-form-urlencoded", request.Headers["Content-Type"]);

        var form = AuthTestSupport.ParseForm(request.Body);

        Assert.Equal("authorization_code", form["grant_type"]);
        Assert.Equal("code from the callback", form["code"]);

        // Verbatim: the authorization server compares the two redirect URIs as strings and rejects
        // a difference as small as a trailing slash.
        Assert.Equal(RedirectUri, form["redirect_uri"]);
        Assert.DoesNotContain("refresh_token", form.Keys, StringComparer.Ordinal);
    }

    [Fact]
    public async Task RefreshSendsTheRefreshTokenGrantAsAnAuthenticatedForm()
    {
        var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(AuthTestSupport.TokenResponseJson("access-2"));

        using var client = CreateClient(handler);

        var result = await client.RefreshAsync("refresh+token/with=specials", TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);

        var request = Assert.Single(handler.Requests);

        Assert.Equal($"Basic {ExpectedBasicCredentials}", request.Headers["Authorization"]);

        var form = AuthTestSupport.ParseForm(request.Body);

        Assert.Equal("refresh_token", form["grant_type"]);
        Assert.Equal("refresh+token/with=specials", form["refresh_token"]);
        Assert.DoesNotContain("code", form.Keys, StringComparer.Ordinal);
    }

    [Fact]
    public async Task SuccessfulResponseCarriesTheTokensAndTheirLifetime()
    {
        var handler = new StubHttpMessageHandler();

        handler.EnqueueJson(AuthTestSupport.TokenResponseJson(
            "access-3",
            refreshToken: "refresh-3",
            expiresIn: 7200,
            scopes: "pullrequest:write repository"));

        using var client = CreateClient(handler);

        var result = await client.RefreshAsync("refresh-2", TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Token);
        Assert.Equal("access-3", result.Token.AccessToken);
        Assert.Equal("refresh-3", result.Token.RefreshToken);
        Assert.Equal(7200, result.Token.ExpiresIn);
        Assert.Equal("pullrequest:write repository", result.Token.GrantedScopes);
        Assert.Equal("no failure", result.DescribeFailure());
    }

    /// <summary>Bitbucket has used both spellings; the client must read whichever arrived.</summary>
    [Fact]
    public async Task GrantedScopesFallBackToTheRfcSpelling()
    {
        var handler = new StubHttpMessageHandler();
        handler.EnqueueJson("""{"access_token":"access-4","expires_in":3600,"scope":"pullrequest"}""");

        using var client = CreateClient(handler);

        var result = await client.RefreshAsync("refresh-3", TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal("pullrequest", result.Token!.GrantedScopes);
    }

    [Theory]
    [InlineData("invalid_grant", true)]
    [InlineData("invalid_client", false)]
    [InlineData("unauthorized_client", false)]
    [InlineData("invalid_request", false)]
    [InlineData("invalid_grant_type", false)]
    public async Task ErrorCodesAreClassifiedByWhetherTheGrantItselfIsGone(string error, bool expectedInvalidGrant)
    {
        var handler = new StubHttpMessageHandler();

        handler.Enqueue(
            HttpStatusCode.BadRequest,
            AuthTestSupport.ErrorResponseJson(error, "the server explains itself"));

        using var client = CreateClient(handler);

        var result = await client.RefreshAsync("refresh-4", TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(expectedInvalidGrant, result.IsInvalidGrant);
        Assert.Equal(error, result.Error);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Equal($"HTTP 400 {error} (the server explains itself)", result.DescribeFailure());

        // A 400 is a deterministic answer: reissuing it would only spend rate-limit budget.
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ErrorWithoutADescriptionStillDescribesItself()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.BadRequest, AuthTestSupport.ErrorResponseJson("invalid_grant"));

        using var client = CreateClient(handler);

        var result = await client.RefreshAsync("refresh-5", TestContext.Current.CancellationToken);

        Assert.True(result.IsInvalidGrant);
        Assert.Equal("HTTP 400 invalid_grant", result.DescribeFailure());
    }

    /// <summary>
    /// Bitbucket answers the token endpoint with an HTML error page often enough that it cannot be
    /// an exception path: the status code and a collapsed excerpt are all there is to report, and
    /// the caller has to be able to put that in a message.
    /// </summary>
    [Fact]
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

        var result = await client.RefreshAsync("refresh-6", TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.False(result.IsInvalidGrant);
        Assert.Null(result.Error);
        Assert.Equal(HttpStatusCode.BadGateway, result.StatusCode);

        var failure = result.DescribeFailure();

        Assert.StartsWith("HTTP 502 (", failure, StringComparison.Ordinal);
        Assert.Contains("502 Bad Gateway", failure, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", failure, StringComparison.Ordinal);

        // 502 is transient, so the client retried the whole budget before giving up.
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task SuccessWithoutAnAccessTokenIsAFailure()
    {
        var handler = new StubHttpMessageHandler();
        handler.EnqueueJson("""{"token_type":"bearer","expires_in":7200}""");

        using var client = CreateClient(handler);

        var result = await client.RefreshAsync("refresh-7", TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.False(result.IsInvalidGrant);
        Assert.Contains("no access token", result.DescribeFailure(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SuccessWithABodyThatIsNotJsonIsAFailure()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, "<html>not json</html>", "text/html");

        using var client = CreateClient(handler);

        var result = await client.RefreshAsync("refresh-8", TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Contains("not valid JSON", result.DescribeFailure(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutConsumerCredentialsNothingIsSentAndTheReasonIsActionable()
    {
        var handler = new StubHttpMessageHandler();

        using var client = CreateClient(handler, consumerKey: null, consumerSecret: null);

        Assert.False(client.IsConfigured);

        var failure = await Assert.ThrowsAsync<AuthenticationRequiredException>(
            () => client.RefreshAsync("refresh-9", TestContext.Current.CancellationToken));

        Assert.Equal(AuthenticationRequiredReason.NotConfigured, failure.Reason);
        Assert.Contains("BITBUCKET_OAUTH_KEY", failure.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task HalfConfiguredConsumerCredentialsCountAsUnconfigured()
    {
        var handler = new StubHttpMessageHandler();

        using var client = CreateClient(handler, consumerSecret: null);

        Assert.False(client.IsConfigured);

        await Assert.ThrowsAsync<AuthenticationRequiredException>(
            () => client.ExchangeAuthorizationCodeAsync(
                "code",
                "http://127.0.0.1:33418/callback",
                TestContext.Current.CancellationToken));

        Assert.Empty(handler.Requests);
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
