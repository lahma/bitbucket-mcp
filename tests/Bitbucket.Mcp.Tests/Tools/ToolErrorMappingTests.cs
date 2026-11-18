using System.Net;
using System.Net.Http.Headers;
using System.Text;

using Bitbucket.Mcp.Authentication;
using Bitbucket.Mcp.Http;
using Bitbucket.Mcp.Tests.Http;
using Bitbucket.Mcp.Tools;

using ModelContextProtocol;


namespace Bitbucket.Mcp.Tests.Tools;

/// <summary>
/// What a failing tool call tells the model.
/// </summary>
/// <remarks>
/// <para>
/// Only <see cref="McpException"/> may escape a tool method — anything else reaches the client as
/// the SDK's generic "An error occurred", which throws away the single chance to tell a model
/// mid-task what to do differently. These tests assert the funnel's translations, not its
/// prose: each one looks for the actionable substring (the next call to make, the parameter to
/// change, the environment variable to set) rather than the whole message.
/// </para>
/// <para>
/// Every case is driven end-to-end through <see cref="BitbucketApiClient"/> over a stub transport,
/// so the retry handler, the 401 refresh and the status-to-advice mapping are all exercised the way
/// a real call would exercise them.
/// </para>
/// </remarks>
public class ToolErrorMappingTests
{
    private const string Workspace = "acme";
    private const string Repository = "widgets";

    /// <summary>Bitbucket's non-standard "the diff is too big to build" status.</summary>
    private const HttpStatusCode DiffTooLarge = (HttpStatusCode) 555;

    /// <summary>
    /// The whole point of the diffstat-first rule: a 555 must come back as the recipe for getting
    /// the diff anyway, not as a status code.
    /// </summary>
    [Test]
    public async Task DiffTooLargeIsAnsweredWithTheDiffstatRecipe()
    {
        using var handler = new StubHttpMessageHandler();
        handler.Enqueue(DiffTooLarge, "Diff too large");

        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Expect.ThrowsAsync<McpException>(() =>
            PullRequestReadTools.GetPullRequestDiffAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                7,
                Workspace,
                mode: "diff",
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception.Message, Does.Contain("555"));
        Assert.That(exception.Message, Does.Contain("mode=diffstat"));
        Assert.That(exception.Message, Does.Contain("paths=["));
        Assert.That(exception.Message, Does.Contain("getPullRequestDiff"));
    }

    /// <summary>
    /// A 404 has to name what was looked for: the same number in another repository is a different
    /// pull request, and a private repository the token cannot see also answers 404.
    /// </summary>
    [Test]
    public async Task NotFoundNamesTheWorkspaceRepositoryAndPullRequest()
    {
        using var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.NotFound, """{"type":"error","error":{"message":"Not found"}}""");

        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Expect.ThrowsAsync<McpException>(() =>
            PullRequestReadTools.GetPullRequestAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                42,
                Workspace,
                TestContext.CurrentContext.CancellationToken));

        Assert.That(exception.Message, Does.Contain("404"));
        Assert.That(exception.Message, Does.Contain("pull request #42 in acme/widgets"));
        Assert.That(exception.Message, Does.Contain("bitbucket.org/{workspace}/{repository}"));
    }

    /// <summary>
    /// A repository-scoped tool has no pull request to name, so the target it reports is the
    /// repository — naming a pull request that was never part of the call would send the reader
    /// looking in the wrong place.
    /// </summary>
    [Test]
    public async Task ARepositoryScopedToolNamesTheRepositoryRatherThanAPullRequest()
    {
        using var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.NotFound, """{"type":"error","error":{"message":"Not found"}}""");

        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Expect.ThrowsAsync<McpException>(() =>
            PullRequestReadTools.ListDefaultReviewersAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                Workspace,
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception.Message, Does.Contain("404"));
        Assert.That(exception.Message, Does.Contain("repository acme/widgets"));
        Assert.That(exception.Message, Does.Not.Contain("pull request #"));
    }

    /// <summary>
    /// A 403 has three plausible causes and the message has to name all three, because only the
    /// first is fixable by changing scopes: a missing scope (read and write do not imply each
    /// other), an API token sent as Bearer rather than Basic, or an account without write access.
    /// Both scope vocabularies appear — the OAuth consumer one and the <c>:bitbucket</c>-suffixed
    /// API token one — since the credential in use decides which applies.
    /// </summary>
    [Test]
    public async Task ForbiddenListsBothScopeVocabulariesAndTheAuthSchemeToCheck()
    {
        using var handler = new StubHttpMessageHandler();
        handler.Enqueue(
            HttpStatusCode.Forbidden,
            """{"type":"error","error":{"message":"Your credentials lack one or more required privilege scopes"}}""");

        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Expect.ThrowsAsync<McpException>(() =>
            PullRequestWriteTools.UpdatePullRequestAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                42,
                Workspace,
                title: "New title",
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception.Message, Does.Contain("403"));
        Assert.That(exception.Message, Does.Contain("pullrequest:write"));
        Assert.That(exception.Message, Does.Contain("repository:write"));
        Assert.That(exception.Message, Does.Contain("read:pullrequest:bitbucket"));
        Assert.That(exception.Message, Does.Contain("write:pullrequest:bitbucket"));
        Assert.That(exception.Message, Does.Contain("BITBUCKET_EMAIL + BITBUCKET_API_TOKEN"));
        Assert.That(exception.Message, Does.Contain("write access"));

        // The endpoint-limitation folklore is gone: every pull request endpoint this server calls
        // accepts an API token, so the message must not send anyone back to OAuth to work around it.
        Assert.That(exception.Message, Does.Not.Contain("token-based authentication"));
    }

    /// <summary>
    /// The credential is discarded once and retried once (<see cref="AuthenticationHandler"/>);
    /// a second rejection means the credential is wrong, not stale, and says so.
    /// </summary>
    [Test]
    public async Task UnauthorizedRetriesOnceThenExplainsHowToReplaceTheCredential()
    {
        using var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.Unauthorized);
        handler.Enqueue(HttpStatusCode.Unauthorized);

        var credentials = new StubCredentialProvider();
        using var client = ToolTestHost.CreateClient(handler, credentials);

        var exception = await Expect.ThrowsAsync<McpException>(() =>
            PullRequestReadTools.GetPullRequestAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                42,
                Workspace,
                TestContext.CurrentContext.CancellationToken));

        Assert.That(credentials.InvalidateCount, Is.EqualTo(1));
        Assert.That(handler.Requests.Count, Is.EqualTo(2));
        Assert.That(exception.Message, Does.Contain("401"));
        Assert.That(exception.Message, Does.Contain("bitbucket-mcp login"));
        Assert.That(exception.Message, Does.Contain("BITBUCKET_ACCESS_TOKEN"));
    }

    /// <summary>
    /// The sign-in message is composed in one place so every tool says the same thing: the URL to
    /// open, the one-off CLI command, and the environment variables that skip OAuth entirely.
    /// </summary>
    [Test]
    public async Task AuthenticationRequiredOffersTheAuthorizeUrlTheCliAndTheEnvironmentVariables()
    {
        const string AuthorizeUrl = "https://bitbucket.org/site/oauth2/authorize?client_id=abc&state=xyz";

        using var handler = new StubHttpMessageHandler();

        var credentials = new StubCredentialProvider(new AuthenticationRequiredException(
            AuthenticationRequiredReason.NoCachedToken,
            AuthorizeUrl));

        using var client = ToolTestHost.CreateClient(handler, credentials);

        var exception = await Expect.ThrowsAsync<McpException>(() =>
            PullRequestReadTools.ListPullRequestsAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                Workspace,
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception.Message, Does.Contain(AuthorizeUrl));
        Assert.That(exception.Message, Does.Contain("bitbucket-mcp login"));
        Assert.That(exception.Message, Does.Contain("BITBUCKET_ACCESS_TOKEN"));
        Assert.That(exception.Message, Does.Contain("BITBUCKET_EMAIL"));
        Assert.That(handler.Requests, Is.Empty);
    }

    /// <summary>
    /// Nothing configured at all: there is no authorize URL to offer, so the message must still be
    /// complete without one.
    /// </summary>
    [Test]
    public async Task AuthenticationRequiredWithoutAnAuthorizeUrlStillExplainsEveryWayIn()
    {
        using var handler = new StubHttpMessageHandler();

        var credentials = new StubCredentialProvider(
            new AuthenticationRequiredException(AuthenticationRequiredReason.NotConfigured));

        using var client = ToolTestHost.CreateClient(handler, credentials);

        var exception = await Expect.ThrowsAsync<McpException>(() =>
            PullRequestReadTools.ListPullRequestsAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                Workspace,
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception.Message, Does.Contain("not configured"));
        Assert.That(exception.Message, Does.Contain("bitbucket-mcp login"));
        Assert.That(exception.Message, Does.Contain("BITBUCKET_ACCESS_TOKEN"));
        Assert.That(exception.Message, Does.Not.Contain("Open this URL"));
    }

    /// <summary>
    /// A cursor is opaque on purpose. A model that edits or invents one gets told to stop doing
    /// that, and no request leaves the process.
    /// </summary>
    [TestCase("not-a-cursor")]
    [TestCase("!!!not base64url!!!")]
    public async Task AnUndecodableCursorIsRejectedBeforeAnyRequest(string cursor)
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Expect.ThrowsAsync<McpException>(() =>
            PullRequestReadTools.ListPullRequestsAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                Workspace,
                cursor: cursor,
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception.Message, Does.Contain("Omit cursor"));
        Assert.That(exception.Message, Does.Contain("nextCursor"));
        Assert.That(handler.Requests, Is.Empty);
    }

    /// <summary>
    /// The SSRF guard, reached through a tool: a well-formed cursor pointing anywhere but the
    /// Bitbucket API is not a cursor, and the credential never reaches the other host.
    /// </summary>
    [TestCase("https://evil.example/2.0/repositories/acme/widgets/pullrequests?page=2")]
    [TestCase("http://169.254.169.254/latest/meta-data/")]
    [TestCase("https://api.bitbucket.org/internal/whatever")]
    public async Task ACursorPointingSomewhereElseIsRejectedBeforeAnyRequest(string url)
    {
        var cursor = Convert.ToBase64String(Encoding.UTF8.GetBytes(url))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Expect.ThrowsAsync<McpException>(() =>
            PullRequestReadTools.ListPullRequestsAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                Workspace,
                cursor: cursor,
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception.Message, Does.Contain("Omit cursor"));
        Assert.That(handler.Requests, Is.Empty);
    }

    /// <summary>
    /// Bitbucket may queue a large merge instead of performing it. Polling is out of scope, so the
    /// honest answer is "it may or may not have happened, go and look" — never a silent success and
    /// never an invitation to merge again.
    /// </summary>
    [Test]
    public async Task AQueuedMergeIsReportedRatherThanTreatedAsSuccess()
    {
        using var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.Accepted, """{"task_status":"PENDING"}""");

        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Expect.ThrowsAsync<McpException>(() =>
            PullRequestWriteTools.MergePullRequestAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                42,
                Workspace,
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception.Message, Does.Contain("queued").IgnoreCase);
        Assert.That(exception.Message, Does.Contain("PENDING"));
        Assert.That(exception.Message, Does.Contain("does not poll"));
        Assert.That(exception.Message, Does.Contain("Bitbucket UI"));
    }

    /// <summary>
    /// A 429 that Bitbucket says to retry immediately is retried to the pipeline's limit; the
    /// message then has to say that waiting longer — not calling again — is the fix.
    /// </summary>
    [Test]
    public async Task RateLimitingReportsHowManyRetriesWereAlreadySpent()
    {
        using var handler = new StubHttpMessageHandler();

        for (var i = 0; i < RetryHandler.MaxAttempts; i++)
        {
            handler.Enqueue(_ => Throttled(TimeSpan.Zero));
        }

        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Expect.ThrowsAsync<McpException>(() =>
            PullRequestReadTools.GetPullRequestAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                42,
                Workspace,
                TestContext.CurrentContext.CancellationToken));

        Assert.That(handler.Requests.Count, Is.EqualTo(RetryHandler.MaxAttempts));
        Assert.That(exception.Message, Does.Contain("429"));
        Assert.That(exception.Message, Does.Contain("already retried 3 time(s)"));
        Assert.That(exception.Message, Does.Contain("Retry-After"));
        Assert.That(exception.Message, Does.Contain("mode=\"diffstat\""));

        // Retry-After: 0 is not a wait worth quoting — "try again in ~0s" is what the pipeline
        // already did three times — so this falls back to the generic advice.
        Assert.That(exception.Message, Does.Contain("Wait about a minute"));
        Assert.That(exception.Message, Does.Not.Contain("~0s"));
    }

    /// <summary>
    /// Nothing to go on: a 429 with no <c>Retry-After</c> at all keeps the generic wait, because
    /// inventing a number would be worse than admitting there is none.
    /// </summary>
    [Test]
    public async Task RateLimitingWithoutARetryAfterKeepsTheGenericWait()
    {
        using var handler = new StubHttpMessageHandler();

        handler.Fallback = _ => StubHttpMessageHandler.CreateResponse(
            HttpStatusCode.TooManyRequests,
            """{"type":"error","error":{"message":"Rate limit exceeded"}}""");

        // With no Retry-After the pipeline falls back to exponential backoff, so this is the one
        // case here that needs a fake clock rather than the real one.
        using var client = ToolTestHost.CreateClient(handler, timeProvider: new ManualTimeProvider());

        var exception = await Expect.ThrowsAsync<McpException>(() =>
            PullRequestReadTools.GetPullRequestAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                42,
                Workspace,
                TestContext.CurrentContext.CancellationToken));

        Assert.That(handler.Requests.Count, Is.EqualTo(RetryHandler.MaxAttempts));
        Assert.That(exception.Message, Does.Contain("429"));
        Assert.That(exception.Message, Does.Contain("Wait about a minute"));
        Assert.That(exception.Message, Does.Not.Contain("try again in ~"));
    }

    /// <summary>
    /// A <c>Retry-After</c> longer than the pipeline is willing to block for is not waited out: the
    /// request comes back unretried so the caller can be told, rather than a tool call sitting on a
    /// socket for two minutes.
    /// </summary>
    [Test]
    public async Task ALongRetryAfterIsReportedInsteadOfWaitedOut()
    {
        using var handler = new StubHttpMessageHandler();
        handler.Enqueue(_ => Throttled(RetryHandler.MaxRetryAfter + TimeSpan.FromSeconds(60)));

        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Expect.ThrowsAsync<McpException>(() =>
            PullRequestReadTools.GetPullRequestAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                42,
                Workspace,
                TestContext.CurrentContext.CancellationToken));

        Assert.That(handler.Requests, Has.Exactly(1).Items);
        Assert.That(exception.Message, Does.Contain("429"));
        Assert.That(exception.Message, Does.Not.Contain("already retried"));

        // The wait is Bitbucket's own number, not a guess: 120 s is what it asked for, and the
        // whole reason the request was not retried here.
        Assert.That(exception.Message, Does.Contain("try again in ~120s"));
        Assert.That(exception.Message, Does.Not.Contain("Wait about a minute"));
    }

    /// <summary>A 400's field errors are the most useful part of it, so they are surfaced by name.</summary>
    [Test]
    public async Task BadRequestSurfacesBitbucketsFieldErrors()
    {
        using var handler = new StubHttpMessageHandler();
        handler.Enqueue(
            HttpStatusCode.BadRequest,
            """
            {"type":"error","error":{"message":"Bad request","fields":{"title":["This field is required."]}}}
            """);

        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Expect.ThrowsAsync<McpException>(() =>
            PullRequestWriteTools.CreatePullRequestAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                "A title Bitbucket will reject",
                "feature/x",
                Workspace,
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception.Message, Does.Contain("400"));
        Assert.That(exception.Message, Does.Contain("Field errors:"));
        Assert.That(exception.Message, Does.Contain("title: This field is required."));
        Assert.That(exception.Message, Does.Contain("Fix the named arguments"));
    }

    /// <summary>A conflicting merge is a git problem, and the message says where to go and fix it.</summary>
    [Test]
    public async Task ConflictExplainsThatTheBranchesHaveToBeReconciledFirst()
    {
        using var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.Conflict, """{"type":"error","error":{"message":"merge conflict"}}""");

        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Expect.ThrowsAsync<McpException>(() =>
            PullRequestWriteTools.MergePullRequestAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                42,
                Workspace,
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception.Message, Does.Contain("409"));
        Assert.That(exception.Message, Does.Contain("conflict").IgnoreCase);
        Assert.That(exception.Message, Does.Contain("getPullRequest"));
    }

    /// <summary>A 5xx is nobody's argument's fault, and the message must not send the model editing them.</summary>
    [Test]
    public async Task ServerErrorsSayNothingInTheRequestNeedsChanging()
    {
        using var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.InternalServerError, "<html>oops</html>", "text/html");

        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Expect.ThrowsAsync<McpException>(() =>
            PullRequestReadTools.GetPullRequestCommentsAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                42,
                Workspace,
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception.Message, Does.Contain("HTTP 500"));
        Assert.That(exception.Message, Does.Contain("Nothing in the request needs changing"));
    }

    /// <summary>
    /// An unresolvable inline anchor is the resolver's own message, passed through unchanged: it
    /// already lists the candidate lines, which is what turns a failed guess into a retry.
    /// </summary>
    [Test]
    public async Task AnUnmatchedCodeSnippetKeepsTheResolversCandidateList()
    {
        using var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, ToolFixtures.SingleFileDiff, "text/plain");

        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Expect.ThrowsAsync<McpException>(() =>
            PullRequestWriteTools.AddPullRequestCommentAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                42,
                "Looks wrong",
                Workspace,
                path: "src/Widget.cs",
                codeSnippet: "var somethingThatIsNotInTheDiff = 1;",
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception.Message, Does.Contain("No line in the diff of src/Widget.cs matches"));
        Assert.That(exception.Message, Does.Contain("lineType="));

        // The comment was never posted: only the per-file diff was fetched.
        Assert.That(handler.Requests, Has.Exactly(1).Items);
    }

    /// <summary>
    /// A workspace or repository of <c>.</c> or <c>..</c> is refused by the request builder, before
    /// dot-segment removal can walk the URL out of the <c>/2.0/</c> prefix. It reaches the caller as
    /// an argument error rather than as "Unexpected error: ArgumentException", because it is the
    /// caller's to fix.
    /// </summary>
    [TestCase("..", "widgets")]
    [TestCase("acme", ".")]
    public async Task ADotSegmentSlugIsAnArgumentErrorRatherThanAnInternalFailure(string workspace, string repository)
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Expect.ThrowsAsync<McpException>(() =>
            PullRequestReadTools.ListPullRequestsAsync(
                client,
                ToolTestHost.CreateOptions(),
                repository,
                workspace,
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception.Message, Does.Contain("Invalid argument"));
        Assert.That(exception.Message, Does.Contain("must be real slugs"));
        Assert.That(exception.Message, Does.Not.Contain("Unexpected error"));
        Assert.That(handler.Requests, Is.Empty);
    }

    /// <summary>
    /// Cancellation is the client's decision, not an error: the SDK has its own path for it, and
    /// wrapping it in an <see cref="McpException"/> would report a failure that did not happen.
    /// </summary>
    [Test]
    public async Task CancellationIsRethrownRatherThanTurnedIntoAnError()
    {
        using var handler = new StubHttpMessageHandler();
        handler.Fallback = _ => StubHttpMessageHandler.CreateResponse(HttpStatusCode.OK, "{}");

        using var client = ToolTestHost.CreateClient(handler);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.CatchAsync<OperationCanceledException>(() =>
            PullRequestReadTools.GetPullRequestAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                42,
                Workspace,
                cancelled.Token));
    }

    /// <summary>A 429 carrying the <c>Retry-After</c> Bitbucket asked for.</summary>
    private static HttpResponseMessage Throttled(TimeSpan retryAfter)
    {
        var response = StubHttpMessageHandler.CreateResponse(
            HttpStatusCode.TooManyRequests,
            """{"type":"error","error":{"message":"Rate limit exceeded"}}""");

        response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter);
        return response;
    }
}
