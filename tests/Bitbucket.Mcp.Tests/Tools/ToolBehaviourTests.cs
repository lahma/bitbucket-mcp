using System.Net;
using System.Text.Json;

using Bitbucket.Mcp.Http;
using Bitbucket.Mcp.Tools;
using Bitbucket.Mcp.Tools.Models;

using ModelContextProtocol;


namespace Bitbucket.Mcp.Tests.Tools;

/// <summary>
/// What the tools actually do with their arguments: the requests they compose, the argument
/// validation they refuse to send, and the shape of what comes back.
/// </summary>
/// <remarks>
/// Everything runs through the real <see cref="BitbucketApiClient"/> over a stub transport, so
/// these tests see the same URLs, bodies and JSON an MCP client's call would produce. The result
/// assertions serialise through <see cref="BitbucketToolJsonContext"/> — the contract the client
/// receives — rather than reading properties, because "no links, no type echoes, no nulls" is a
/// statement about the JSON and not about the record.
/// </remarks>
public class ToolBehaviourTests
{
    private const string Workspace = "acme";
    private const string Repository = "widgets";

    // ---------------------------------------------------------------------------------------
    // Argument handling
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The page size is bounded by the model's context, not by Bitbucket's ceiling of 100 — and a
    /// nonsensical value is clamped rather than rejected, because failing a call over it would cost
    /// the caller a round trip for nothing.
    /// </summary>
    [TestCase(0, "1")]
    [TestCase(-5, "1")]
    [TestCase(1, "1")]
    [TestCase(25, "25")]
    [TestCase(50, "50")]
    [TestCase(500, "50")]
    public async Task PageSizeIsClampedIntoTheSupportedRange(int requested, string expectedPageLength)
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(ToolFixtures.PullRequestPage);

        using var client = ToolTestHost.CreateClient(handler);

        _ = await PullRequestReadTools.ListPullRequestsAsync(
            client,
            ToolTestHost.CreateOptions(),
            Repository,
            Workspace,
            pageSize: requested,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(Single(handler, "pagelen"), Is.EqualTo(expectedPageLength));
    }

    [Test]
    public async Task AnUnsetPageSizeLeavesBitbucketsOwnDefault()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(ToolFixtures.PullRequestPage);

        using var client = ToolTestHost.CreateClient(handler);

        _ = await PullRequestReadTools.ListPullRequestsAsync(
            client,
            ToolTestHost.CreateOptions(),
            Repository,
            Workspace,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(QueryOf(handler.Requests[0]).Keys, Does.Not.Contain("pagelen"));
    }

    [Test]
    public async Task ListingDefaultsToOpenPullRequestsOnly()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(ToolFixtures.PullRequestPage);

        using var client = ToolTestHost.CreateClient(handler);

        _ = await PullRequestReadTools.ListPullRequestsAsync(
            client,
            ToolTestHost.CreateOptions(),
            Repository,
            Workspace,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(Single(handler, "q"), Is.EqualTo("state = \"OPEN\""));
        Assert.That(Single(handler, "sort"), Is.EqualTo("-updated_on"));
    }

    /// <summary>
    /// <c>ALL</c> means "send no state filter", not "send every state" — a BBQL disjunction over the
    /// four states is both longer and subtly different from omitting the clause.
    /// </summary>
    [Test]
    public async Task StateAllSendsNoStateFilterAtAll()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(ToolFixtures.PullRequestPage);

        using var client = ToolTestHost.CreateClient(handler);

        _ = await PullRequestReadTools.ListPullRequestsAsync(
            client,
            ToolTestHost.CreateOptions(),
            Repository,
            Workspace,
            state: "ALL",
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(QueryOf(handler.Requests[0]).Keys, Does.Not.Contain("q"));
    }

    /// <summary>
    /// The branch filter is a BBQL clause, ANDed with the state filter rather than replacing it —
    /// "does this branch already have an open pull request?" is the question createPullRequest
    /// should be asking, and it is only answerable if both clauses survive.
    /// </summary>
    [Test]
    public async Task SourceBranchBecomesABbqlClauseAndedWithTheStateFilter()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(ToolFixtures.PullRequestPage);

        using var client = ToolTestHost.CreateClient(handler);

        _ = await PullRequestReadTools.ListPullRequestsAsync(
            client,
            ToolTestHost.CreateOptions(),
            Repository,
            Workspace,
            sourceBranch: "feature/clamp",
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(Single(handler, "q"), Is.EqualTo("state = \"OPEN\" AND source.branch.name = \"feature/clamp\""));
    }

    [Test]
    public async Task SourceBranchIsTheOnlyClauseWhenTheStateFilterIsWidenedToAll()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(ToolFixtures.PullRequestPage);

        using var client = ToolTestHost.CreateClient(handler);

        _ = await PullRequestReadTools.ListPullRequestsAsync(
            client,
            ToolTestHost.CreateOptions(),
            Repository,
            Workspace,
            state: "ALL",
            sourceBranch: "feature/clamp",
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(Single(handler, "q"), Is.EqualTo("source.branch.name = \"feature/clamp\""));
    }

    /// <summary>
    /// BBQL delimits strings with <c>"</c> and documents no escape sequence, so a branch name
    /// containing one cannot be turned into a literal safely. Refusing beats guessing at an escape
    /// the parser may not implement — an unescaped quote would not fail, it would end the literal
    /// early and silently change what the query asks for.
    /// </summary>
    [TestCase("feature/\"quoted\"")]
    [TestCase("feature\\clamp")]
    public async Task ASourceBranchThatCannotBeABbqlLiteralIsRefused(string branch)
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Expect.ThrowsAsync<McpException>(() =>
            PullRequestReadTools.ListPullRequestsAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                Workspace,
                sourceBranch: branch,
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception.Message, Does.Contain("double quote or a backslash"));
        Assert.That(handler.Requests, Is.Empty);
    }

    /// <summary>The value still reaches the wire percent-encoded, quotes and all.</summary>
    [Test]
    public async Task TheBranchClauseIsPercentEncodedInTheQueryString()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(ToolFixtures.PullRequestPage);

        using var client = ToolTestHost.CreateClient(handler);

        _ = await PullRequestReadTools.ListPullRequestsAsync(
            client,
            ToolTestHost.CreateOptions(),
            Repository,
            Workspace,
            sourceBranch: "feature/clamp",
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var query = handler.Requests[0].Uri!.Query;

        Assert.That(query, Does.Not.Contain('"'));
        Assert.That(query, Does.Contain("source.branch.name%20%3D%20%22feature%2Fclamp%22"));
    }

    /// <summary>
    /// Bitbucket stores the short branch name, so a fully-qualified ref would match nothing at all —
    /// silently, which is the worst possible answer to "does this branch have a pull request?".
    /// </summary>
    [Test]
    public async Task SourceBranchStripsARefsHeadsPrefixRatherThanMatchingNothing()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(ToolFixtures.PullRequestPage);

        using var client = ToolTestHost.CreateClient(handler);

        _ = await PullRequestReadTools.ListPullRequestsAsync(
            client,
            ToolTestHost.CreateOptions(),
            Repository,
            Workspace,
            state: "ALL",
            sourceBranch: "refs/heads/feature/clamp",
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(Single(handler, "q"), Is.EqualTo("source.branch.name = \"feature/clamp\""));
    }

    [Test]
    public async Task AnEmptySourceBranchAddsNoClause()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(ToolFixtures.PullRequestPage);

        using var client = ToolTestHost.CreateClient(handler);

        _ = await PullRequestReadTools.ListPullRequestsAsync(
            client,
            ToolTestHost.CreateOptions(),
            Repository,
            Workspace,
            sourceBranch: "   ",
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(Single(handler, "q"), Is.EqualTo("state = \"OPEN\""));
    }

    [Test]
    public async Task AnUnknownStateIsRejectedWithTheAcceptedValues()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Expect.ThrowsAsync<McpException>(() =>
            PullRequestReadTools.ListPullRequestsAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                Workspace,
                state: "CLOSED",
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception.Message, Does.Contain("OPEN, MERGED, DECLINED, SUPERSEDED, ALL"));
        Assert.That(handler.Requests, Is.Empty);
    }

    /// <summary>
    /// The workspace is a URL segment, and the fallback exists so a single-workspace user never has
    /// to repeat it. When neither is available the error has to say both ways of supplying it.
    /// </summary>
    [Test]
    public async Task WorkspaceFallsBackToTheConfiguredDefault()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(ToolFixtures.PullRequestPage);

        using var client = ToolTestHost.CreateClient(handler);

        _ = await PullRequestReadTools.ListPullRequestsAsync(
            client,
            ToolTestHost.CreateOptions("from-environment"),
            Repository,
            workspace: null,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(handler.Requests[0].Uri!.AbsolutePath, Does.Contain("/repositories/from-environment/widgets/"));
    }

    [Test]
    public async Task AMissingWorkspaceNamesBothWaysOfSupplyingIt()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Expect.ThrowsAsync<McpException>(() =>
            PullRequestReadTools.ListPullRequestsAsync(
                client,
                ToolTestHost.CreateOptions(defaultWorkspace: null),
                Repository,
                workspace: null,
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception.Message, Does.Contain("BITBUCKET_DEFAULT_WORKSPACE"));
        Assert.That(exception.Message, Does.Contain("first URL segment"));
        Assert.That(handler.Requests, Is.Empty);
    }

    [Test]
    public async Task APullRequestNumberMustBePositive()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Expect.ThrowsAsync<McpException>(() =>
            PullRequestReadTools.GetPullRequestAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                0,
                Workspace,
                TestContext.CurrentContext.CancellationToken));

        Assert.That(exception.Message, Does.Contain("pullRequestId must be 1 or greater"));
        Assert.That(handler.Requests, Is.Empty);
    }

    /// <summary>
    /// An update with nothing in it would be a no-op PUT that still counts against the rate limit
    /// and still looks like a change in the pull request's history.
    /// </summary>
    [Test]
    public async Task AnEmptyUpdateIsRefusedBeforeAnyRequest()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Expect.ThrowsAsync<McpException>(() =>
            PullRequestWriteTools.UpdatePullRequestAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                42,
                Workspace,
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception.Message, Does.Contain("Nothing to update"));
        Assert.That(exception.Message, Does.Contain("title, description, destinationBranch, reviewers, closeSourceBranch or draft"));
        Assert.That(handler.Requests, Is.Empty);
    }

    /// <summary>
    /// Reviewers are UUIDs and nothing else. A model that has a display name in front of it will
    /// try it, and Bitbucket's own rejection does not say what to send instead.
    /// </summary>
    [TestCase("Grace Hopper")]
    [TestCase("grace@example.com")]
    [TestCase("11111111-2222-3333-4444-555555555555")]
    public async Task ReviewersMustBeBracedAccountUuids(string reviewer)
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Expect.ThrowsAsync<McpException>(() =>
            PullRequestWriteTools.CreatePullRequestAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                "Clamp the widget size",
                "feature/clamp",
                Workspace,
                reviewers: [reviewer],
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception.Message, Does.Contain("is not a Bitbucket account UUID"));
        Assert.That(exception.Message, Does.Contain("getPullRequest"));
        Assert.That(handler.Requests, Is.Empty);
    }

    [Test]
    public async Task AnUnknownMergeStrategyIsRejectedWithTheAcceptedOnes()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Expect.ThrowsAsync<McpException>(() =>
            PullRequestWriteTools.MergePullRequestAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                42,
                Workspace,
                mergeStrategy: "octopus",
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception.Message, Does.Contain("merge_commit"));
        Assert.That(exception.Message, Does.Contain("squash_fast_forward"));
        Assert.That(handler.Requests, Is.Empty);
    }

    /// <summary>
    /// <c>line</c>, <c>startLine</c> and <c>codeSnippet</c> only mean something relative to a file;
    /// silently dropping them would post a general comment where an inline one was meant.
    /// </summary>
    [Test]
    public async Task InlineArgumentsWithoutAPathAreRefused()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Expect.ThrowsAsync<McpException>(() =>
            PullRequestWriteTools.AddPullRequestCommentAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                42,
                "Looks wrong",
                Workspace,
                line: 12,
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception.Message, Does.Contain("An inline comment needs path as well"));
        Assert.That(handler.Requests, Is.Empty);
    }

    // ---------------------------------------------------------------------------------------
    // Review status
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// "Unapproved" is the absence of two separately tracked flags, so it takes two deletions — and
    /// deleting a flag that was never set is the requested end state, not a failure.
    /// </summary>
    [Test]
    public async Task UnapprovingClearsBothFlagsAndToleratesMissingOnes()
    {
        using var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.NotFound);
        handler.Enqueue(HttpStatusCode.MethodNotAllowed);

        using var client = ToolTestHost.CreateClient(handler);

        var result = await PullRequestWriteTools.SetPullRequestReviewStatusAsync(
            client,
            ToolTestHost.CreateOptions(),
            Repository,
            42,
            "UNAPPROVED",
            Workspace,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(handler.Requests.Count, Is.EqualTo(2));
        Assert.That(handler.Requests.Select(request => request.Method), Is.All.EqualTo(HttpMethod.Delete));
        Assert.That(handler.Requests[0].Uri!.AbsolutePath, Does.EndWith("/approve"));
        Assert.That(handler.Requests[1].Uri!.AbsolutePath, Does.EndWith("/request-changes"));

        Assert.That(result.Status, Is.EqualTo("UNAPPROVED"));
        Assert.That(result.Approved, Is.False);
        Assert.That(result.CommentId, Is.Null);
    }

    /// <summary>Tolerating a missing flag must not become tolerating a failed request.</summary>
    [Test]
    public async Task UnapprovingStillReportsAFailureThatIsNotAMissingFlag()
    {
        using var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.InternalServerError);

        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Expect.ThrowsAsync<McpException>(() =>
            PullRequestWriteTools.SetPullRequestReviewStatusAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                42,
                "UNAPPROVED",
                Workspace,
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception.Message, Does.Contain("HTTP 500"));
    }

    [Test]
    public async Task ApprovingWithACommentPostsBothAndReportsTheCommentId()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(ToolFixtures.ApprovedParticipant);
        handler.EnqueueJson(ToolFixtures.CreatedComment);

        using var client = ToolTestHost.CreateClient(handler);

        var result = await PullRequestWriteTools.SetPullRequestReviewStatusAsync(
            client,
            ToolTestHost.CreateOptions(),
            Repository,
            42,
            "approved",
            Workspace,
            comment: "Looks good, shipping it.",
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(handler.Requests.Count, Is.EqualTo(2));
        Assert.That(handler.Requests[0].Uri!.AbsolutePath, Does.EndWith("/approve"));
        Assert.That(handler.Requests[1].Uri!.AbsolutePath, Does.EndWith("/comments"));
        Assert.That(handler.Requests[1].Body!, Does.Contain("Looks good, shipping it."));

        Assert.That(result.Status, Is.EqualTo("APPROVED"));
        Assert.That(result.Approved, Is.True);
        Assert.That(result.CommentId, Is.EqualTo(2001));
        Assert.That(result.User?.Name, Is.EqualTo("Grace Hopper"));
        Assert.That(result.User?.Uuid, Is.EqualTo("{99999999-8888-7777-6666-555555555555}"));
    }

    [Test]
    public async Task RequestingChangesPostsToTheRequestChangesEndpoint()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(ToolFixtures.ApprovedParticipant);

        using var client = ToolTestHost.CreateClient(handler);

        _ = await PullRequestWriteTools.SetPullRequestReviewStatusAsync(
            client,
            ToolTestHost.CreateOptions(),
            Repository,
            42,
            "changes_requested",
            Workspace,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var request = Expect.Single(handler.Requests);
        Assert.That(request.Method, Is.EqualTo(HttpMethod.Post));
        Assert.That(request.Uri!.AbsolutePath, Does.EndWith("/request-changes"));
    }

    [Test]
    public async Task AnUnknownReviewStatusIsRejectedWithTheAcceptedOnes()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Expect.ThrowsAsync<McpException>(() =>
            PullRequestWriteTools.SetPullRequestReviewStatusAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                42,
                "LGTM",
                Workspace,
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception.Message, Does.Contain("APPROVED, CHANGES_REQUESTED or UNAPPROVED"));
        Assert.That(handler.Requests, Is.Empty);
    }

    // ---------------------------------------------------------------------------------------
    // Inline comments
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The anchor is resolved against <em>one file's</em> diff, not the pull request's: a whole-PR
    /// diff is exactly what answers 555, and the snippet only ever needs the file it is in.
    /// </summary>
    [Test]
    public async Task AnInlineCommentFetchesOnlyTheCommentedFilesDiffAndAnchorsToTheSnippet()
    {
        using var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, ToolFixtures.SingleFileDiff, "text/plain");
        handler.EnqueueJson(ToolFixtures.CreatedComment);

        using var client = ToolTestHost.CreateClient(handler);

        var result = await PullRequestWriteTools.AddPullRequestCommentAsync(
            client,
            ToolTestHost.CreateOptions(),
            Repository,
            42,
            "Please clamp the upper bound as well.",
            Workspace,
            path: "src/Widget.cs",
            codeSnippet: "Size = Math.Clamp(size, 0, MaxSize);",
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(handler.Requests.Count, Is.EqualTo(2));

        var diffRequest = handler.Requests[0];
        Assert.That(diffRequest.Uri!.AbsolutePath, Does.EndWith("/diff"));
        Assert.That(Single(diffRequest, "path"), Is.EqualTo("src/Widget.cs"));

        // The anchor lands on the added line, which is line 12 of the file after the change.
        Assert.That(handler.Requests[1].Body!, Does.Contain("\"inline\":{\"path\":\"src/Widget.cs\",\"to\":12}"));

        Assert.That(result.Path, Is.EqualTo("src/Widget.cs"));
        Assert.That(result.Line, Is.EqualTo(12));
        Assert.That(result.LineType, Is.EqualTo("ADDED"));
        Assert.That(result.MatchedText!, Does.Contain("Math.Clamp"));
    }

    /// <summary>An explicit line number anchors to the side its <c>lineType</c> names.</summary>
    [TestCase("ADDED", "\"to\":12")]
    [TestCase("REMOVED", "\"from\":12")]
    public async Task AnExplicitLineAnchorsToTheSideItsLineTypeNames(string lineType, string expectedAnchor)
    {
        using var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, ToolFixtures.SingleFileDiff, "text/plain");
        handler.EnqueueJson(ToolFixtures.CreatedComment);

        using var client = ToolTestHost.CreateClient(handler);

        _ = await PullRequestWriteTools.AddPullRequestCommentAsync(
            client,
            ToolTestHost.CreateOptions(),
            Repository,
            42,
            "A note",
            Workspace,
            path: "src/Widget.cs",
            line: 12,
            lineType: lineType,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(handler.Requests[1].Body!, Does.Contain(expectedAnchor));
    }

    [Test]
    public async Task AnUnknownLineTypeIsRejectedBeforeTheDiffIsFetched()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Expect.ThrowsAsync<McpException>(() =>
            PullRequestWriteTools.AddPullRequestCommentAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                42,
                "A note",
                Workspace,
                path: "src/Widget.cs",
                line: 12,
                lineType: "INSERTED",
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception.Message, Does.Contain("lineType must be ADDED, REMOVED or CONTEXT"));
        Assert.That(handler.Requests, Is.Empty);
    }

    /// <summary>A comment without a path is a comment on the pull request, and fetches no diff.</summary>
    [Test]
    public async Task AGeneralCommentPostsDirectlyWithNoInlineAnchor()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(ToolFixtures.CreatedComment);

        using var client = ToolTestHost.CreateClient(handler);

        var result = await PullRequestWriteTools.AddPullRequestCommentAsync(
            client,
            ToolTestHost.CreateOptions(),
            Repository,
            42,
            "Thanks!",
            Workspace,
            parentCommentId: 1001,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var request = Expect.Single(handler.Requests);
        Assert.That(request.Uri!.AbsolutePath, Does.EndWith("/comments"));
        Assert.That(request.Body!, Does.Not.Contain("inline"));
        Assert.That(request.Body!, Does.Contain("\"parent\":{\"id\":1001}"));

        Assert.That(result.Path, Is.Null);
        Assert.That(result.LineType, Is.Null);
    }

    // ---------------------------------------------------------------------------------------
    // Result shaping
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The mapping is where the token budget is won: links, object-type discriminators and
    /// repository echoes never reach the caller, a user is a name and the UUID that identifies it,
    /// and the cursor survives so pagination is possible at all.
    /// </summary>
    [Test]
    public async Task AListResultCarriesTheCursorAndNothingBitbucketSpecific()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(ToolFixtures.PullRequestPage);

        using var client = ToolTestHost.CreateClient(handler);

        var result = await PullRequestReadTools.ListPullRequestsAsync(
            client,
            ToolTestHost.CreateOptions(),
            Repository,
            Workspace,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var json = JsonSerializer.Serialize(result, BitbucketToolJsonContext.Default.PullRequestListResult);

        Assert.That(json, Does.Not.Contain("links").IgnoreCase);
        Assert.That(json, Does.Not.Contain("\"type\""));
        Assert.That(json, Does.Not.Contain("repository").IgnoreCase);
        Assert.That(json, Does.Not.Contain("null"));

        Assert.That(json, Does.Contain("\"nextCursor\":"));
        Assert.That(json, Does.Contain("\"author\":{\"name\":\"Ada Lovelace\",\"uuid\":\"{11111111-2222-3333-4444-555555555555}\"}"));

        Assert.That(result.NextCursor, Is.Not.Null);
        Assert.That(BitbucketCursor.TryDecode(result.NextCursor, out var decoded), Is.True);
        Assert.That(decoded, Is.EqualTo(ToolFixtures.NextPageUrl));
    }

    /// <summary>
    /// The cursor is opaque and complete: handing it straight back must request exactly the URL
    /// Bitbucket named, with no filter re-applied on top of it.
    /// </summary>
    [Test]
    public async Task HandingTheCursorBackRequestsExactlyTheUrlBitbucketNamed()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(ToolFixtures.PullRequestPage);
        handler.EnqueueJson("""{"values":[]}""");

        using var client = ToolTestHost.CreateClient(handler);
        var options = ToolTestHost.CreateOptions();

        var first = await PullRequestReadTools.ListPullRequestsAsync(
            client,
            options,
            Repository,
            Workspace,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var second = await PullRequestReadTools.ListPullRequestsAsync(
            client,
            options,
            Repository,
            Workspace,
            cursor: first.NextCursor,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(handler.Requests[1].Uri!.ToString(), Is.EqualTo(ToolFixtures.NextPageUrl));
        Assert.That(second.PullRequests, Is.Empty);
        Assert.That(second.NextCursor, Is.Null);
    }

    /// <summary>
    /// Bitbucket returns deleted comments with their content blanked so a thread keeps its shape.
    /// Showing them to a model means showing it empty comments it cannot act on.
    /// </summary>
    [Test]
    public async Task DeletedCommentsNeverReachTheCaller()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(ToolFixtures.CommentPage);

        using var client = ToolTestHost.CreateClient(handler);

        var result = await PullRequestReadTools.GetPullRequestCommentsAsync(
            client,
            ToolTestHost.CreateOptions(),
            Repository,
            42,
            Workspace,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(result.Comments.Select(comment => comment.Id), Is.EqualTo([1001L, 1003L]));
        Assert.That(result.NextCursor, Is.Not.Null);

        var reply = result.Comments[1];
        Assert.That(reply.Path, Is.EqualTo("src/Widget.cs"));
        Assert.That(reply.Line, Is.EqualTo(13));
        Assert.That(reply.ParentId, Is.EqualTo(1001));
        Assert.That(reply.Resolved, Is.True);

        // WhenWritingNull, asserted where it is visible: the first comment has no path, line or
        // parent, and those keys are simply absent rather than present and null.
        var json = JsonSerializer.Serialize(result, BitbucketToolJsonContext.Default.CommentListResult);
        Assert.That(json, Does.Not.Contain("null"));
    }

    /// <summary>
    /// The one URL a model cannot synthesise. Every other <c>links</c> entry Bitbucket attaches is
    /// an API URL this server composes itself, so exactly one of them is requested and mapped.
    /// </summary>
    [Test]
    public async Task PullRequestResultsCarryTheWebUrl()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(ToolFixtures.PullRequestPage);
        handler.EnqueueJson(ToolFixtures.PullRequestDetail);

        using var client = ToolTestHost.CreateClient(handler);
        var options = ToolTestHost.CreateOptions();

        var list = await PullRequestReadTools.ListPullRequestsAsync(
            client,
            options,
            Repository,
            Workspace,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var detail = await PullRequestReadTools.GetPullRequestAsync(
            client,
            options,
            Repository,
            42,
            Workspace,
            TestContext.CurrentContext.CancellationToken);

        Assert.That(list.PullRequests[0].Url, Is.EqualTo(ToolFixtures.PullRequestWebUrl));
        Assert.That(detail.Url, Is.EqualTo(ToolFixtures.PullRequestWebUrl));

        // Requested, not inferred: an inclusive fields= list returns only what it names.
        Assert.That(Single(handler.Requests[0], "fields"), Does.Contain("links.html.href"));
        Assert.That(Single(handler.Requests[1], "fields"), Does.Contain("links.html.href"));
    }

    /// <summary>
    /// The list field set has always paid for <c>close_source_branch</c> on the wire, and the
    /// mapper dropped it — so "which of these open pull requests will leave their branch behind?"
    /// cost one <c>getPullRequest</c> per entry to answer.
    /// </summary>
    [Test]
    public async Task ListedPullRequestsCarryCloseSourceBranch()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(ToolFixtures.PullRequestPage);

        using var client = ToolTestHost.CreateClient(handler);

        var list = await PullRequestReadTools.ListPullRequestsAsync(
            client,
            ToolTestHost.CreateOptions(),
            Repository,
            Workspace,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(list.PullRequests[0].CloseSourceBranch, Is.True);

        Assert.That(Single(handler.Requests[0], "fields"), Does.Contain("values.close_source_branch"));
    }

    [Test]
    public async Task CommentResultsCarryTheWebUrl()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(ToolFixtures.CommentPage);
        handler.EnqueueJson(ToolFixtures.CreatedComment);

        using var client = ToolTestHost.CreateClient(handler);
        var options = ToolTestHost.CreateOptions();

        var listed = await PullRequestReadTools.GetPullRequestCommentsAsync(
            client,
            options,
            Repository,
            42,
            Workspace,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var posted = await PullRequestWriteTools.AddPullRequestCommentAsync(
            client,
            options,
            Repository,
            42,
            "Thanks!",
            Workspace,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(listed.Comments[0].Url, Is.EqualTo(ToolFixtures.CommentWebUrl));
        Assert.That(posted.Url, Is.EqualTo(ToolFixtures.CommentWebUrl));
    }

    /// <summary>A reviewer without their stance cannot answer "is this ready to merge?".</summary>
    [Test]
    public async Task ReviewersCarryTheStanceFromTheParticipantList()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(ToolFixtures.PullRequestDetail);

        using var client = ToolTestHost.CreateClient(handler);

        var result = await PullRequestReadTools.GetPullRequestAsync(
            client,
            ToolTestHost.CreateOptions(),
            Repository,
            42,
            Workspace,
            TestContext.CurrentContext.CancellationToken);

        var reviewer = Expect.Single(result.Reviewers!);
        Assert.That(reviewer.Name, Is.EqualTo("Grace Hopper"));
        Assert.That(reviewer.Uuid, Is.EqualTo("{99999999-8888-7777-6666-555555555555}"));
        Assert.That(reviewer.Approved, Is.True);
        Assert.That(reviewer.State, Is.EqualTo("approved"));
    }

    // ---------------------------------------------------------------------------------------
    // Diffs
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Diffstat is the default because a speculative whole-PR diff is the call that fails on the
    /// pull requests worth reviewing.
    /// </summary>
    [Test]
    public async Task TheDiffToolDefaultsToDiffstat()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(ToolFixtures.DiffStatPage);

        using var client = ToolTestHost.CreateClient(handler);

        var result = await PullRequestReadTools.GetPullRequestDiffAsync(
            client,
            ToolTestHost.CreateOptions(),
            Repository,
            42,
            Workspace,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(handler.Requests[0].Uri!.AbsolutePath, Does.EndWith("/diffstat"));
        Assert.That(result.Mode, Is.EqualTo("diffstat"));
        Assert.That(result.Diff, Is.Null);

        var file = Expect.Single(result.Diffstat!.Files);
        Assert.That(file.Path, Is.EqualTo("src/Widget.cs"));
        Assert.That(file.Status, Is.EqualTo("modified"));
        Assert.That(result.Diffstat.NextCursor, Is.Not.Null);

        var json = JsonSerializer.Serialize(result, BitbucketToolJsonContext.Default.PullRequestDiffResult);
        Assert.That(json, Does.Not.Contain("\"diff\":"));
    }

    [Test]
    public async Task DiffModeRequestsOnlyTheNamedFiles()
    {
        using var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, ToolFixtures.SingleFileDiff, "text/plain");

        using var client = ToolTestHost.CreateClient(handler);

        var result = await PullRequestReadTools.GetPullRequestDiffAsync(
            client,
            ToolTestHost.CreateOptions(),
            Repository,
            42,
            Workspace,
            mode: "diff",
            paths: ["src/Widget.cs"],
            contextLines: 5,
            ignoreWhitespace: true,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var query = QueryOf(handler.Requests[0]);
        Assert.That(query["path"], Is.EqualTo(["src/Widget.cs"]));
        Assert.That(query["context"][0], Is.EqualTo("5"));
        Assert.That(query["ignore_whitespace"][0], Is.EqualTo("true"));

        Assert.That(result.Mode, Is.EqualTo("diff"));
        Assert.That(result.Diffstat, Is.Null);
        Assert.That(result.Diff!.Truncated, Is.False);
        Assert.That(result.Diff.Hint, Is.Null);

        var file = Expect.Single(result.Diff.Files);
        Assert.That(file.Path, Is.EqualTo("src/Widget.cs"));
        Assert.That(file.Status, Is.EqualTo("modified"));
        Assert.That(file.Diff!, Does.Contain("Math.Clamp"));
    }

    /// <summary>
    /// Truncation is never silent: the cut is marked inside the diff text, the result says it
    /// happened, and the hint says which call would fetch the rest.
    /// </summary>
    [Test]
    public async Task ATruncatedDiffSaysSoInTheTextAndInTheResult()
    {
        using var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, ToolFixtures.SingleFileDiff, "text/plain");

        using var client = ToolTestHost.CreateClient(handler);

        var result = await PullRequestReadTools.GetPullRequestDiffAsync(
            client,
            ToolTestHost.CreateOptions(),
            Repository,
            42,
            Workspace,
            mode: "diff",
            paths: ["src/Widget.cs"],
            maxLinesPerFile: 2,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(result.Diff!.Truncated, Is.True);
        Assert.That(result.Diff.Hint!, Does.Contain("mode=\"diffstat\""));

        var file = Expect.Single(result.Diff.Files);
        Assert.That(file.Truncated, Is.True);
        Assert.That(file.LinesShown, Is.EqualTo(2));
        Assert.That(file.Diff!, Does.Contain("[truncated:"));
        Assert.That(file.Diff, Does.Contain("maxLinesPerFile="));
    }

    /// <summary>
    /// <c>paths</c> names files to read, and only the diff mode reads files. Before this, the call
    /// answered with the file list and nothing said why — from which a model reasonably concluded
    /// the file was not in the pull request.
    /// </summary>
    [Test]
    public async Task PathsSelectDiffModeWithoutAnExplicitMode()
    {
        using var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, ToolFixtures.SingleFileDiff, "text/plain");

        using var client = ToolTestHost.CreateClient(handler);

        var result = await PullRequestReadTools.GetPullRequestDiffAsync(
            client,
            ToolTestHost.CreateOptions(),
            Repository,
            42,
            Workspace,
            paths: ["src/Widget.cs"],
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(handler.Requests[0].Uri!.AbsolutePath, Does.EndWith("/diff"));
        Assert.That(result.Mode, Is.EqualTo("diff"));
        Assert.That(result.Diffstat, Is.Null);
        Assert.That(result.Diff!.Files, Has.Exactly(1).Items);
    }

    /// <summary>An explicit mode is never overridden — the two arguments contradict each other.</summary>
    [Test]
    public async Task PathsWithAnExplicitDiffstatModeIsRefusedRatherThanIgnored()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Expect.ThrowsAsync<McpException>(() =>
            PullRequestReadTools.GetPullRequestDiffAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                42,
                Workspace,
                mode: "diffstat",
                paths: ["src/Widget.cs"],
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception.Message, Does.Contain("conflicts with paths"));
        Assert.That(exception.Message, Does.Contain("Drop mode"));
        Assert.That(handler.Requests, Is.Empty);
    }

    [Test]
    public async Task MaxLinesPerFileWithAnExplicitDiffstatModeIsRefused()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Expect.ThrowsAsync<McpException>(() =>
            PullRequestReadTools.GetPullRequestDiffAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                42,
                Workspace,
                mode: "diffstat",
                maxLinesPerFile: 100,
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception.Message, Does.Contain("conflicts with maxLinesPerFile"));
        Assert.That(handler.Requests, Is.Empty);
    }

    /// <summary>Only the changed-file list is paginated, so a cursor cannot continue a diff.</summary>
    [Test]
    public async Task ACursorAlongsideThePathsThatSelectDiffModeIsRefused()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Expect.ThrowsAsync<McpException>(() =>
            PullRequestReadTools.GetPullRequestDiffAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                42,
                Workspace,
                paths: ["src/Widget.cs"],
                cursor: BitbucketCursor.Encode(ToolFixtures.NextPageUrl),
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception.Message, Does.Contain("cursor conflicts with"));
        Assert.That(handler.Requests, Is.Empty);
    }

    /// <summary>
    /// Blank paths are not paths: <c>CleanList</c> drops them, and the mode must follow — otherwise
    /// an empty array would quietly ask for the whole pull request's diff.
    /// </summary>
    [Test]
    public async Task AnEmptyPathsArrayStillMeansDiffstat()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(ToolFixtures.DiffStatPage);

        using var client = ToolTestHost.CreateClient(handler);

        var result = await PullRequestReadTools.GetPullRequestDiffAsync(
            client,
            ToolTestHost.CreateOptions(),
            Repository,
            42,
            Workspace,
            paths: ["  "],
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(handler.Requests[0].Uri!.AbsolutePath, Does.EndWith("/diffstat"));
        Assert.That(result.Mode, Is.EqualTo("diffstat"));
    }

    [Test]
    public async Task AnUnknownDiffModeIsRejectedWithBothModes()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Expect.ThrowsAsync<McpException>(() =>
            PullRequestReadTools.GetPullRequestDiffAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                42,
                Workspace,
                mode: "patch",
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception.Message, Does.Contain("\"diffstat\""));
        Assert.That(exception.Message, Does.Contain("\"diff\""));
        Assert.That(handler.Requests, Is.Empty);
    }

    // ---------------------------------------------------------------------------------------
    // Merge and decline
    // ---------------------------------------------------------------------------------------

    [Test]
    public async Task MergingReportsTheStateAndTheMergeCommit()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(ToolFixtures.MergedPullRequest);

        using var client = ToolTestHost.CreateClient(handler);

        var result = await PullRequestWriteTools.MergePullRequestAsync(
            client,
            ToolTestHost.CreateOptions(),
            Repository,
            42,
            Workspace,
            mergeStrategy: "squash",
            message: "Clamp the widget size (#42)",
            closeSourceBranch: true,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var request = Expect.Single(handler.Requests);
        Assert.That(request.Uri!.AbsolutePath, Does.EndWith("/merge"));
        Assert.That(request.Body!, Does.Contain("\"merge_strategy\":\"squash\""));
        Assert.That(request.Body, Does.Contain("\"close_source_branch\":true"));

        Assert.That(result.State, Is.EqualTo("MERGED"));
        Assert.That(result.MergeCommitHash, Is.EqualTo("abc123def456"));
    }

    [Test]
    public async Task DecliningReportsTheStateAndTheStoredReason()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(ToolFixtures.DeclinedPullRequest);

        using var client = ToolTestHost.CreateClient(handler);

        var result = await PullRequestWriteTools.DeclinePullRequestAsync(
            client,
            ToolTestHost.CreateOptions(),
            Repository,
            42,
            Workspace,
            reason: "Superseded by #43",
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var request = Expect.Single(handler.Requests);
        Assert.That(request.Uri!.AbsolutePath, Does.EndWith("/decline"));

        Assert.That(result.State, Is.EqualTo("DECLINED"));
        Assert.That(result.Reason, Is.EqualTo("Superseded by #43"));
    }

    [Test]
    public async Task CreatingAPullRequestSendsOnlyTheFieldsThatWereGiven()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(ToolFixtures.PullRequestDetail);

        using var client = ToolTestHost.CreateClient(handler);

        var result = await PullRequestWriteTools.CreatePullRequestAsync(
            client,
            ToolTestHost.CreateOptions(),
            Repository,
            "  Clamp the widget size  ",
            "feature/clamp",
            Workspace,
            reviewers: ["{99999999-8888-7777-6666-555555555555}"],
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var body = Expect.Single(handler.Requests).Body!;

        Assert.That(body, Does.Contain("\"title\":\"Clamp the widget size\""));
        Assert.That(body, Does.Contain("\"source\":{\"branch\":{\"name\":\"feature/clamp\"}}"));
        Assert.That(body, Does.Contain("\"uuid\":\"{99999999-8888-7777-6666-555555555555}\""));
        Assert.That(body, Does.Not.Contain("\"destination\""));
        Assert.That(body, Does.Not.Contain("\"description\""));

        // The two flags are nullable for this line: as plain bools they defaulted to false and were
        // serialised on every create, forcing a value where the caller had expressed no opinion.
        Assert.That(body, Does.Not.Contain("\"close_source_branch\""));
        Assert.That(body, Does.Not.Contain("\"draft\""));

        Assert.That(result.Id, Is.EqualTo(42));
    }

    /// <summary>
    /// Omitting <c>reviewers</c> leaves the field out, so Bitbucket applies whatever
    /// default-reviewer rule the repository carries. Passing an empty array sends
    /// <c>"reviewers": []</c>, which is the only way to say "nobody" — the two must not collapse
    /// into each other.
    /// </summary>
    [Test]
    public async Task OmittingReviewersLeavesTheFieldOutEntirely()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(ToolFixtures.PullRequestDetail);

        using var client = ToolTestHost.CreateClient(handler);

        _ = await PullRequestWriteTools.CreatePullRequestAsync(
            client,
            ToolTestHost.CreateOptions(),
            Repository,
            "Clamp the widget size",
            "feature/clamp",
            Workspace,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var body = Expect.Single(handler.Requests).Body!;

        Assert.That(body, Does.Not.Contain("\"reviewers\""));
    }

    [Test]
    public async Task AnEmptyReviewerArrayOpensThePullRequestWithNobodyOnIt()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(ToolFixtures.PullRequestDetail);

        using var client = ToolTestHost.CreateClient(handler);

        _ = await PullRequestWriteTools.CreatePullRequestAsync(
            client,
            ToolTestHost.CreateOptions(),
            Repository,
            "Clamp the widget size",
            "feature/clamp",
            Workspace,
            reviewers: [],
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var body = Expect.Single(handler.Requests).Body!;

        Assert.That(body, Does.Contain("\"reviewers\":[]"));
    }

    /// <summary>
    /// An array of nothing but blanks is still an array: the caller typed one, so it means the
    /// empty list rather than "unspecified".
    /// </summary>
    [Test]
    public async Task AnArrayOfBlankReviewersIsStillTheEmptyList()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(ToolFixtures.PullRequestDetail);

        using var client = ToolTestHost.CreateClient(handler);

        _ = await PullRequestWriteTools.CreatePullRequestAsync(
            client,
            ToolTestHost.CreateOptions(),
            Repository,
            "Clamp the widget size",
            "feature/clamp",
            Workspace,
            reviewers: ["   ", ""],
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(Expect.Single(handler.Requests).Body!, Does.Contain("\"reviewers\":[]"));
    }

    /// <summary>
    /// Clearing a reviewer list was previously inexpressible — an empty array folded to null and
    /// the field was omitted, which the arity guard then read as "nothing to update".
    /// </summary>
    [Test]
    public async Task AnEmptyReviewerArrayClearsTheListOnUpdateAndSatisfiesTheArityGuard()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(ToolFixtures.PullRequestDetail);

        using var client = ToolTestHost.CreateClient(handler);

        _ = await PullRequestWriteTools.UpdatePullRequestAsync(
            client,
            ToolTestHost.CreateOptions(),
            Repository,
            42,
            Workspace,
            reviewers: [],
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var body = Expect.Single(handler.Requests).Body!;

        Assert.That(body, Does.Contain("\"reviewers\":[]"));
        Assert.That(body, Does.Not.Contain("\"title\""));
    }

    /// <summary>
    /// The regression behind issue #1: both flags round-trip through the wire model correctly and
    /// were simply never bound, so passing one was accepted, answered 200, and changed nothing.
    /// </summary>
    [Test]
    public async Task UpdatingAPullRequestSendsCloseSourceBranchAndDraft()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(ToolFixtures.PullRequestDetail);

        using var client = ToolTestHost.CreateClient(handler);

        var result = await PullRequestWriteTools.UpdatePullRequestAsync(
            client,
            ToolTestHost.CreateOptions(),
            Repository,
            42,
            Workspace,
            closeSourceBranch: true,
            draft: false,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var request = Expect.Single(handler.Requests);
        Assert.That(request.Method, Is.EqualTo(HttpMethod.Put));
        Assert.That(request.Body, Is.EqualTo("""{"close_source_branch":true,"draft":false}"""));

        Assert.That(result.CloseSourceBranch, Is.True);
    }

    /// <summary>
    /// Either flag on its own is a complete update. Before the fix the arity guard counted only
    /// title, description, destinationBranch and reviewers, so this call was refused outright and
    /// the workaround — resending the title to get past the guard — silently dropped the flag.
    /// </summary>
    [TestCase(true, null, """{"close_source_branch":true}""")]
    [TestCase(false, null, """{"close_source_branch":false}""")]
    [TestCase(null, false, """{"draft":false}""")]
    [TestCase(null, true, """{"draft":true}""")]
    public async Task EitherFlagAloneIsEnoughToUpdate(bool? closeSourceBranch, bool? draft, string expected)
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(ToolFixtures.PullRequestDetail);

        using var client = ToolTestHost.CreateClient(handler);

        _ = await PullRequestWriteTools.UpdatePullRequestAsync(
            client,
            ToolTestHost.CreateOptions(),
            Repository,
            42,
            Workspace,
            closeSourceBranch: closeSourceBranch,
            draft: draft,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(Expect.Single(handler.Requests).Body, Is.EqualTo(expected));
    }

    /// <summary>
    /// The endpoint is documented as a partial update, but every example Bitbucket publishes for it
    /// carries a title. If it turns out to mean the examples, the pull request's own title is
    /// fetched and resent with the change rather than the call simply failing.
    /// </summary>
    [Test]
    public async Task AFlagOnlyUpdateFallsBackToResendingThePullRequestsOwnTitle()
    {
        using var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.BadRequest, """{"error":{"message":"title: This field is required."}}""");
        handler.EnqueueJson(ToolFixtures.PullRequestDetail);
        handler.EnqueueJson(ToolFixtures.PullRequestDetail);

        using var client = ToolTestHost.CreateClient(handler);

        var result = await PullRequestWriteTools.UpdatePullRequestAsync(
            client,
            ToolTestHost.CreateOptions(),
            Repository,
            42,
            Workspace,
            closeSourceBranch: true,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(handler.Requests.Count, Is.EqualTo(3));
        Assert.That(handler.Requests[0].Method, Is.EqualTo(HttpMethod.Put));
        Assert.That(handler.Requests[1].Method, Is.EqualTo(HttpMethod.Get));
        Assert.That(handler.Requests[2].Method, Is.EqualTo(HttpMethod.Put));

        Assert.That(handler.Requests[0].Body, Is.EqualTo("""{"close_source_branch":true}"""));
        Assert.That(handler.Requests[2].Body, Is.EqualTo("""{"title":"Clamp the widget size","close_source_branch":true}"""));

        Assert.That(result.Id, Is.EqualTo(42));
    }

    /// <summary>
    /// A 400 on a body that named a title is the caller's own problem — retrying it with the same
    /// title would only spend a second request to fail the same way.
    /// </summary>
    [Test]
    public async Task AnUpdateThatAlreadyCarriedATitleIsNotRetried()
    {
        using var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.BadRequest, """{"error":{"message":"destination: Invalid branch."}}""");

        using var client = ToolTestHost.CreateClient(handler);

        _ = await Expect.ThrowsAsync<McpException>(() =>
            PullRequestWriteTools.UpdatePullRequestAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                42,
                Workspace,
                title: "Clamp the widget size",
                destinationBranch: "no/such/branch",
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(handler.Requests, Has.Exactly(1).Items);
    }

    // ---------------------------------------------------------------------------------------
    // Default reviewers
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The <em>effective</em> endpoint, not plain <c>default-reviewers</c>: a repository whose
    /// project configures the reviewers centrally has none of its own, and the plain endpoint then
    /// answers with an empty list — which reads exactly like "this repository has no reviewers".
    /// </summary>
    [Test]
    public async Task DefaultReviewersComeFromTheEffectiveEndpointWithTheirOrigin()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(ToolFixtures.DefaultReviewerPage);

        using var client = ToolTestHost.CreateClient(handler);

        var result = await PullRequestReadTools.ListDefaultReviewersAsync(
            client,
            ToolTestHost.CreateOptions(),
            Repository,
            Workspace,
            pageSize: 10,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var request = Expect.Single(handler.Requests);
        Assert.That(request.Method, Is.EqualTo(HttpMethod.Get));
        Assert.That(request.Uri!.AbsolutePath, Does.EndWith("/effective-default-reviewers"));
        Assert.That(Single(request, "pagelen"), Is.EqualTo("10"));

        Assert.That(result.Reviewers.Count, Is.EqualTo(2));
        Assert.That(result.Reviewers[0].Name, Is.EqualTo("Grace Hopper"));
        Assert.That(result.Reviewers[0].Uuid, Is.EqualTo("{99999999-8888-7777-6666-555555555555}"));
        Assert.That(result.Reviewers[0].ReviewerType, Is.EqualTo("repository"));
        Assert.That(result.Reviewers[1].ReviewerType, Is.EqualTo("project"));
        Assert.That(result.TotalSize, Is.EqualTo(2));

        Assert.That(result.NextCursor, Is.Not.Null);
        Assert.That(BitbucketCursor.TryDecode(result.NextCursor, out var decoded), Is.True);
        Assert.That(decoded, Is.EqualTo(ToolFixtures.NextPageUrl));

        var json = JsonSerializer.Serialize(result, BitbucketToolJsonContext.Default.DefaultReviewerListResult);
        Assert.That(json, Does.Not.Contain("links").IgnoreCase);
        Assert.That(json, Does.Not.Contain("null"));
    }

    /// <summary>The cursor is the whole request; handing it back must not re-derive the URL.</summary>
    [Test]
    public async Task DefaultReviewersPaginateThroughTheCursor()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(ToolFixtures.DefaultReviewerPage);
        handler.EnqueueJson("""{"values":[]}""");

        using var client = ToolTestHost.CreateClient(handler);
        var options = ToolTestHost.CreateOptions();

        var first = await PullRequestReadTools.ListDefaultReviewersAsync(
            client,
            options,
            Repository,
            Workspace,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var second = await PullRequestReadTools.ListDefaultReviewersAsync(
            client,
            options,
            Repository,
            Workspace,
            cursor: first.NextCursor,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(handler.Requests[1].Uri!.ToString(), Is.EqualTo(ToolFixtures.NextPageUrl));
        Assert.That(second.Reviewers, Is.Empty);
        Assert.That(second.NextCursor, Is.Null);
    }

    // ---------------------------------------------------------------------------------------
    // Build statuses
    // ---------------------------------------------------------------------------------------

    [Test]
    public async Task StatusesReportEveryCheckWithItsStateAndLink()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(ToolFixtures.CommitStatusPage);

        using var client = ToolTestHost.CreateClient(handler);

        var result = await PullRequestReadTools.ListPullRequestStatusesAsync(
            client,
            ToolTestHost.CreateOptions(),
            Repository,
            42,
            Workspace,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var request = Expect.Single(handler.Requests);
        Assert.That(request.Method, Is.EqualTo(HttpMethod.Get));
        Assert.That(request.Uri!.AbsolutePath, Does.EndWith("/pullrequests/42/statuses"));

        Assert.That(result.Statuses.Select(status => status.State), Is.EqualTo(["SUCCESSFUL", "FAILED"]));

        var failed = result.Statuses[1];
        Assert.That(failed.Key, Is.EqualTo("BB-DEPLOY"));
        Assert.That(failed.Name, Is.EqualTo("BB-DEPLOY-4"));
        Assert.That(failed.Url, Is.EqualTo("https://ci.example.com/deploys/4"));
        Assert.That(failed.Description, Is.EqualTo("Smoke tests failed"));
        Assert.That(failed.Refname, Is.Null);

        Assert.That(result.NextCursor, Is.Not.Null);

        var json = JsonSerializer.Serialize(result, BitbucketToolJsonContext.Default.PullRequestStatusListResult);
        Assert.That(json, Does.Not.Contain("null"));
    }

    // ---------------------------------------------------------------------------------------
    // Tasks
    // ---------------------------------------------------------------------------------------

    [Test]
    public async Task TasksCarryTheirStateCreatorAndAttachedComment()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(ToolFixtures.TaskPage);

        using var client = ToolTestHost.CreateClient(handler);

        var result = await PullRequestReadTools.ListPullRequestTasksAsync(
            client,
            ToolTestHost.CreateOptions(),
            Repository,
            42,
            Workspace,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var request = Expect.Single(handler.Requests);
        Assert.That(request.Uri!.AbsolutePath, Does.EndWith("/pullrequests/42/tasks"));

        Assert.That(result.Tasks.Select(task => task.Id), Is.EqualTo([501L, 502L]));

        var open = result.Tasks[0];
        Assert.That(open.State, Is.EqualTo("UNRESOLVED"));
        Assert.That(open.Content, Is.EqualTo("Clamp the upper bound too."));
        Assert.That(open.Creator?.Name, Is.EqualTo("Grace Hopper"));
        Assert.That(open.ResolvedBy, Is.Null);
        Assert.That(open.CommentId, Is.Null);

        var done = result.Tasks[1];
        Assert.That(done.State, Is.EqualTo("RESOLVED"));
        Assert.That(done.ResolvedBy?.Name, Is.EqualTo("Ada Lovelace"));
        Assert.That(done.CommentId, Is.EqualTo(1001));

        Assert.That(result.NextCursor, Is.Not.Null);

        var json = JsonSerializer.Serialize(result, BitbucketToolJsonContext.Default.PullRequestTaskListResult);
        Assert.That(json, Does.Not.Contain("null"));
    }

    [Test]
    public async Task AddingATaskPostsTheContentAndTheCommentItHangsOff()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(ToolFixtures.CreatedTask, HttpStatusCode.Created);

        using var client = ToolTestHost.CreateClient(handler);

        var result = await PullRequestWriteTools.AddPullRequestTaskAsync(
            client,
            ToolTestHost.CreateOptions(),
            Repository,
            42,
            "Clamp the upper bound too.",
            Workspace,
            commentId: 1001,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var request = Expect.Single(handler.Requests);
        Assert.That(request.Method, Is.EqualTo(HttpMethod.Post));
        Assert.That(request.Uri!.AbsolutePath, Does.EndWith("/pullrequests/42/tasks"));
        Assert.That(request.Body, Is.EqualTo("""{"content":{"raw":"Clamp the upper bound too."},"comment":{"id":1001}}"""));

        Assert.That(result.Id, Is.EqualTo(503));
        Assert.That(result.State, Is.EqualTo("UNRESOLVED"));
        Assert.That(result.CommentId, Is.EqualTo(1001));
    }

    [Test]
    public async Task AFreeStandingTaskSendsNoCommentAtAll()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(ToolFixtures.CreatedTask, HttpStatusCode.Created);

        using var client = ToolTestHost.CreateClient(handler);

        _ = await PullRequestWriteTools.AddPullRequestTaskAsync(
            client,
            ToolTestHost.CreateOptions(),
            Repository,
            42,
            "Clamp the upper bound too.",
            Workspace,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(handler.Requests[0].Body!, Does.Not.Contain("comment"));
    }

    [Test]
    public async Task ATaskNeedsContent()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Expect.ThrowsAsync<McpException>(() =>
            PullRequestWriteTools.AddPullRequestTaskAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                42,
                "   ",
                Workspace,
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception.Message, Does.Contain("content is required"));
        Assert.That(handler.Requests, Is.Empty);
    }

    /// <summary>
    /// Bitbucket's published schema marks both fields of the update body optional, so ticking a task
    /// off is one PUT carrying one field — no read-modify-write, and no chance of clobbering a text
    /// edit made in between.
    /// </summary>
    [Test]
    public async Task ResolvingATaskSendsTheStateAloneInASingleRequest()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(ToolFixtures.ResolvedTask);

        using var client = ToolTestHost.CreateClient(handler);

        var result = await PullRequestWriteTools.UpdatePullRequestTaskAsync(
            client,
            ToolTestHost.CreateOptions(),
            Repository,
            42,
            503,
            Workspace,
            state: "resolved",
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var request = Expect.Single(handler.Requests);
        Assert.That(request.Method, Is.EqualTo(HttpMethod.Put));
        Assert.That(request.Uri!.AbsolutePath, Does.EndWith("/pullrequests/42/tasks/503"));
        Assert.That(request.Body, Is.EqualTo("""{"state":"RESOLVED"}"""));

        Assert.That(result.State, Is.EqualTo("RESOLVED"));
        Assert.That(result.ResolvedBy?.Name, Is.EqualTo("Ada Lovelace"));
    }

    /// <summary>
    /// The schema says a state-only body is legal; the same endpoint documents a 400 for "a missing
    /// required field". If Bitbucket turns out to mean the second, the task's own text is fetched
    /// and sent back with the new state rather than the call simply failing.
    /// </summary>
    [Test]
    public async Task AStateOnlyUpdateFallsBackToResendingTheTasksOwnText()
    {
        using var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.BadRequest, """{"error":{"message":"content: This field is required."}}""");
        handler.EnqueueJson(ToolFixtures.CreatedTask);
        handler.EnqueueJson(ToolFixtures.ResolvedTask);

        using var client = ToolTestHost.CreateClient(handler);

        var result = await PullRequestWriteTools.UpdatePullRequestTaskAsync(
            client,
            ToolTestHost.CreateOptions(),
            Repository,
            42,
            503,
            Workspace,
            state: "RESOLVED",
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(handler.Requests.Count, Is.EqualTo(3));
        Assert.That(handler.Requests[0].Method, Is.EqualTo(HttpMethod.Put));
        Assert.That(handler.Requests[1].Method, Is.EqualTo(HttpMethod.Get));
        Assert.That(handler.Requests[2].Method, Is.EqualTo(HttpMethod.Put));

        Assert.That(handler.Requests[2].Body, Is.EqualTo("""{"content":{"raw":"Clamp the upper bound too."},"state":"RESOLVED"}"""));

        Assert.That(result.State, Is.EqualTo("RESOLVED"));
    }

    /// <summary>The fallback is for a state-only body; a 400 on a body that already had content is real.</summary>
    [Test]
    public async Task ABadRequestOnAnUpdateThatSentContentIsNotRetried()
    {
        using var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.BadRequest, """{"error":{"message":"Task content is blank."}}""");

        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Expect.ThrowsAsync<McpException>(() =>
            PullRequestWriteTools.UpdatePullRequestTaskAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                42,
                503,
                Workspace,
                content: "Clamp the upper bound too.",
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception.Message, Does.Contain("Task content is blank."));
        Assert.That(handler.Requests, Has.Exactly(1).Items);
    }

    [Test]
    public async Task AnEmptyTaskUpdateIsRefusedBeforeAnyRequest()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Expect.ThrowsAsync<McpException>(() =>
            PullRequestWriteTools.UpdatePullRequestTaskAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                42,
                503,
                Workspace,
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception.Message, Does.Contain("Nothing to update"));
        Assert.That(handler.Requests, Is.Empty);
    }

    [Test]
    public async Task AnUnknownTaskStateIsRejectedWithBothStates()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Expect.ThrowsAsync<McpException>(() =>
            PullRequestWriteTools.UpdatePullRequestTaskAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                42,
                503,
                Workspace,
                state: "DONE",
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception.Message, Does.Contain("RESOLVED or UNRESOLVED"));
        Assert.That(handler.Requests, Is.Empty);
    }

    [Test]
    public async Task ATaskIdMustBePositive()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Expect.ThrowsAsync<McpException>(() =>
            PullRequestWriteTools.UpdatePullRequestTaskAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                42,
                0,
                Workspace,
                state: "RESOLVED",
                cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception.Message, Does.Contain("taskId must be 1 or greater"));
        Assert.That(handler.Requests, Is.Empty);
    }

    // ---------------------------------------------------------------------------------------
    // Comment resolution
    // ---------------------------------------------------------------------------------------

    [Test]
    public async Task ResolvingACommentPostsToResolveAndReportsWhoDidIt()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(ToolFixtures.CommentResolution);

        using var client = ToolTestHost.CreateClient(handler);

        var result = await PullRequestWriteTools.ResolvePullRequestCommentAsync(
            client,
            ToolTestHost.CreateOptions(),
            Repository,
            42,
            1001,
            resolved: true,
            Workspace,
            TestContext.CurrentContext.CancellationToken);

        var request = Expect.Single(handler.Requests);
        Assert.That(request.Method, Is.EqualTo(HttpMethod.Post));
        Assert.That(request.Uri!.AbsolutePath, Does.EndWith("/pullrequests/42/comments/1001/resolve"));

        Assert.That(result.CommentId, Is.EqualTo(1001));
        Assert.That(result.Resolved, Is.True);
        Assert.That(result.ResolvedBy?.Name, Is.EqualTo("Ada Lovelace"));
        Assert.That(result.ResolvedBy?.Uuid, Is.EqualTo("{11111111-2222-3333-4444-555555555555}"));
        Assert.That(result.ResolvedOn, Is.Not.Null);
    }

    [Test]
    public async Task ReopeningACommentDeletesTheResolution()
    {
        using var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.NoContent);

        using var client = ToolTestHost.CreateClient(handler);

        var result = await PullRequestWriteTools.ResolvePullRequestCommentAsync(
            client,
            ToolTestHost.CreateOptions(),
            Repository,
            42,
            1001,
            resolved: false,
            Workspace,
            TestContext.CurrentContext.CancellationToken);

        var request = Expect.Single(handler.Requests);
        Assert.That(request.Method, Is.EqualTo(HttpMethod.Delete));
        Assert.That(request.Uri!.AbsolutePath, Does.EndWith("/pullrequests/42/comments/1001/resolve"));

        Assert.That(result.CommentId, Is.EqualTo(1001));
        Assert.That(result.Resolved, Is.False);

        // The DELETE answers 204 with no body, so there is nothing to echo — and the keys are
        // absent rather than present and null.
        var json = JsonSerializer.Serialize(result, BitbucketToolJsonContext.Default.CommentResolutionResult);
        Assert.That(json, Does.Not.Contain("null"));
    }

    /// <summary>
    /// The tool is annotated idempotent, which it only is because these two are swallowed: Bitbucket
    /// answers 409 to resolving a resolved thread and 404 to reopening an open one, and in both
    /// cases the state the caller asked for is the state it is in.
    /// </summary>
    [TestCase(true, HttpStatusCode.Conflict)]
    [TestCase(false, HttpStatusCode.NotFound)]
    public async Task AskingForTheStateAThreadIsAlreadyInIsNotAnError(bool resolved, HttpStatusCode status)
    {
        using var handler = new StubHttpMessageHandler();
        handler.Enqueue(status);

        using var client = ToolTestHost.CreateClient(handler);

        var result = await PullRequestWriteTools.ResolvePullRequestCommentAsync(
            client,
            ToolTestHost.CreateOptions(),
            Repository,
            42,
            1001,
            resolved,
            Workspace,
            TestContext.CurrentContext.CancellationToken);

        Assert.That(result.Resolved, Is.EqualTo(resolved));
        Assert.That(result.ResolvedBy, Is.Null);
    }

    /// <summary>Tolerating the already-in-that-state answer must not become tolerating a refusal.</summary>
    [Test]
    public async Task AResolveThatBitbucketRefusesIsStillReported()
    {
        using var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.Forbidden);

        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Expect.ThrowsAsync<McpException>(() =>
            PullRequestWriteTools.ResolvePullRequestCommentAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                42,
                1001,
                resolved: true,
                Workspace,
                TestContext.CurrentContext.CancellationToken));

        Assert.That(exception.Message, Does.Contain("403 Forbidden"));
    }

    [Test]
    public async Task ACommentIdMustBePositive()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Expect.ThrowsAsync<McpException>(() =>
            PullRequestWriteTools.ResolvePullRequestCommentAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                42,
                0,
                resolved: true,
                Workspace,
                TestContext.CurrentContext.CancellationToken));

        Assert.That(exception.Message, Does.Contain("commentId must be 1 or greater"));
        Assert.That(handler.Requests, Is.Empty);
    }

    // ---------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------

    private static string Single(StubHttpMessageHandler handler, string name) =>
        Single(handler.Requests[0], name);

    private static string Single(RecordedRequest request, string name) =>
        Expect.Single(QueryOf(request)[name]);

    /// <summary>The recorded request's query string, unescaped, with repeated names preserved.</summary>
    private static Dictionary<string, List<string>> QueryOf(RecordedRequest request)
    {
        var parsed = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var query = request.Uri?.Query ?? string.Empty;

        if (query.StartsWith('?'))
        {
            query = query[1..];
        }

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var name = separator < 0 ? pair : pair[..separator];
            var value = separator < 0 ? string.Empty : Uri.UnescapeDataString(pair[(separator + 1)..]);

            if (!parsed.TryGetValue(name, out var values))
            {
                values = [];
                parsed[name] = values;
            }

            values.Add(value);
        }

        return parsed;
    }
}
