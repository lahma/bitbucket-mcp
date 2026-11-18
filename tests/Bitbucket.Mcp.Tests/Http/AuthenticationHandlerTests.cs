using System.Net;
using System.Net.Http.Headers;

using Bitbucket.Mcp.Http;

using Microsoft.Extensions.Logging.Abstractions;


namespace Bitbucket.Mcp.Tests.Http;

/// <summary>
/// Covers <see cref="AuthenticationHandler"/> directly, above a stub transport: what it attaches,
/// and how it follows the redirects the transport is no longer allowed to follow itself (D16).
/// </summary>
/// <remarks>
/// <para>
/// The regression this suite exists for: Bitbucket answers <c>…/diff</c> and <c>…/diffstat</c> with
/// a <c>302</c> to another path on <c>api.bitbucket.org</c> whose target still requires the
/// credential, and <c>SocketsHttpHandler</c> strips <c>Authorization</c> on <em>every</em>
/// automatic redirect. Both diff modes came back <c>404</c> against a live private repository until
/// the hop was taken here, with the header attached again.
/// </para>
/// <para>
/// The other half is the guard that makes that safe: the target of a redirect is chosen by the
/// server, so anything that is not <c>https</c> on the API host is fetched without the credential.
/// </para>
/// </remarks>
public class AuthenticationHandlerTests
{
    private const string ApiUri = "https://api.bitbucket.org/2.0/repositories/acme/widget-api/pullrequests/412/diff";
    private const string ApiTarget = "https://api.bitbucket.org/2.0/repositories/acme/widget-api/diff-content/abc123";
    private const string ForeignTarget = "https://example.com/diffs/abc123";

    /// <summary>Every status this handler treats as a redirect.</summary>
    public static int[] RedirectStatuses =>
    [
        301,
        302,
        303,
        307,
        308,
    ];

    [TestCaseSource(nameof(RedirectStatuses))]
    public async Task FollowsARedirectToTheApiHostWithTheSameCredential(int status)
    {
        var (client, stub, credentials) = CreateClient();

        using (client)
        {
            stub.Enqueue(_ => StubHttpMessageHandler.CreateRedirect((HttpStatusCode) status, ApiTarget));
            stub.Enqueue(HttpStatusCode.OK, "diff --git a/x b/x\n", "text/plain");

            using var response = await SendAsync(client);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(stub.Requests.Count, Is.EqualTo(2));

            // THE regression test: the redirected request is what actually returns the diff, and
            // Bitbucket answers it with a 404 unless it carries the credential.
            Assert.That(stub.Requests[1].Uri?.AbsoluteUri, Is.EqualTo(ApiTarget));
            Assert.That(stub.Requests[1].Headers["Authorization"], Is.EqualTo("Bearer token-1"));

            // Same credential, not a fresh one: a hop is not a reason to touch the token store.
            Assert.That(stub.Requests[0].Headers["Authorization"], Is.EqualTo("Bearer token-1"));
            Assert.That(credentials.HeaderRequestCount, Is.EqualTo(1));
            Assert.That(credentials.InvalidateCount, Is.EqualTo(0));
        }
    }

    [Test]
    public async Task FollowsARedirectToAnotherHostWithoutTheCredential()
    {
        var (client, stub, _) = CreateClient();

        using (client)
        {
            stub.Enqueue(_ => StubHttpMessageHandler.CreateRedirect(HttpStatusCode.Found, ForeignTarget));
            stub.Enqueue(HttpStatusCode.OK, "diff --git a/x b/x\n", "text/plain");

            using var response = await SendAsync(client);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(stub.Requests.Count, Is.EqualTo(2));
            Assert.That(stub.Requests[1].Uri?.AbsoluteUri, Is.EqualTo(ForeignTarget));

            // The target of a redirect is the server's choice; handing a live Bitbucket credential
            // to whatever host it names is the one mistake this must not make.
            Assert.That(stub.Requests[1].Headers.ContainsKey("Authorization"), Is.False);
        }
    }

    [TestCase("http://api.bitbucket.org/2.0/diff-content/abc123")]
    [TestCase("https://api.bitbucket.org.evil.example/2.0/diff-content/abc123")]
    [TestCase("https://evil.example/api.bitbucket.org/diff")]
    public async Task OnlyHttpsOnTheApiHostCountsAsBitbucket(string target)
    {
        var (client, stub, _) = CreateClient();

        using (client)
        {
            stub.Enqueue(_ => StubHttpMessageHandler.CreateRedirect(HttpStatusCode.Found, target));
            stub.Enqueue(HttpStatusCode.OK, "{}");

            using var response = await SendAsync(client);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(stub.Requests[1].Headers.ContainsKey("Authorization"), Is.False);
        }
    }

    [Test]
    public async Task ResolvesARelativeLocationAgainstTheRequestUri()
    {
        var (client, stub, _) = CreateClient();

        using (client)
        {
            stub.Enqueue(_ => StubHttpMessageHandler.CreateRedirect(
                HttpStatusCode.Found,
                "/2.0/repositories/acme/widget-api/diff-content/abc123"));
            stub.Enqueue(HttpStatusCode.OK, "diff --git a/x b/x\n", "text/plain");

            using var response = await SendAsync(client);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            // Relative is the form Bitbucket actually sends; resolved, it is still the API host, so
            // the credential goes with it.
            Assert.That(stub.Requests[1].Uri?.AbsoluteUri, Is.EqualTo(ApiTarget));
            Assert.That(stub.Requests[1].Headers["Authorization"], Is.EqualTo("Bearer token-1"));
        }
    }

    [Test]
    public async Task CarriesTheRequestHeadersAcrossTheHop()
    {
        var (client, stub, _) = CreateClient();

        using (client)
        {
            stub.Enqueue(_ => StubHttpMessageHandler.CreateRedirect(HttpStatusCode.Found, ApiTarget));
            stub.Enqueue(HttpStatusCode.OK, "diff --git a/x b/x\n", "text/plain");

            using var request = new HttpRequestMessage(HttpMethod.Get, ApiUri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));

            using var response = await client.SendAsync(request, TestContext.CurrentContext.CancellationToken);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            // HttpClient merges its default headers in once, before the chain runs; a request the
            // chain issues itself gets nothing for free, so the diff endpoint would silently ask
            // for JSON on the hop that actually returns the diff.
            Assert.That(stub.Requests[1].Headers["Accept"], Is.EqualTo("text/plain"));
        }
    }

    [Test]
    public async Task StopsAfterFiveHopsAndSaysWhereItWasSent()
    {
        var (client, stub, _) = CreateClient();

        using (client)
        {
            // A redirect pointing at itself: the shape a misconfigured proxy actually produces.
            stub.Fallback = _ => StubHttpMessageHandler.CreateRedirect(HttpStatusCode.Found, ApiTarget);

            var exception = await Expect.ThrowsAsync<BitbucketApiException>(() => SendAsync(client));

            Assert.That(exception.StatusCode, Is.EqualTo(HttpStatusCode.Found));
            Assert.That(exception.Message, Does.Contain("Too many redirects"));
            Assert.That(exception.Message, Does.Contain(ApiTarget));

            // The original request plus exactly MaxRedirects hops, and then it gives up.
            Assert.That(stub.Requests.Count, Is.EqualTo(AuthenticationHandler.MaxRedirects + 1));
        }
    }

    [Test]
    public async Task FollowsAChainOfExactlyFiveHops()
    {
        var (client, stub, _) = CreateClient();

        using (client)
        {
            for (var hop = 0; hop < AuthenticationHandler.MaxRedirects; hop++)
            {
                stub.Enqueue(_ => StubHttpMessageHandler.CreateRedirect(HttpStatusCode.Found, ApiTarget));
            }

            stub.Enqueue(HttpStatusCode.OK, "{}");

            using var response = await SendAsync(client);

            // Five is the budget, not the point at which it starts refusing.
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(stub.Requests.Count, Is.EqualTo(AuthenticationHandler.MaxRedirects + 1));
        }
    }

    [Test]
    public async Task NeverFollowsARedirectOnARequestWithABody()
    {
        var (client, stub, _) = CreateClient();

        using (client)
        {
            stub.Fallback = _ => StubHttpMessageHandler.CreateRedirect(HttpStatusCode.Found, ApiTarget);

            using var request = new HttpRequestMessage(HttpMethod.Post, ApiUri)
            {
                Content = new ByteArrayContent("{}"u8.ToArray()),
            };

            using var response = await client.SendAsync(request, TestContext.CurrentContext.CancellationToken);

            // Replaying a write after a redirect is how a comment gets posted twice; the 302 comes
            // back up instead and the client turns it into an error.
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Found));
            Assert.That(stub.Requests, Has.Exactly(1).Items);
        }
    }

    [Test]
    public async Task ReturnsARedirectThatNamesNoLocation()
    {
        var (client, stub, _) = CreateClient();

        using (client)
        {
            stub.Enqueue(HttpStatusCode.Found);

            using var response = await SendAsync(client);

            // Nothing to follow and nothing to guess at: the status travels up as it is.
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Found));
            Assert.That(stub.Requests, Has.Exactly(1).Items);
        }
    }

    [Test]
    public async Task DoesNotTreatANonRedirectStatusAsOne()
    {
        var (client, stub, _) = CreateClient();

        using (client)
        {
            // 304 sits between the redirect codes and is not one; it must not be followed.
            stub.Enqueue(_ => StubHttpMessageHandler.CreateRedirect(HttpStatusCode.NotModified, ApiTarget));

            using var response = await SendAsync(client);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotModified));
            Assert.That(stub.Requests, Has.Exactly(1).Items);
        }
    }

    /// <summary>
    /// The 401 path runs first and the redirect chain starts from whatever it left behind, so a
    /// credential refreshed on a 401 is the one that reaches the redirect target.
    /// </summary>
    [Test]
    public async Task ARefreshedCredentialIsTheOneThatFollowsTheRedirect()
    {
        var (client, stub, credentials) = CreateClient("stale-token", "fresh-token");

        using (client)
        {
            stub.Enqueue(HttpStatusCode.Unauthorized, """{"type":"error","error":{"message":"Invalid token"}}""");
            stub.Enqueue(_ => StubHttpMessageHandler.CreateRedirect(HttpStatusCode.Found, ApiTarget));
            stub.Enqueue(HttpStatusCode.OK, "diff --git a/x b/x\n", "text/plain");

            using var response = await SendAsync(client);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(stub.Requests.Count, Is.EqualTo(3));

            Assert.That(stub.Requests[0].Headers["Authorization"], Is.EqualTo("Bearer stale-token"));
            Assert.That(stub.Requests[1].Headers["Authorization"], Is.EqualTo("Bearer fresh-token"));
            Assert.That(stub.Requests[2].Headers["Authorization"], Is.EqualTo("Bearer fresh-token"));

            Assert.That(credentials.InvalidateCount, Is.EqualTo(1));
            Assert.That(credentials.HeaderRequestCount, Is.EqualTo(2));
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiUri);

        return await client.SendAsync(request, TestContext.CurrentContext.CancellationToken);
    }

    private static (HttpClient Client, StubHttpMessageHandler Stub, StubCredentialProvider Credentials) CreateClient(
        params string[] tokens)
    {
        var stub = new StubHttpMessageHandler();
        var credentials = new StubCredentialProvider(tokens);

        var handler = new AuthenticationHandler(credentials, NullLogger.Instance)
        {
            InnerHandler = stub,
        };

        return (new HttpClient(handler, disposeHandler: true), stub, credentials);
    }
}
