using System.Net;
using System.Net.Http.Headers;
using System.Text;

using Bitbucket.Mcp.Authentication;
using Bitbucket.Mcp.Http;
using Bitbucket.Mcp.Tests.Http;
using Bitbucket.Mcp.Tools;

using ModelContextProtocol;

using Xunit;

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
    [Fact]
    public async Task DiffTooLargeIsAnsweredWithTheDiffstatRecipe()
    {
        using var handler = new StubHttpMessageHandler();
        handler.Enqueue(DiffTooLarge, "Diff too large");

        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            PullRequestReadTools.GetPullRequestDiffAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                7,
                Workspace,
                mode: "diff",
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("555", exception.Message, StringComparison.Ordinal);
        Assert.Contains("mode=diffstat", exception.Message, StringComparison.Ordinal);
        Assert.Contains("paths=[", exception.Message, StringComparison.Ordinal);
        Assert.Contains("getPullRequestDiff", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A 404 has to name what was looked for: the same number in another repository is a different
    /// pull request, and a private repository the token cannot see also answers 404.
    /// </summary>
    [Fact]
    public async Task NotFoundNamesTheWorkspaceRepositoryAndPullRequest()
    {
        using var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.NotFound, """{"type":"error","error":{"message":"Not found"}}""");

        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            PullRequestReadTools.GetPullRequestAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                42,
                Workspace,
                TestContext.Current.CancellationToken));

        Assert.Contains("404", exception.Message, StringComparison.Ordinal);
        Assert.Contains("pull request #42 in acme/widgets", exception.Message, StringComparison.Ordinal);
        Assert.Contains("bitbucket.org/{workspace}/{repository}", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A 403 has three plausible causes and the message has to name all three, because only the
    /// first is fixable by changing scopes: a missing scope (read and write do not imply each
    /// other), an API token sent as Bearer rather than Basic, or an account without write access.
    /// Both scope vocabularies appear — the OAuth consumer one and the <c>:bitbucket</c>-suffixed
    /// API token one — since the credential in use decides which applies.
    /// </summary>
    [Fact]
    public async Task ForbiddenListsBothScopeVocabulariesAndTheAuthSchemeToCheck()
    {
        using var handler = new StubHttpMessageHandler();
        handler.Enqueue(
            HttpStatusCode.Forbidden,
            """{"type":"error","error":{"message":"Your credentials lack one or more required privilege scopes"}}""");

        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            PullRequestWriteTools.UpdatePullRequestAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                42,
                Workspace,
                title: "New title",
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("403", exception.Message, StringComparison.Ordinal);
        Assert.Contains("pullrequest:write", exception.Message, StringComparison.Ordinal);
        Assert.Contains("repository:write", exception.Message, StringComparison.Ordinal);
        Assert.Contains("read:pullrequest:bitbucket", exception.Message, StringComparison.Ordinal);
        Assert.Contains("write:pullrequest:bitbucket", exception.Message, StringComparison.Ordinal);
        Assert.Contains("BITBUCKET_EMAIL + BITBUCKET_API_TOKEN", exception.Message, StringComparison.Ordinal);
        Assert.Contains("write access", exception.Message, StringComparison.Ordinal);

        // The endpoint-limitation folklore is gone: every pull request endpoint this server calls
        // accepts an API token, so the message must not send anyone back to OAuth to work around it.
        Assert.DoesNotContain("token-based authentication", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The credential is discarded once and retried once (<see cref="AuthenticationHandler"/>);
    /// a second rejection means the credential is wrong, not stale, and says so.
    /// </summary>
    [Fact]
    public async Task UnauthorizedRetriesOnceThenExplainsHowToReplaceTheCredential()
    {
        using var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.Unauthorized);
        handler.Enqueue(HttpStatusCode.Unauthorized);

        var credentials = new StubCredentialProvider();
        using var client = ToolTestHost.CreateClient(handler, credentials);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            PullRequestReadTools.GetPullRequestAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                42,
                Workspace,
                TestContext.Current.CancellationToken));

        Assert.Equal(1, credentials.InvalidateCount);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("401", exception.Message, StringComparison.Ordinal);
        Assert.Contains("bitbucket-mcp login", exception.Message, StringComparison.Ordinal);
        Assert.Contains("BITBUCKET_ACCESS_TOKEN", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The sign-in message is composed in one place so every tool says the same thing: the URL to
    /// open, the one-off CLI command, and the environment variables that skip OAuth entirely.
    /// </summary>
    [Fact]
    public async Task AuthenticationRequiredOffersTheAuthorizeUrlTheCliAndTheEnvironmentVariables()
    {
        const string AuthorizeUrl = "https://bitbucket.org/site/oauth2/authorize?client_id=abc&state=xyz";

        using var handler = new StubHttpMessageHandler();

        var credentials = new StubCredentialProvider(new AuthenticationRequiredException(
            AuthenticationRequiredReason.NoCachedToken,
            AuthorizeUrl));

        using var client = ToolTestHost.CreateClient(handler, credentials);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            PullRequestReadTools.ListPullRequestsAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                Workspace,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains(AuthorizeUrl, exception.Message, StringComparison.Ordinal);
        Assert.Contains("bitbucket-mcp login", exception.Message, StringComparison.Ordinal);
        Assert.Contains("BITBUCKET_ACCESS_TOKEN", exception.Message, StringComparison.Ordinal);
        Assert.Contains("BITBUCKET_EMAIL", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    /// <summary>
    /// Nothing configured at all: there is no authorize URL to offer, so the message must still be
    /// complete without one.
    /// </summary>
    [Fact]
    public async Task AuthenticationRequiredWithoutAnAuthorizeUrlStillExplainsEveryWayIn()
    {
        using var handler = new StubHttpMessageHandler();

        var credentials = new StubCredentialProvider(
            new AuthenticationRequiredException(AuthenticationRequiredReason.NotConfigured));

        using var client = ToolTestHost.CreateClient(handler, credentials);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            PullRequestReadTools.ListPullRequestsAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                Workspace,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("not configured", exception.Message, StringComparison.Ordinal);
        Assert.Contains("bitbucket-mcp login", exception.Message, StringComparison.Ordinal);
        Assert.Contains("BITBUCKET_ACCESS_TOKEN", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Open this URL", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A cursor is opaque on purpose. A model that edits or invents one gets told to stop doing
    /// that, and no request leaves the process.
    /// </summary>
    [Theory]
    [InlineData("not-a-cursor")]
    [InlineData("!!!not base64url!!!")]
    public async Task AnUndecodableCursorIsRejectedBeforeAnyRequest(string cursor)
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            PullRequestReadTools.ListPullRequestsAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                Workspace,
                cursor: cursor,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("Omit cursor", exception.Message, StringComparison.Ordinal);
        Assert.Contains("nextCursor", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    /// <summary>
    /// The SSRF guard, reached through a tool: a well-formed cursor pointing anywhere but the
    /// Bitbucket API is not a cursor, and the credential never reaches the other host.
    /// </summary>
    [Theory]
    [InlineData("https://evil.example/2.0/repositories/acme/widgets/pullrequests?page=2")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("https://api.bitbucket.org/internal/whatever")]
    public async Task ACursorPointingSomewhereElseIsRejectedBeforeAnyRequest(string url)
    {
        var cursor = Convert.ToBase64String(Encoding.UTF8.GetBytes(url))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            PullRequestReadTools.ListPullRequestsAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                Workspace,
                cursor: cursor,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("Omit cursor", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    /// <summary>
    /// Bitbucket may queue a large merge instead of performing it. Polling is out of scope, so the
    /// honest answer is "it may or may not have happened, go and look" — never a silent success and
    /// never an invitation to merge again.
    /// </summary>
    [Fact]
    public async Task AQueuedMergeIsReportedRatherThanTreatedAsSuccess()
    {
        using var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.Accepted, """{"task_status":"PENDING"}""");

        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            PullRequestWriteTools.MergePullRequestAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                42,
                Workspace,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("queued", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PENDING", exception.Message, StringComparison.Ordinal);
        Assert.Contains("does not poll", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Bitbucket UI", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A 429 that Bitbucket says to retry immediately is retried to the pipeline's limit; the
    /// message then has to say that waiting longer — not calling again — is the fix.
    /// </summary>
    [Fact]
    public async Task RateLimitingReportsHowManyRetriesWereAlreadySpent()
    {
        using var handler = new StubHttpMessageHandler();

        for (var i = 0; i < RetryHandler.MaxAttempts; i++)
        {
            handler.Enqueue(_ => Throttled(TimeSpan.Zero));
        }

        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            PullRequestReadTools.GetPullRequestAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                42,
                Workspace,
                TestContext.Current.CancellationToken));

        Assert.Equal(RetryHandler.MaxAttempts, handler.Requests.Count);
        Assert.Contains("429", exception.Message, StringComparison.Ordinal);
        Assert.Contains("already retried 3 time(s)", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Retry-After", exception.Message, StringComparison.Ordinal);
        Assert.Contains("mode=\"diffstat\"", exception.Message, StringComparison.Ordinal);

        // Retry-After: 0 is not a wait worth quoting — "try again in ~0s" is what the pipeline
        // already did three times — so this falls back to the generic advice.
        Assert.Contains("Wait about a minute", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("~0s", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Nothing to go on: a 429 with no <c>Retry-After</c> at all keeps the generic wait, because
    /// inventing a number would be worse than admitting there is none.
    /// </summary>
    [Fact]
    public async Task RateLimitingWithoutARetryAfterKeepsTheGenericWait()
    {
        using var handler = new StubHttpMessageHandler();

        handler.Fallback = _ => StubHttpMessageHandler.CreateResponse(
            HttpStatusCode.TooManyRequests,
            """{"type":"error","error":{"message":"Rate limit exceeded"}}""");

        // With no Retry-After the pipeline falls back to exponential backoff, so this is the one
        // case here that needs a fake clock rather than the real one.
        using var client = ToolTestHost.CreateClient(handler, timeProvider: new ManualTimeProvider());

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            PullRequestReadTools.GetPullRequestAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                42,
                Workspace,
                TestContext.Current.CancellationToken));

        Assert.Equal(RetryHandler.MaxAttempts, handler.Requests.Count);
        Assert.Contains("429", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Wait about a minute", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("try again in ~", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A <c>Retry-After</c> longer than the pipeline is willing to block for is not waited out: the
    /// request comes back unretried so the caller can be told, rather than a tool call sitting on a
    /// socket for two minutes.
    /// </summary>
    [Fact]
    public async Task ALongRetryAfterIsReportedInsteadOfWaitedOut()
    {
        using var handler = new StubHttpMessageHandler();
        handler.Enqueue(_ => Throttled(RetryHandler.MaxRetryAfter + TimeSpan.FromSeconds(60)));

        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            PullRequestReadTools.GetPullRequestAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                42,
                Workspace,
                TestContext.Current.CancellationToken));

        Assert.Single(handler.Requests);
        Assert.Contains("429", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("already retried", exception.Message, StringComparison.Ordinal);

        // The wait is Bitbucket's own number, not a guess: 120 s is what it asked for, and the
        // whole reason the request was not retried here.
        Assert.Contains("try again in ~120s", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Wait about a minute", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>A 400's field errors are the most useful part of it, so they are surfaced by name.</summary>
    [Fact]
    public async Task BadRequestSurfacesBitbucketsFieldErrors()
    {
        using var handler = new StubHttpMessageHandler();
        handler.Enqueue(
            HttpStatusCode.BadRequest,
            """
            {"type":"error","error":{"message":"Bad request","fields":{"title":["This field is required."]}}}
            """);

        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            PullRequestWriteTools.CreatePullRequestAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                "A title Bitbucket will reject",
                "feature/x",
                Workspace,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("400", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Field errors:", exception.Message, StringComparison.Ordinal);
        Assert.Contains("title: This field is required.", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Fix the named arguments", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>A conflicting merge is a git problem, and the message says where to go and fix it.</summary>
    [Fact]
    public async Task ConflictExplainsThatTheBranchesHaveToBeReconciledFirst()
    {
        using var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.Conflict, """{"type":"error","error":{"message":"merge conflict"}}""");

        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            PullRequestWriteTools.MergePullRequestAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                42,
                Workspace,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("409", exception.Message, StringComparison.Ordinal);
        Assert.Contains("conflict", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("getPullRequest", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>A 5xx is nobody's argument's fault, and the message must not send the model editing them.</summary>
    [Fact]
    public async Task ServerErrorsSayNothingInTheRequestNeedsChanging()
    {
        using var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.InternalServerError, "<html>oops</html>", "text/html");

        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            PullRequestReadTools.GetPullRequestCommentsAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                42,
                Workspace,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("HTTP 500", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Nothing in the request needs changing", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An unresolvable inline anchor is the resolver's own message, passed through unchanged: it
    /// already lists the candidate lines, which is what turns a failed guess into a retry.
    /// </summary>
    [Fact]
    public async Task AnUnmatchedCodeSnippetKeepsTheResolversCandidateList()
    {
        using var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, ToolFixtures.SingleFileDiff, "text/plain");

        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            PullRequestWriteTools.AddPullRequestCommentAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                42,
                "Looks wrong",
                Workspace,
                path: "src/Widget.cs",
                codeSnippet: "var somethingThatIsNotInTheDiff = 1;",
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("No line in the diff of src/Widget.cs matches", exception.Message, StringComparison.Ordinal);
        Assert.Contains("lineType=", exception.Message, StringComparison.Ordinal);

        // The comment was never posted: only the per-file diff was fetched.
        Assert.Single(handler.Requests);
    }

    /// <summary>
    /// A workspace or repository of <c>.</c> or <c>..</c> is refused by the request builder, before
    /// dot-segment removal can walk the URL out of the <c>/2.0/</c> prefix. It reaches the caller as
    /// an argument error rather than as "Unexpected error: ArgumentException", because it is the
    /// caller's to fix.
    /// </summary>
    [Theory]
    [InlineData("..", "widgets")]
    [InlineData("acme", ".")]
    public async Task ADotSegmentSlugIsAnArgumentErrorRatherThanAnInternalFailure(string workspace, string repository)
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            PullRequestReadTools.ListPullRequestsAsync(
                client,
                ToolTestHost.CreateOptions(),
                repository,
                workspace,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("Invalid argument", exception.Message, StringComparison.Ordinal);
        Assert.Contains("must be real slugs", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Unexpected error", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    /// <summary>
    /// Cancellation is the client's decision, not an error: the SDK has its own path for it, and
    /// wrapping it in an <see cref="McpException"/> would report a failure that did not happen.
    /// </summary>
    [Fact]
    public async Task CancellationIsRethrownRatherThanTurnedIntoAnError()
    {
        using var handler = new StubHttpMessageHandler();
        handler.Fallback = _ => StubHttpMessageHandler.CreateResponse(HttpStatusCode.OK, "{}");

        using var client = ToolTestHost.CreateClient(handler);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
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
