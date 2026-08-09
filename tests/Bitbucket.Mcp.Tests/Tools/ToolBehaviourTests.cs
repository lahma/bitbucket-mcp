using System.Net;
using System.Text.Json;

using Bitbucket.Mcp.Http;
using Bitbucket.Mcp.Tools;
using Bitbucket.Mcp.Tools.Models;

using ModelContextProtocol;

using Xunit;

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
    [Theory]
    [InlineData(0, "1")]
    [InlineData(-5, "1")]
    [InlineData(1, "1")]
    [InlineData(25, "25")]
    [InlineData(50, "50")]
    [InlineData(500, "50")]
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
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expectedPageLength, Single(handler, "pagelen"));
    }

    [Fact]
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
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.DoesNotContain("pagelen", QueryOf(handler.Requests[0]).Keys);
    }

    [Fact]
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
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("state = \"OPEN\"", Single(handler, "q"));
        Assert.Equal("-updated_on", Single(handler, "sort"));
    }

    /// <summary>
    /// <c>ALL</c> means "send no state filter", not "send every state" — a BBQL disjunction over the
    /// four states is both longer and subtly different from omitting the clause.
    /// </summary>
    [Fact]
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
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.DoesNotContain("q", QueryOf(handler.Requests[0]).Keys);
    }

    /// <summary>
    /// The branch filter is a BBQL clause, ANDed with the state filter rather than replacing it —
    /// "does this branch already have an open pull request?" is the question createPullRequest
    /// should be asking, and it is only answerable if both clauses survive.
    /// </summary>
    [Fact]
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
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
            "state = \"OPEN\" AND source.branch.name = \"feature/clamp\"",
            Single(handler, "q"));
    }

    [Fact]
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
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("source.branch.name = \"feature/clamp\"", Single(handler, "q"));
    }

    /// <summary>
    /// BBQL delimits strings with <c>"</c> and documents no escape sequence, so a branch name
    /// containing one cannot be turned into a literal safely. Refusing beats guessing at an escape
    /// the parser may not implement — an unescaped quote would not fail, it would end the literal
    /// early and silently change what the query asks for.
    /// </summary>
    [Theory]
    [InlineData("feature/\"quoted\"")]
    [InlineData("feature\\clamp")]
    public async Task ASourceBranchThatCannotBeABbqlLiteralIsRefused(string branch)
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            PullRequestReadTools.ListPullRequestsAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                Workspace,
                sourceBranch: branch,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("double quote or a backslash", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    /// <summary>The value still reaches the wire percent-encoded, quotes and all.</summary>
    [Fact]
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
            cancellationToken: TestContext.Current.CancellationToken);

        var query = handler.Requests[0].Uri!.Query;

        Assert.DoesNotContain('"', query);
        Assert.Contains("source.branch.name%20%3D%20%22feature%2Fclamp%22", query, StringComparison.Ordinal);
    }

    /// <summary>
    /// Bitbucket stores the short branch name, so a fully-qualified ref would match nothing at all —
    /// silently, which is the worst possible answer to "does this branch have a pull request?".
    /// </summary>
    [Fact]
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
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("source.branch.name = \"feature/clamp\"", Single(handler, "q"));
    }

    [Fact]
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
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("state = \"OPEN\"", Single(handler, "q"));
    }

    [Fact]
    public async Task AnUnknownStateIsRejectedWithTheAcceptedValues()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            PullRequestReadTools.ListPullRequestsAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                Workspace,
                state: "CLOSED",
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("OPEN, MERGED, DECLINED, SUPERSEDED, ALL", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    /// <summary>
    /// The workspace is a URL segment, and the fallback exists so a single-workspace user never has
    /// to repeat it. When neither is available the error has to say both ways of supplying it.
    /// </summary>
    [Fact]
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
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(
            "/repositories/from-environment/widgets/",
            handler.Requests[0].Uri!.AbsolutePath,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingWorkspaceNamesBothWaysOfSupplyingIt()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            PullRequestReadTools.ListPullRequestsAsync(
                client,
                ToolTestHost.CreateOptions(defaultWorkspace: null),
                Repository,
                workspace: null,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("BITBUCKET_DEFAULT_WORKSPACE", exception.Message, StringComparison.Ordinal);
        Assert.Contains("first URL segment", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task APullRequestNumberMustBePositive()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            PullRequestReadTools.GetPullRequestAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                0,
                Workspace,
                TestContext.Current.CancellationToken));

        Assert.Contains("pullRequestId must be 1 or greater", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    /// <summary>
    /// An update with nothing in it would be a no-op PUT that still counts against the rate limit
    /// and still looks like a change in the pull request's history.
    /// </summary>
    [Fact]
    public async Task AnEmptyUpdateIsRefusedBeforeAnyRequest()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            PullRequestWriteTools.UpdatePullRequestAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                42,
                Workspace,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("Nothing to update", exception.Message, StringComparison.Ordinal);
        Assert.Contains("title, description, destinationBranch or reviewers", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    /// <summary>
    /// Reviewers are UUIDs and nothing else. A model that has a display name in front of it will
    /// try it, and Bitbucket's own rejection does not say what to send instead.
    /// </summary>
    [Theory]
    [InlineData("Grace Hopper")]
    [InlineData("grace@example.com")]
    [InlineData("11111111-2222-3333-4444-555555555555")]
    public async Task ReviewersMustBeBracedAccountUuids(string reviewer)
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            PullRequestWriteTools.CreatePullRequestAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                "Clamp the widget size",
                "feature/clamp",
                Workspace,
                reviewers: [reviewer],
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("is not a Bitbucket account UUID", exception.Message, StringComparison.Ordinal);
        Assert.Contains("getPullRequest", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AnUnknownMergeStrategyIsRejectedWithTheAcceptedOnes()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            PullRequestWriteTools.MergePullRequestAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                42,
                Workspace,
                mergeStrategy: "octopus",
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("merge_commit", exception.Message, StringComparison.Ordinal);
        Assert.Contains("squash_fast_forward", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    /// <summary>
    /// <c>line</c>, <c>startLine</c> and <c>codeSnippet</c> only mean something relative to a file;
    /// silently dropping them would post a general comment where an inline one was meant.
    /// </summary>
    [Fact]
    public async Task InlineArgumentsWithoutAPathAreRefused()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            PullRequestWriteTools.AddPullRequestCommentAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                42,
                "Looks wrong",
                Workspace,
                line: 12,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("An inline comment needs path as well", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    // ---------------------------------------------------------------------------------------
    // Review status
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// "Unapproved" is the absence of two separately tracked flags, so it takes two deletions — and
    /// deleting a flag that was never set is the requested end state, not a failure.
    /// </summary>
    [Fact]
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
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request => Assert.Equal(HttpMethod.Delete, request.Method));
        Assert.EndsWith("/approve", handler.Requests[0].Uri!.AbsolutePath, StringComparison.Ordinal);
        Assert.EndsWith("/request-changes", handler.Requests[1].Uri!.AbsolutePath, StringComparison.Ordinal);

        Assert.Equal("UNAPPROVED", result.Status);
        Assert.False(result.Approved);
        Assert.Null(result.CommentId);
    }

    /// <summary>Tolerating a missing flag must not become tolerating a failed request.</summary>
    [Fact]
    public async Task UnapprovingStillReportsAFailureThatIsNotAMissingFlag()
    {
        using var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.InternalServerError);

        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            PullRequestWriteTools.SetPullRequestReviewStatusAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                42,
                "UNAPPROVED",
                Workspace,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("HTTP 500", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
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
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.Requests.Count);
        Assert.EndsWith("/approve", handler.Requests[0].Uri!.AbsolutePath, StringComparison.Ordinal);
        Assert.EndsWith("/comments", handler.Requests[1].Uri!.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains("Looks good, shipping it.", handler.Requests[1].Body!, StringComparison.Ordinal);

        Assert.Equal("APPROVED", result.Status);
        Assert.True(result.Approved);
        Assert.Equal(2001, result.CommentId);
        Assert.Equal("Grace Hopper", result.User?.Name);
        Assert.Equal("{99999999-8888-7777-6666-555555555555}", result.User?.Uuid);
    }

    [Fact]
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
            cancellationToken: TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.EndsWith("/request-changes", request.Uri!.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnknownReviewStatusIsRejectedWithTheAcceptedOnes()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            PullRequestWriteTools.SetPullRequestReviewStatusAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                42,
                "LGTM",
                Workspace,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("APPROVED, CHANGES_REQUESTED or UNAPPROVED", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    // ---------------------------------------------------------------------------------------
    // Inline comments
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The anchor is resolved against <em>one file's</em> diff, not the pull request's: a whole-PR
    /// diff is exactly what answers 555, and the snippet only ever needs the file it is in.
    /// </summary>
    [Fact]
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
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.Requests.Count);

        var diffRequest = handler.Requests[0];
        Assert.EndsWith("/diff", diffRequest.Uri!.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal("src/Widget.cs", Single(diffRequest, "path"));

        // The anchor lands on the added line, which is line 12 of the file after the change.
        Assert.Contains("\"inline\":{\"path\":\"src/Widget.cs\",\"to\":12}", handler.Requests[1].Body!, StringComparison.Ordinal);

        Assert.Equal("src/Widget.cs", result.Path);
        Assert.Equal(12, result.Line);
        Assert.Equal("ADDED", result.LineType);
        Assert.Contains("Math.Clamp", result.MatchedText!, StringComparison.Ordinal);
    }

    /// <summary>An explicit line number anchors to the side its <c>lineType</c> names.</summary>
    [Theory]
    [InlineData("ADDED", "\"to\":12")]
    [InlineData("REMOVED", "\"from\":12")]
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
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(expectedAnchor, handler.Requests[1].Body!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnknownLineTypeIsRejectedBeforeTheDiffIsFetched()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
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
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("lineType must be ADDED, REMOVED or CONTEXT", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    /// <summary>A comment without a path is a comment on the pull request, and fetches no diff.</summary>
    [Fact]
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
            cancellationToken: TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);
        Assert.EndsWith("/comments", request.Uri!.AbsolutePath, StringComparison.Ordinal);
        Assert.DoesNotContain("inline", request.Body!, StringComparison.Ordinal);
        Assert.Contains("\"parent\":{\"id\":1001}", request.Body!, StringComparison.Ordinal);

        Assert.Null(result.Path);
        Assert.Null(result.LineType);
    }

    // ---------------------------------------------------------------------------------------
    // Result shaping
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The mapping is where the token budget is won: links, object-type discriminators and
    /// repository echoes never reach the caller, a user is a name and the UUID that identifies it,
    /// and the cursor survives so pagination is possible at all.
    /// </summary>
    [Fact]
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
            cancellationToken: TestContext.Current.CancellationToken);

        var json = JsonSerializer.Serialize(result, BitbucketToolJsonContext.Default.PullRequestListResult);

        Assert.DoesNotContain("links", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"type\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("repository", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("null", json, StringComparison.Ordinal);

        Assert.Contains("\"nextCursor\":", json, StringComparison.Ordinal);
        Assert.Contains(
            "\"author\":{\"name\":\"Ada Lovelace\",\"uuid\":\"{11111111-2222-3333-4444-555555555555}\"}",
            json,
            StringComparison.Ordinal);

        Assert.NotNull(result.NextCursor);
        Assert.True(BitbucketCursor.TryDecode(result.NextCursor, out var decoded));
        Assert.Equal(ToolFixtures.NextPageUrl, decoded);
    }

    /// <summary>
    /// The cursor is opaque and complete: handing it straight back must request exactly the URL
    /// Bitbucket named, with no filter re-applied on top of it.
    /// </summary>
    [Fact]
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
            cancellationToken: TestContext.Current.CancellationToken);

        var second = await PullRequestReadTools.ListPullRequestsAsync(
            client,
            options,
            Repository,
            Workspace,
            cursor: first.NextCursor,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ToolFixtures.NextPageUrl, handler.Requests[1].Uri!.ToString());
        Assert.Empty(second.PullRequests);
        Assert.Null(second.NextCursor);
    }

    /// <summary>
    /// Bitbucket returns deleted comments with their content blanked so a thread keeps its shape.
    /// Showing them to a model means showing it empty comments it cannot act on.
    /// </summary>
    [Fact]
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
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal([1001L, 1003L], result.Comments.Select(comment => comment.Id));
        Assert.NotNull(result.NextCursor);

        var reply = result.Comments[1];
        Assert.Equal("src/Widget.cs", reply.Path);
        Assert.Equal(13, reply.Line);
        Assert.Equal(1001, reply.ParentId);
        Assert.True(reply.Resolved);

        // WhenWritingNull, asserted where it is visible: the first comment has no path, line or
        // parent, and those keys are simply absent rather than present and null.
        var json = JsonSerializer.Serialize(result, BitbucketToolJsonContext.Default.CommentListResult);
        Assert.DoesNotContain("null", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// The one URL a model cannot synthesise. Every other <c>links</c> entry Bitbucket attaches is
    /// an API URL this server composes itself, so exactly one of them is requested and mapped.
    /// </summary>
    [Fact]
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
            cancellationToken: TestContext.Current.CancellationToken);

        var detail = await PullRequestReadTools.GetPullRequestAsync(
            client,
            options,
            Repository,
            42,
            Workspace,
            TestContext.Current.CancellationToken);

        Assert.Equal(ToolFixtures.PullRequestWebUrl, list.PullRequests[0].Url);
        Assert.Equal(ToolFixtures.PullRequestWebUrl, detail.Url);

        // Requested, not inferred: an inclusive fields= list returns only what it names.
        Assert.Contains("links.html.href", Single(handler.Requests[0], "fields"), StringComparison.Ordinal);
        Assert.Contains("links.html.href", Single(handler.Requests[1], "fields"), StringComparison.Ordinal);
    }

    [Fact]
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
            cancellationToken: TestContext.Current.CancellationToken);

        var posted = await PullRequestWriteTools.AddPullRequestCommentAsync(
            client,
            options,
            Repository,
            42,
            "Thanks!",
            Workspace,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ToolFixtures.CommentWebUrl, listed.Comments[0].Url);
        Assert.Equal(ToolFixtures.CommentWebUrl, posted.Url);
    }

    /// <summary>A reviewer without their stance cannot answer "is this ready to merge?".</summary>
    [Fact]
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
            TestContext.Current.CancellationToken);

        var reviewer = Assert.Single(result.Reviewers!);
        Assert.Equal("Grace Hopper", reviewer.Name);
        Assert.Equal("{99999999-8888-7777-6666-555555555555}", reviewer.Uuid);
        Assert.True(reviewer.Approved);
        Assert.Equal("approved", reviewer.State);
    }

    // ---------------------------------------------------------------------------------------
    // Diffs
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Diffstat is the default because a speculative whole-PR diff is the call that fails on the
    /// pull requests worth reviewing.
    /// </summary>
    [Fact]
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
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.EndsWith("/diffstat", handler.Requests[0].Uri!.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal("diffstat", result.Mode);
        Assert.Null(result.Diff);

        var file = Assert.Single(result.Diffstat!.Files);
        Assert.Equal("src/Widget.cs", file.Path);
        Assert.Equal("modified", file.Status);
        Assert.NotNull(result.Diffstat.NextCursor);

        var json = JsonSerializer.Serialize(result, BitbucketToolJsonContext.Default.PullRequestDiffResult);
        Assert.DoesNotContain("\"diff\":", json, StringComparison.Ordinal);
    }

    [Fact]
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
            cancellationToken: TestContext.Current.CancellationToken);

        var query = QueryOf(handler.Requests[0]);
        Assert.Equal(["src/Widget.cs"], query["path"]);
        Assert.Equal("5", query["context"][0]);
        Assert.Equal("true", query["ignore_whitespace"][0]);

        Assert.Equal("diff", result.Mode);
        Assert.Null(result.Diffstat);
        Assert.False(result.Diff!.Truncated);
        Assert.Null(result.Diff.Hint);

        var file = Assert.Single(result.Diff.Files);
        Assert.Equal("src/Widget.cs", file.Path);
        Assert.Equal("modified", file.Status);
        Assert.Contains("Math.Clamp", file.Diff!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Truncation is never silent: the cut is marked inside the diff text, the result says it
    /// happened, and the hint says which call would fetch the rest.
    /// </summary>
    [Fact]
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
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Diff!.Truncated);
        Assert.Contains("mode=\"diffstat\"", result.Diff.Hint!, StringComparison.Ordinal);

        var file = Assert.Single(result.Diff.Files);
        Assert.True(file.Truncated);
        Assert.Equal(2, file.LinesShown);
        Assert.Contains("[truncated:", file.Diff!, StringComparison.Ordinal);
        Assert.Contains("maxLinesPerFile=", file.Diff, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>paths</c> names files to read, and only the diff mode reads files. Before this, the call
    /// answered with the file list and nothing said why — from which a model reasonably concluded
    /// the file was not in the pull request.
    /// </summary>
    [Fact]
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
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.EndsWith("/diff", handler.Requests[0].Uri!.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal("diff", result.Mode);
        Assert.Null(result.Diffstat);
        Assert.Single(result.Diff!.Files);
    }

    /// <summary>An explicit mode is never overridden — the two arguments contradict each other.</summary>
    [Fact]
    public async Task PathsWithAnExplicitDiffstatModeIsRefusedRatherThanIgnored()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            PullRequestReadTools.GetPullRequestDiffAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                42,
                Workspace,
                mode: "diffstat",
                paths: ["src/Widget.cs"],
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("conflicts with paths", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Drop mode", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task MaxLinesPerFileWithAnExplicitDiffstatModeIsRefused()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            PullRequestReadTools.GetPullRequestDiffAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                42,
                Workspace,
                mode: "diffstat",
                maxLinesPerFile: 100,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("conflicts with maxLinesPerFile", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    /// <summary>Only the changed-file list is paginated, so a cursor cannot continue a diff.</summary>
    [Fact]
    public async Task ACursorAlongsideThePathsThatSelectDiffModeIsRefused()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            PullRequestReadTools.GetPullRequestDiffAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                42,
                Workspace,
                paths: ["src/Widget.cs"],
                cursor: BitbucketCursor.Encode(ToolFixtures.NextPageUrl),
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("cursor conflicts with", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    /// <summary>
    /// Blank paths are not paths: <c>CleanList</c> drops them, and the mode must follow — otherwise
    /// an empty array would quietly ask for the whole pull request's diff.
    /// </summary>
    [Fact]
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
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.EndsWith("/diffstat", handler.Requests[0].Uri!.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal("diffstat", result.Mode);
    }

    [Fact]
    public async Task AnUnknownDiffModeIsRejectedWithBothModes()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            PullRequestReadTools.GetPullRequestDiffAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                42,
                Workspace,
                mode: "patch",
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("\"diffstat\"", exception.Message, StringComparison.Ordinal);
        Assert.Contains("\"diff\"", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    // ---------------------------------------------------------------------------------------
    // Merge and decline
    // ---------------------------------------------------------------------------------------

    [Fact]
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
            cancellationToken: TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);
        Assert.EndsWith("/merge", request.Uri!.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains("\"merge_strategy\":\"squash\"", request.Body!, StringComparison.Ordinal);
        Assert.Contains("\"close_source_branch\":true", request.Body, StringComparison.Ordinal);

        Assert.Equal("MERGED", result.State);
        Assert.Equal("abc123def456", result.MergeCommitHash);
    }

    [Fact]
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
            cancellationToken: TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);
        Assert.EndsWith("/decline", request.Uri!.AbsolutePath, StringComparison.Ordinal);

        Assert.Equal("DECLINED", result.State);
        Assert.Equal("Superseded by #43", result.Reason);
    }

    [Fact]
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
            cancellationToken: TestContext.Current.CancellationToken);

        var body = Assert.Single(handler.Requests).Body!;

        Assert.Contains("\"title\":\"Clamp the widget size\"", body, StringComparison.Ordinal);
        Assert.Contains("\"source\":{\"branch\":{\"name\":\"feature/clamp\"}}", body, StringComparison.Ordinal);
        Assert.Contains("\"uuid\":\"{99999999-8888-7777-6666-555555555555}\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"destination\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"description\"", body, StringComparison.Ordinal);

        Assert.Equal(42, result.Id);
    }

    // ---------------------------------------------------------------------------------------
    // Default reviewers
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The <em>effective</em> endpoint, not plain <c>default-reviewers</c>: a repository whose
    /// project configures the reviewers centrally has none of its own, and the plain endpoint then
    /// answers with an empty list — which reads exactly like "this repository has no reviewers".
    /// </summary>
    [Fact]
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
            cancellationToken: TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.EndsWith("/effective-default-reviewers", request.Uri!.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal("10", Single(request, "pagelen"));

        Assert.Equal(2, result.Reviewers.Count);
        Assert.Equal("Grace Hopper", result.Reviewers[0].Name);
        Assert.Equal("{99999999-8888-7777-6666-555555555555}", result.Reviewers[0].Uuid);
        Assert.Equal("repository", result.Reviewers[0].ReviewerType);
        Assert.Equal("project", result.Reviewers[1].ReviewerType);
        Assert.Equal(2, result.TotalSize);

        Assert.NotNull(result.NextCursor);
        Assert.True(BitbucketCursor.TryDecode(result.NextCursor, out var decoded));
        Assert.Equal(ToolFixtures.NextPageUrl, decoded);

        var json = JsonSerializer.Serialize(result, BitbucketToolJsonContext.Default.DefaultReviewerListResult);
        Assert.DoesNotContain("links", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("null", json, StringComparison.Ordinal);
    }

    /// <summary>The cursor is the whole request; handing it back must not re-derive the URL.</summary>
    [Fact]
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
            cancellationToken: TestContext.Current.CancellationToken);

        var second = await PullRequestReadTools.ListDefaultReviewersAsync(
            client,
            options,
            Repository,
            Workspace,
            cursor: first.NextCursor,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ToolFixtures.NextPageUrl, handler.Requests[1].Uri!.ToString());
        Assert.Empty(second.Reviewers);
        Assert.Null(second.NextCursor);
    }

    // ---------------------------------------------------------------------------------------
    // Build statuses
    // ---------------------------------------------------------------------------------------

    [Fact]
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
            cancellationToken: TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.EndsWith("/pullrequests/42/statuses", request.Uri!.AbsolutePath, StringComparison.Ordinal);

        Assert.Equal(["SUCCESSFUL", "FAILED"], result.Statuses.Select(status => status.State));

        var failed = result.Statuses[1];
        Assert.Equal("BB-DEPLOY", failed.Key);
        Assert.Equal("BB-DEPLOY-4", failed.Name);
        Assert.Equal("https://ci.example.com/deploys/4", failed.Url);
        Assert.Equal("Smoke tests failed", failed.Description);
        Assert.Null(failed.Refname);

        Assert.NotNull(result.NextCursor);

        var json = JsonSerializer.Serialize(result, BitbucketToolJsonContext.Default.PullRequestStatusListResult);
        Assert.DoesNotContain("null", json, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------
    // Tasks
    // ---------------------------------------------------------------------------------------

    [Fact]
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
            cancellationToken: TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);
        Assert.EndsWith("/pullrequests/42/tasks", request.Uri!.AbsolutePath, StringComparison.Ordinal);

        Assert.Equal([501L, 502L], result.Tasks.Select(task => task.Id));

        var open = result.Tasks[0];
        Assert.Equal("UNRESOLVED", open.State);
        Assert.Equal("Clamp the upper bound too.", open.Content);
        Assert.Equal("Grace Hopper", open.Creator?.Name);
        Assert.Null(open.ResolvedBy);
        Assert.Null(open.CommentId);

        var done = result.Tasks[1];
        Assert.Equal("RESOLVED", done.State);
        Assert.Equal("Ada Lovelace", done.ResolvedBy?.Name);
        Assert.Equal(1001, done.CommentId);

        Assert.NotNull(result.NextCursor);

        var json = JsonSerializer.Serialize(result, BitbucketToolJsonContext.Default.PullRequestTaskListResult);
        Assert.DoesNotContain("null", json, StringComparison.Ordinal);
    }

    [Fact]
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
            cancellationToken: TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.EndsWith("/pullrequests/42/tasks", request.Uri!.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal(
            """{"content":{"raw":"Clamp the upper bound too."},"comment":{"id":1001}}""",
            request.Body);

        Assert.Equal(503, result.Id);
        Assert.Equal("UNRESOLVED", result.State);
        Assert.Equal(1001, result.CommentId);
    }

    [Fact]
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
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.DoesNotContain("comment", handler.Requests[0].Body!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATaskNeedsContent()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            PullRequestWriteTools.AddPullRequestTaskAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                42,
                "   ",
                Workspace,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("content is required", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    /// <summary>
    /// Bitbucket's published schema marks both fields of the update body optional, so ticking a task
    /// off is one PUT carrying one field — no read-modify-write, and no chance of clobbering a text
    /// edit made in between.
    /// </summary>
    [Fact]
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
            cancellationToken: TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.EndsWith("/pullrequests/42/tasks/503", request.Uri!.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal("""{"state":"RESOLVED"}""", request.Body);

        Assert.Equal("RESOLVED", result.State);
        Assert.Equal("Ada Lovelace", result.ResolvedBy?.Name);
    }

    /// <summary>
    /// The schema says a state-only body is legal; the same endpoint documents a 400 for "a missing
    /// required field". If Bitbucket turns out to mean the second, the task's own text is fetched
    /// and sent back with the new state rather than the call simply failing.
    /// </summary>
    [Fact]
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
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(HttpMethod.Put, handler.Requests[0].Method);
        Assert.Equal(HttpMethod.Get, handler.Requests[1].Method);
        Assert.Equal(HttpMethod.Put, handler.Requests[2].Method);

        Assert.Equal(
            """{"content":{"raw":"Clamp the upper bound too."},"state":"RESOLVED"}""",
            handler.Requests[2].Body);

        Assert.Equal("RESOLVED", result.State);
    }

    /// <summary>The fallback is for a state-only body; a 400 on a body that already had content is real.</summary>
    [Fact]
    public async Task ABadRequestOnAnUpdateThatSentContentIsNotRetried()
    {
        using var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.BadRequest, """{"error":{"message":"Task content is blank."}}""");

        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            PullRequestWriteTools.UpdatePullRequestTaskAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                42,
                503,
                Workspace,
                content: "Clamp the upper bound too.",
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("Task content is blank.", exception.Message, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task AnEmptyTaskUpdateIsRefusedBeforeAnyRequest()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            PullRequestWriteTools.UpdatePullRequestTaskAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                42,
                503,
                Workspace,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("Nothing to update", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AnUnknownTaskStateIsRejectedWithBothStates()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            PullRequestWriteTools.UpdatePullRequestTaskAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                42,
                503,
                Workspace,
                state: "DONE",
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("RESOLVED or UNRESOLVED", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ATaskIdMustBePositive()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            PullRequestWriteTools.UpdatePullRequestTaskAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                42,
                0,
                Workspace,
                state: "RESOLVED",
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("taskId must be 1 or greater", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    // ---------------------------------------------------------------------------------------
    // Comment resolution
    // ---------------------------------------------------------------------------------------

    [Fact]
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
            TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.EndsWith(
            "/pullrequests/42/comments/1001/resolve",
            request.Uri!.AbsolutePath,
            StringComparison.Ordinal);

        Assert.Equal(1001, result.CommentId);
        Assert.True(result.Resolved);
        Assert.Equal("Ada Lovelace", result.ResolvedBy?.Name);
        Assert.Equal("{11111111-2222-3333-4444-555555555555}", result.ResolvedBy?.Uuid);
        Assert.NotNull(result.ResolvedOn);
    }

    [Fact]
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
            TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.EndsWith(
            "/pullrequests/42/comments/1001/resolve",
            request.Uri!.AbsolutePath,
            StringComparison.Ordinal);

        Assert.Equal(1001, result.CommentId);
        Assert.False(result.Resolved);

        // The DELETE answers 204 with no body, so there is nothing to echo — and the keys are
        // absent rather than present and null.
        var json = JsonSerializer.Serialize(result, BitbucketToolJsonContext.Default.CommentResolutionResult);
        Assert.DoesNotContain("null", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// The tool is annotated idempotent, which it only is because these two are swallowed: Bitbucket
    /// answers 409 to resolving a resolved thread and 404 to reopening an open one, and in both
    /// cases the state the caller asked for is the state it is in.
    /// </summary>
    [Theory]
    [InlineData(true, HttpStatusCode.Conflict)]
    [InlineData(false, HttpStatusCode.NotFound)]
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
            TestContext.Current.CancellationToken);

        Assert.Equal(resolved, result.Resolved);
        Assert.Null(result.ResolvedBy);
    }

    /// <summary>Tolerating the already-in-that-state answer must not become tolerating a refusal.</summary>
    [Fact]
    public async Task AResolveThatBitbucketRefusesIsStillReported()
    {
        using var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.Forbidden);

        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            PullRequestWriteTools.ResolvePullRequestCommentAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                42,
                1001,
                resolved: true,
                Workspace,
                TestContext.Current.CancellationToken));

        Assert.Contains("403 Forbidden", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACommentIdMustBePositive()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            PullRequestWriteTools.ResolvePullRequestCommentAsync(
                client,
                ToolTestHost.CreateOptions(),
                Repository,
                42,
                0,
                resolved: true,
                Workspace,
                TestContext.Current.CancellationToken));

        Assert.Contains("commentId must be 1 or greater", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    // ---------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------

    private static string Single(StubHttpMessageHandler handler, string name) =>
        Single(handler.Requests[0], name);

    private static string Single(RecordedRequest request, string name) =>
        Assert.Single(QueryOf(request)[name]);

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
