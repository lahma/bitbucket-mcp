using System.Globalization;
using System.Net;
using System.Net.Http.Headers;

using Bitbucket.Mcp.Http;
using Bitbucket.Mcp.Http.Models;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace Bitbucket.Mcp.Tests.Http;

/// <summary>
/// Covers <see cref="BitbucketApiClient"/> against a stub transport: the exact URL and body it
/// puts on the wire, the DTOs it reads back from golden fixtures, and how it turns a failure into
/// a <see cref="BitbucketApiException"/>.
/// </summary>
/// <remarks>
/// <para>
/// URLs are asserted through <see cref="Uri.AbsolutePath"/> and the parsed query rather than
/// <see cref="Uri.ToString()"/>, which unescapes and would happily report a hostile workspace slug
/// as if it had never been escaped.
/// </para>
/// <para>
/// The stub replaces the innermost handler, so the <c>302</c> Bitbucket answers the diff endpoints
/// with never happens here; the final response is stubbed directly.
/// </para>
/// </remarks>
public class BitbucketApiClientTests
{
    private const string Workspace = "acme";
    private const string Repository = "widget-api";
    private const string ProjectUrl = "+https://github.com/lahma/bitbucket-mcp";

    private const string EmptyPage = """{"values":[]}""";

    /// <summary>The files a diff is narrowed to, and the order their <c>path=</c> parameters must keep.</summary>
    private static readonly string[] DiffPaths = ["src/A.cs", "assets/logo (draft).png", "docs/http/retry.md"];

    private static readonly string[] TitleFieldErrors = ["This field is required."];

    /// <summary>Page sizes and the <c>pagelen</c> they must be clamped to.</summary>
    public static TheoryData<int, string> PageSizes => new()
    {
        { -5, "1" },
        { 0, "1" },
        { 1, "1" },
        { 50, "50" },
        { 100, "100" },
        { 101, "100" },
        { 5000, "100" },
    };

    // ---------------------------------------------------------------- listing

    [Fact]
    public async Task ListPullRequestsAsksForTheListFieldSetAndNothingElse()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(EmptyPage);

        using var client = CreateClient(stub);

        _ = await client.ListPullRequestsAsync(
            Workspace,
            Repository,
            cancellationToken: TestContext.Current.CancellationToken);

        var request = Assert.Single(stub.Requests);

        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/2.0/repositories/acme/widget-api/pullrequests", RequestUrl.Path(request.Uri));

        // Exactly the constant, so a field set edited without editing FieldSets shows up here.
        Assert.Equal(FieldSets.PullRequestList, RequestUrl.QueryValue(request.Uri, "fields"));

        // Unset filters are absent rather than empty: "" is a different request to Bitbucket.
        Assert.Equal("fields", Assert.Single(RequestUrl.Query(request.Uri)).Key);

        // Commas are escaped on the wire even though the decoded value reads as a plain list.
        Assert.Contains("%2C", RequestUrl.PathAndQuery(request.Uri), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(PageSizes))]
    public async Task ListPullRequestsClampsPageSizeToBitbucketsCeiling(int pageSize, string expected)
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(EmptyPage);

        using var client = CreateClient(stub);

        _ = await client.ListPullRequestsAsync(
            Workspace,
            Repository,
            pageSize: pageSize,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected, RequestUrl.QueryValue(Assert.Single(stub.Requests).Uri, "pagelen"));
    }

    [Theory]
    [InlineData(new[] { "OPEN" }, "state = \"OPEN\"")]
    [InlineData(new[] { "open" }, "state = \"OPEN\"")]
    [InlineData(new[] { "OPEN", "MERGED" }, "(state = \"OPEN\" OR state = \"MERGED\")")]
    [InlineData(new[] { "OPEN", "MERGED", "DECLINED" }, "(state = \"OPEN\" OR state = \"MERGED\" OR state = \"DECLINED\")")]
    [InlineData(new[] { "OPEN", "  " }, "state = \"OPEN\"")]
    public async Task ListPullRequestsComposesStateClauses(string[] states, string expected)
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(EmptyPage);

        using var client = CreateClient(stub);

        _ = await client.ListPullRequestsAsync(
            Workspace,
            Repository,
            states: states,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected, RequestUrl.QueryValue(Assert.Single(stub.Requests).Uri, "q"));
    }

    [Theory]
    [InlineData("{9e1f2a3b-4c5d-6e7f-8091-a2b3c4d5e6f7}", "author.uuid = \"{9e1f2a3b-4c5d-6e7f-8091-a2b3c4d5e6f7}\"")]
    [InlineData("jane", "author.nickname = \"jane\"")]
    [InlineData("  jane  ", "author.nickname = \"jane\"")]
    public async Task ListPullRequestsMatchesAuthorsByUuidOrNickname(string author, string expected)
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(EmptyPage);

        using var client = CreateClient(stub);

        _ = await client.ListPullRequestsAsync(
            Workspace,
            Repository,
            author: author,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected, RequestUrl.QueryValue(Assert.Single(stub.Requests).Uri, "q"));
    }

    [Fact]
    public async Task ListPullRequestsQuotesAndEscapesAuthorValues()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(EmptyPage);

        using var client = CreateClient(stub);

        _ = await client.ListPullRequestsAsync(
            Workspace,
            Repository,
            author: "ja\"ne\\x",
            cancellationToken: TestContext.Current.CancellationToken);

        // BBQL string literals are double-quoted; a quote or a backslash in the value has to be
        // escaped or the filter parses as something else entirely.
        Assert.Equal(
            "author.nickname = \"ja\\\"ne\\\\x\"",
            RequestUrl.QueryValue(Assert.Single(stub.Requests).Uri, "q"));
    }

    [Fact]
    public async Task ListPullRequestsParenthesisesTheCallerQueryAndAndsEveryClause()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(EmptyPage);

        using var client = CreateClient(stub);

        _ = await client.ListPullRequestsAsync(
            Workspace,
            Repository,
            states: ["OPEN", "MERGED"],
            author: "jane",
            query: "title ~ \"retry\" OR reviewers.nickname = \"ada\"",
            sort: "-updated_on",
            cancellationToken: TestContext.Current.CancellationToken);

        var request = Assert.Single(stub.Requests);

        // The caller's fragment is wrapped so its OR cannot rearrange the state and author
        // clauses it is ANDed with.
        Assert.Equal(
            "(state = \"OPEN\" OR state = \"MERGED\") AND author.nickname = \"jane\" "
            + "AND (title ~ \"retry\" OR reviewers.nickname = \"ada\")",
            RequestUrl.QueryValue(request.Uri, "q"));

        Assert.Equal("-updated_on", RequestUrl.QueryValue(request.Uri, "sort"));
    }

    [Fact]
    public async Task ListPullRequestsOmitsTheQueryWhenNoFilterIsGiven()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(EmptyPage);

        using var client = CreateClient(stub);

        _ = await client.ListPullRequestsAsync(
            Workspace,
            Repository,
            states: [],
            author: "   ",
            query: "",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(RequestUrl.QueryValue(Assert.Single(stub.Requests).Uri, "q"));
    }

    [Fact]
    public async Task ListPullRequestsMapsTheFixturePage()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(HttpFixtures.Read("http-pullrequest-list.json"));

        using var client = CreateClient(stub);

        var page = await client.ListPullRequestsAsync(
            Workspace,
            Repository,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(47, page.TotalSize);
        Assert.Equal(3, page.Items.Count);

        Assert.NotNull(page.NextCursor);
        Assert.True(BitbucketCursor.TryDecode(page.NextCursor, out var next));
        Assert.Equal(
            "https://api.bitbucket.org/2.0/repositories/acme/widget-api/pullrequests?fields=next%2Csize%2Cvalues.id&page=2&pagelen=3",
            next);

        var first = page.Items[0];
        Assert.Equal(412, first.Id);
        Assert.Equal("Add retry handler for 429 responses", first.Title);
        Assert.Equal("OPEN", first.State);
        Assert.False(first.Draft);
        Assert.Equal(7, first.CommentCount);
        Assert.Equal(1, first.TaskCount);
        Assert.True(first.CloseSourceBranch);
        Assert.Equal(
            DateTimeOffset.Parse("2026-07-14T09:12:33.415882+00:00", CultureInfo.InvariantCulture),
            first.CreatedOn);
        Assert.Equal("Jane Doe", first.Author?.DisplayName);
        Assert.Equal("{9e1f2a3b-4c5d-6e7f-8091-a2b3c4d5e6f7}", first.Author?.Uuid);
        Assert.Equal("jane", first.Author?.Nickname);
        Assert.Equal("feature/retry-handler", first.Source?.Branch?.Name);
        Assert.Equal("a1b2c3d4e5f6", first.Source?.Commit?.Hash);
        Assert.Equal("acme/widget-api", first.Source?.Repository?.FullName);
        Assert.Equal("main", first.Destination?.Branch?.Name);

        var second = page.Items[1];
        Assert.True(second.Draft);
        Assert.Null(second.Author?.Nickname);
        Assert.Equal("sam-fork/widget-api", second.Source?.Repository?.FullName);

        // A fields=-trimmed or genuinely unset value is null, not a default: nothing here may
        // invent a zero or an empty author.
        var third = page.Items[2];
        Assert.Equal("MERGED", third.State);
        Assert.Null(third.Author);
        Assert.Null(third.Draft);
        Assert.Null(third.CommentCount);
        Assert.Null(third.CloseSourceBranch);
        Assert.Null(third.Source?.Commit);
        Assert.Null(third.Source?.Repository);
    }

    [Fact]
    public async Task ListPullRequestsSurvivesAPageWithoutValues()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson("""{"size":0}""");

        using var client = CreateClient(stub);

        var page = await client.ListPullRequestsAsync(
            Workspace,
            Repository,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(page.Items);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task ANextLinkOutsideTheApiDoesNotBecomeACursor()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson("""{"values":[],"next":"https://evil.example/2.0/pullrequests?page=2"}""");

        using var client = CreateClient(stub);

        var page = await client.ListPullRequestsAsync(
            Workspace,
            Repository,
            cancellationToken: TestContext.Current.CancellationToken);

        // Pagination stops rather than handing out a cursor that would be refused on the way back
        // in — "no more pages" is the safe direction to fail.
        Assert.Null(page.NextCursor);
    }

    // ------------------------------------------------------------ single item

    [Fact]
    public async Task GetPullRequestAsksForTheDetailFieldSetAndMapsReviewersAndParticipants()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(HttpFixtures.Read("http-pullrequest-detail.json"));

        using var client = CreateClient(stub);

        var pullRequest = await client.GetPullRequestAsync(
            Workspace,
            Repository,
            412,
            TestContext.Current.CancellationToken);

        var request = Assert.Single(stub.Requests);

        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/2.0/repositories/acme/widget-api/pullrequests/412", RequestUrl.Path(request.Uri));
        Assert.Equal(FieldSets.PullRequestDetail, RequestUrl.QueryValue(request.Uri, "fields"));

        Assert.Equal(412, pullRequest.Id);
        Assert.StartsWith("Retries 408/429/502/503/504", pullRequest.Description, StringComparison.Ordinal);

        Assert.NotNull(pullRequest.Reviewers);
        Assert.Equal(2, pullRequest.Reviewers.Count);
        Assert.Equal("{aaaabbbb-cccc-dddd-eeee-ffff00001111}", pullRequest.Reviewers[0].Uuid);

        Assert.NotNull(pullRequest.Participants);
        Assert.Equal(3, pullRequest.Participants.Count);

        Assert.True(pullRequest.Participants[0].Approved);
        Assert.Equal("approved", pullRequest.Participants[0].State);
        Assert.Equal("REVIEWER", pullRequest.Participants[0].Role);

        // The only place a change request is visible; note the lower-case wire value.
        Assert.Equal("changes_requested", pullRequest.Participants[1].State);
        Assert.False(pullRequest.Participants[1].Approved);

        Assert.Null(pullRequest.Participants[2].State);
        Assert.Equal("Jane Doe", pullRequest.Participants[2].User?.DisplayName);

        Assert.Null(pullRequest.MergeCommit);
        Assert.Null(pullRequest.ClosedBy);
    }

    [Fact]
    public async Task GetPullRequestToleratesAPullRequestWithNoReviewers()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(HttpFixtures.Read("http-pullrequest-detail-unreviewed.json"));

        using var client = CreateClient(stub);

        var pullRequest = await client.GetPullRequestAsync(
            Workspace,
            Repository,
            500,
            TestContext.Current.CancellationToken);

        // Bitbucket omits the arrays entirely rather than sending empty ones.
        Assert.Null(pullRequest.Reviewers);
        Assert.Null(pullRequest.Participants);
        Assert.Null(pullRequest.Description);
        Assert.Equal("docs/diffstat-first", pullRequest.Source?.Branch?.Name);
    }

    // --------------------------------------------------------------- diffstat

    [Fact]
    public async Task GetDiffStatAsksForTheDiffStatFieldSetAndMapsEveryFileStatus()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(HttpFixtures.Read("http-diffstat-page.json"));

        using var client = CreateClient(stub);

        var page = await client.GetDiffStatAsync(
            Workspace,
            Repository,
            412,
            pageSize: 5,
            cancellationToken: TestContext.Current.CancellationToken);

        var request = Assert.Single(stub.Requests);

        Assert.Equal("/2.0/repositories/acme/widget-api/pullrequests/412/diffstat", RequestUrl.Path(request.Uri));
        Assert.Equal(FieldSets.DiffStat, RequestUrl.QueryValue(request.Uri, "fields"));
        Assert.Equal("5", RequestUrl.QueryValue(request.Uri, "pagelen"));

        Assert.Equal(12, page.TotalSize);
        Assert.Equal(5, page.Items.Count);
        Assert.NotNull(page.NextCursor);

        Assert.Equal("modified", page.Items[0].Status);
        Assert.Equal(42, page.Items[0].LinesAdded);
        Assert.Equal(7, page.Items[0].LinesRemoved);
        Assert.Equal("src/Bitbucket.Mcp/Http/RetryHandler.cs", page.Items[0].New?.Path);

        Assert.Null(page.Items[1].Old);
        Assert.Equal("tests/Bitbucket.Mcp.Tests/Http/RetryHandlerTests.cs", page.Items[1].New?.Path);

        Assert.Null(page.Items[2].New);
        Assert.Equal("src/Bitbucket.Mcp/Http/LegacyBackoff.cs", page.Items[2].Old?.Path);

        Assert.Equal("renamed", page.Items[3].Status);
        Assert.Equal("docs/retry.md", page.Items[3].Old?.Path);
        Assert.Equal("docs/http/retry.md", page.Items[3].New?.Path);

        // A binary file reports zero on both sides; the path is what the diff endpoint needs back.
        Assert.Equal(0, page.Items[4].LinesAdded);
        Assert.Equal("assets/logo (draft).png", page.Items[4].New?.Path);
    }

    // ------------------------------------------------------------------- diff

    [Fact]
    public async Task GetDiffRepeatsThePathParameterAndAsksForText()
    {
        const string Diff = "diff --git a/src/A.cs b/src/A.cs\n--- a/src/A.cs\n+++ b/src/A.cs\n@@ -1 +1 @@\n-old\n+new\n";

        using var stub = new StubHttpMessageHandler();
        stub.Enqueue(HttpStatusCode.OK, Diff, "text/plain");

        using var client = CreateClient(stub);

        var diff = await client.GetDiffAsync(
            Workspace,
            Repository,
            412,
            paths: DiffPaths,
            contextLines: 3,
            ignoreWhitespace: true,
            cancellationToken: TestContext.Current.CancellationToken);

        var request = Assert.Single(stub.Requests);

        Assert.Equal(Diff, diff);
        Assert.Equal("/2.0/repositories/acme/widget-api/pullrequests/412/diff", RequestUrl.Path(request.Uri));

        // Repeated `path=`, not a comma-separated list — the whole diffstat-first workflow rests
        // on this being one parameter per file.
        Assert.Equal(DiffPaths, RequestUrl.QueryValues(request.Uri, "path"));

        Assert.Equal("3", RequestUrl.QueryValue(request.Uri, "context"));
        Assert.Equal("true", RequestUrl.QueryValue(request.Uri, "ignore_whitespace"));

        // No `fields=`: this endpoint answers with text, not JSON.
        Assert.Null(RequestUrl.QueryValue(request.Uri, "fields"));

        // A request-level Accept replaces the client-wide application/json default outright.
        Assert.Equal("text/plain", request.Headers["Accept"]);
    }

    [Fact]
    public async Task GetDiffSendsNoQueryAtAllForAWholePullRequest()
    {
        using var stub = new StubHttpMessageHandler();
        stub.Enqueue(HttpStatusCode.OK, "diff --git a/x b/x\n", "text/plain");

        using var client = CreateClient(stub);

        _ = await client.GetDiffAsync(
            Workspace,
            Repository,
            412,
            cancellationToken: TestContext.Current.CancellationToken);

        var request = Assert.Single(stub.Requests);

        Assert.Equal("/2.0/repositories/acme/widget-api/pullrequests/412/diff", RequestUrl.PathAndQuery(request.Uri));
        Assert.Empty(RequestUrl.Query(request.Uri));
    }

    [Fact]
    public async Task GetDiffSendsFalseForIgnoreWhitespaceRatherThanOmittingIt()
    {
        using var stub = new StubHttpMessageHandler();
        stub.Enqueue(HttpStatusCode.OK, "", "text/plain");

        using var client = CreateClient(stub);

        _ = await client.GetDiffAsync(
            Workspace,
            Repository,
            412,
            contextLines: 0,
            ignoreWhitespace: false,
            cancellationToken: TestContext.Current.CancellationToken);

        var request = Assert.Single(stub.Requests);

        Assert.Equal("0", RequestUrl.QueryValue(request.Uri, "context"));
        Assert.Equal("false", RequestUrl.QueryValue(request.Uri, "ignore_whitespace"));
    }

    // --------------------------------------------------------------- comments

    [Fact]
    public async Task GetCommentsMapsGeneralInlineResolvedRepliedAndDeletedComments()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(HttpFixtures.Read("http-comments-page.json"));

        using var client = CreateClient(stub);

        var page = await client.GetCommentsAsync(
            Workspace,
            Repository,
            412,
            pageSize: 5,
            cancellationToken: TestContext.Current.CancellationToken);

        var request = Assert.Single(stub.Requests);

        Assert.Equal("/2.0/repositories/acme/widget-api/pullrequests/412/comments", RequestUrl.Path(request.Uri));
        Assert.Equal(FieldSets.Comments, RequestUrl.QueryValue(request.Uri, "fields"));
        Assert.Equal("5", RequestUrl.QueryValue(request.Uri, "pagelen"));

        Assert.Equal(5, page.Items.Count);
        Assert.NotNull(page.NextCursor);

        var general = page.Items[0];
        Assert.Null(general.Inline);
        Assert.Null(general.Parent);
        Assert.False(general.Deleted);
        Assert.Equal("Ada Byron", general.User?.DisplayName);

        var inline = page.Items[1];
        Assert.Equal("src/Bitbucket.Mcp/Http/RetryHandler.cs", inline.Inline?.Path);
        Assert.Equal(75, inline.Inline?.To);
        Assert.Null(inline.Inline?.From);

        var resolved = page.Items[2];
        Assert.Equal(118, resolved.Inline?.From);
        Assert.Equal(112, resolved.Inline?.StartFrom);
        Assert.Null(resolved.Inline?.To);
        Assert.Equal("Jane Doe", resolved.Resolution?.User?.DisplayName);
        Assert.Equal(
            DateTimeOffset.Parse("2026-07-16T10:05:00+00:00", CultureInfo.InvariantCulture),
            resolved.Resolution?.CreatedOn);

        var reply = page.Items[3];
        Assert.Equal(900002, reply.Parent?.Id);
        Assert.Equal(900004, reply.Id);

        // Deleted comments still arrive, with their content blanked, so the thread keeps its
        // shape; filtering them out is the caller's job, not the client's.
        var deleted = page.Items[4];
        Assert.True(deleted.Deleted);
        Assert.Equal(string.Empty, deleted.Content?.Raw);
    }

    // ------------------------------------------------------------ path safety

    [Theory]
    [InlineData("../../evil", "widget-api", "/2.0/repositories/..%2F..%2Fevil/widget-api/pullrequests")]
    [InlineData("acme", "my/repo", "/2.0/repositories/acme/my%2Frepo/pullrequests")]
    [InlineData("a b", "c?d#e", "/2.0/repositories/a%20b/c%3Fd%23e/pullrequests")]
    [InlineData("acme", "%2e%2e", "/2.0/repositories/acme/%252e%252e/pullrequests")]
    [InlineData("ünïcode", "repo", "/2.0/repositories/%C3%BCn%C3%AFcode/repo/pullrequests")]
    public async Task PathSegmentsAreEscapedExactlyOnce(string workspace, string slug, string expectedPath)
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(EmptyPage);

        using var client = CreateClient(stub);

        _ = await client.ListPullRequestsAsync(
            workspace,
            slug,
            cancellationToken: TestContext.Current.CancellationToken);

        // A slug arrives from the model and may be anything; escaping is what stops it walking out
        // of the repository's namespace or smuggling a query on to the end of the URL.
        Assert.Equal(expectedPath, RequestUrl.Path(Assert.Single(stub.Requests).Uri));
    }

    /// <summary>
    /// <c>.</c> is unreserved, so <see cref="Uri.EscapeDataString(string)"/> leaves a dot segment untouched
    /// and RFC 3986 dot-segment removal collapses the path when the relative URL is resolved
    /// against the base address — <c>workspace=".."</c> with <c>slug=".."</c> would otherwise
    /// request <c>/pullrequests</c>, outside the <c>/2.0/</c> prefix entirely. Escaping cannot fix
    /// that, so the builder refuses the segment instead.
    /// </summary>
    [Theory]
    [InlineData("..", "..")]
    [InlineData("..", "widget-api")]
    [InlineData("acme", ".")]
    [InlineData(".", "widget-api")]
    [InlineData("acme", "..")]
    [InlineData("  ..  ", "widget-api")]
    public async Task ADotSegmentIsRejectedBeforeAnyRequest(string workspace, string slug)
    {
        using var stub = new StubHttpMessageHandler();
        using var client = CreateClient(stub);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => client.ListPullRequestsAsync(
            workspace,
            slug,
            cancellationToken: TestContext.Current.CancellationToken));

        // The message is model-facing: it has to say what a slug is, not that a guard tripped.
        Assert.Contains("must be real slugs", exception.Message, StringComparison.Ordinal);
        Assert.Empty(stub.Requests);
    }

    [Theory]
    [InlineData(null, "repo")]
    [InlineData("", "repo")]
    [InlineData("   ", "repo")]
    [InlineData("acme", null)]
    [InlineData("acme", "")]
    [InlineData("acme", "  ")]
    public async Task ABlankWorkspaceOrSlugIsRejectedBeforeAnyRequest(string? workspace, string? slug)
    {
        using var stub = new StubHttpMessageHandler();
        using var client = CreateClient(stub);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => client.ListPullRequestsAsync(
            workspace!,
            slug!,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Empty(stub.Requests);
    }

    /// <summary>
    /// The guard is on the segment, not on <c>listPullRequests</c>: every client method composes
    /// its URL through the same builder, so none of them can be talked out of the <c>/2.0/</c>
    /// prefix.
    /// </summary>
    [Fact]
    public async Task EveryEndpointInheritsTheSlugGuard()
    {
        using var stub = new StubHttpMessageHandler();
        using var client = CreateClient(stub);
        var cancellationToken = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.GetPullRequestAsync("..", Repository, 412, cancellationToken));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.GetDiffAsync(Workspace, "..", 412, cancellationToken: cancellationToken));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.GetDiffStatAsync("..", Repository, 412, cancellationToken: cancellationToken));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.GetCommentsAsync(Workspace, ".", 412, cancellationToken: cancellationToken));

        await Assert.ThrowsAsync<ArgumentException>(() => client.CreatePullRequestAsync(
            ".",
            Repository,
            new CreatePullRequestRequest
            {
                Title = "x",
                Source = new PullRequestEndpointRequest { Branch = new BranchRequest { Name = "feature/x" } },
            },
            cancellationToken));

        await Assert.ThrowsAsync<ArgumentException>(() => client.UpdatePullRequestAsync(
            Workspace, "..", 412, new UpdatePullRequestRequest { Title = "x" }, cancellationToken));

        await Assert.ThrowsAsync<ArgumentException>(() => client.AddCommentAsync(
            "..",
            Repository,
            412,
            new CommentRequest { Content = new CommentContentRequest { Raw = "x" } },
            cancellationToken));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.ApproveAsync(Workspace, "..", 412, cancellationToken));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.UnapproveAsync("..", Repository, 412, cancellationToken));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.RequestChangesAsync(Workspace, ".", 412, cancellationToken));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.UnrequestChangesAsync(".", Repository, 412, cancellationToken));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.MergeAsync("..", Repository, 412, new MergeRequest(), cancellationToken));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.DeclineAsync(Workspace, "..", 412, cancellationToken: cancellationToken));

        Assert.Empty(stub.Requests);
    }

    // ----------------------------------------------------------- write bodies

    [Fact]
    public async Task CreatePullRequestOmitsEveryUnsetField()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(HttpFixtures.Read("http-pullrequest-detail.json"), HttpStatusCode.Created);

        using var client = CreateClient(stub);

        _ = await client.CreatePullRequestAsync(
            Workspace,
            Repository,
            new CreatePullRequestRequest
            {
                Title = "Add retry handler for 429 responses",
                Source = new PullRequestEndpointRequest { Branch = new BranchRequest { Name = "feature/retry-handler" } },
            },
            TestContext.Current.CancellationToken);

        var request = Assert.Single(stub.Requests);

        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/2.0/repositories/acme/widget-api/pullrequests", RequestUrl.Path(request.Uri));
        Assert.Equal(FieldSets.PullRequestDetail, RequestUrl.QueryValue(request.Uri, "fields"));
        Assert.Equal("application/json; charset=utf-8", request.Headers["Content-Type"]);

        // A null property must be absent, not `null`: Bitbucket treats an explicit null as "clear
        // this field".
        Assert.Equal(
            """{"title":"Add retry handler for 429 responses","source":{"branch":{"name":"feature/retry-handler"}}}""",
            request.Body);
    }

    [Fact]
    public async Task CreatePullRequestSendsEveryFieldItWasGiven()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(HttpFixtures.Read("http-pullrequest-detail.json"), HttpStatusCode.Created);

        using var client = CreateClient(stub);

        _ = await client.CreatePullRequestAsync(
            Workspace,
            Repository,
            new CreatePullRequestRequest
            {
                Title = "Add retry handler",
                Source = new PullRequestEndpointRequest { Branch = new BranchRequest { Name = "feature/retry-handler" } },
                Destination = new PullRequestEndpointRequest { Branch = new BranchRequest { Name = "main" } },
                Description = "Retries the transient statuses.",
                CloseSourceBranch = true,
                Draft = false,
                Reviewers = [new AccountRefRequest { Uuid = "{aaaabbbb-cccc-dddd-eeee-ffff00001111}" }],
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(
            """{"title":"Add retry handler","source":{"branch":{"name":"feature/retry-handler"}},"destination":{"branch":{"name":"main"}},"description":"Retries the transient statuses.","close_source_branch":true,"draft":false,"reviewers":[{"uuid":"{aaaabbbb-cccc-dddd-eeee-ffff00001111}"}]}""",
            Assert.Single(stub.Requests).Body);
    }

    [Fact]
    public async Task UpdatePullRequestSendsOnlyTheFieldsBeingChanged()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(HttpFixtures.Read("http-pullrequest-detail.json"));

        using var client = CreateClient(stub);

        _ = await client.UpdatePullRequestAsync(
            Workspace,
            Repository,
            412,
            new UpdatePullRequestRequest { Title = "Add retry handler (v2)" },
            TestContext.Current.CancellationToken);

        var request = Assert.Single(stub.Requests);

        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal("/2.0/repositories/acme/widget-api/pullrequests/412", RequestUrl.Path(request.Uri));
        Assert.Equal(FieldSets.PullRequestDetail, RequestUrl.QueryValue(request.Uri, "fields"));

        // Anything omitted keeps its current value; sending nulls would blank the description.
        Assert.Equal("""{"title":"Add retry handler (v2)"}""", request.Body);
    }

    [Fact]
    public async Task AddCommentPostsAGeneralComment()
    {
        var request = await PostCommentAsync(new CommentRequest
        {
            Content = new CommentContentRequest { Raw = "Looks good to me." },
        });

        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/2.0/repositories/acme/widget-api/pullrequests/412/comments", RequestUrl.Path(request.Uri));
        Assert.Equal(FieldSets.Comment, RequestUrl.QueryValue(request.Uri, "fields"));
        Assert.Equal("""{"content":{"raw":"Looks good to me."}}""", request.Body);
    }

    [Fact]
    public async Task AddCommentPostsAnInlineComment()
    {
        var request = await PostCommentAsync(new CommentRequest
        {
            Content = new CommentContentRequest { Raw = "This needs a test." },
            Inline = new InlineDto { Path = "src/Bitbucket.Mcp/Http/RetryHandler.cs", To = 218 },
        });

        Assert.Equal(
            """{"content":{"raw":"This needs a test."},"inline":{"path":"src/Bitbucket.Mcp/Http/RetryHandler.cs","to":218}}""",
            request.Body);
    }

    [Fact]
    public async Task AddCommentPostsAMultiLineCommentOnTheOldSide()
    {
        var request = await PostCommentAsync(new CommentRequest
        {
            Content = new CommentContentRequest { Raw = "This block moved." },
            Inline = new InlineDto { Path = "src/A.cs", From = 118, StartFrom = 112 },
        });

        Assert.Equal(
            """{"content":{"raw":"This block moved."},"inline":{"path":"src/A.cs","from":118,"start_from":112}}""",
            request.Body);
    }

    [Fact]
    public async Task AddCommentPostsAReply()
    {
        var request = await PostCommentAsync(new CommentRequest
        {
            Content = new CommentContentRequest { Raw = "Agreed." },
            Parent = new CommentParentRequest { Id = 900002 },
        });

        Assert.Equal("""{"content":{"raw":"Agreed."},"parent":{"id":900002}}""", request.Body);
    }

    // --------------------------------------------------------- review actions

    [Fact]
    public async Task ApprovePostsToApproveAndReturnsTheCallersParticipantEntry()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(HttpFixtures.Read("http-participant-approved.json"));

        using var client = CreateClient(stub);

        var participant = await client.ApproveAsync(
            Workspace,
            Repository,
            412,
            TestContext.Current.CancellationToken);

        var request = Assert.Single(stub.Requests);

        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/2.0/repositories/acme/widget-api/pullrequests/412/approve", RequestUrl.Path(request.Uri));
        Assert.Equal(FieldSets.Participant, RequestUrl.QueryValue(request.Uri, "fields"));

        Assert.True(participant.Approved);
        Assert.Equal("approved", participant.State);
        Assert.Equal("Ada Byron", participant.User?.DisplayName);
    }

    [Fact]
    public async Task RequestChangesPostsToRequestChanges()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson("""{"role":"REVIEWER","approved":false,"state":"changes_requested"}""");

        using var client = CreateClient(stub);

        var participant = await client.RequestChangesAsync(
            Workspace,
            Repository,
            412,
            TestContext.Current.CancellationToken);

        var request = Assert.Single(stub.Requests);

        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/2.0/repositories/acme/widget-api/pullrequests/412/request-changes", RequestUrl.Path(request.Uri));
        Assert.Equal(FieldSets.Participant, RequestUrl.QueryValue(request.Uri, "fields"));
        Assert.Equal("changes_requested", participant.State);
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("request-changes")]
    public async Task WithdrawingAReviewIsADeleteWithNoFieldsAndNoBody(string segment)
    {
        using var stub = new StubHttpMessageHandler();
        stub.Enqueue(HttpStatusCode.NoContent);

        using var client = CreateClient(stub);

        if (segment == "approve")
        {
            await client.UnapproveAsync(Workspace, Repository, 412, TestContext.Current.CancellationToken);
        }
        else
        {
            await client.UnrequestChangesAsync(Workspace, Repository, 412, TestContext.Current.CancellationToken);
        }

        var request = Assert.Single(stub.Requests);

        Assert.Equal(HttpMethod.Delete, request.Method);

        // No `fields=`: a 204 has nothing to shape.
        Assert.Equal($"/2.0/repositories/acme/widget-api/pullrequests/412/{segment}", RequestUrl.PathAndQuery(request.Uri));
        Assert.Null(request.Body);
    }

    // ------------------------------------------------------------------ merge

    [Fact]
    public async Task MergeAsksForTheTaskStatusAlongsideThePullRequest()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(HttpFixtures.Read("http-pullrequest-merged.json"));

        using var client = CreateClient(stub);

        var merged = await client.MergeAsync(
            Workspace,
            Repository,
            412,
            new MergeRequest { Message = "Merged via MCP", CloseSourceBranch = true, MergeStrategy = "squash" },
            TestContext.Current.CancellationToken);

        var request = Assert.Single(stub.Requests);

        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/2.0/repositories/acme/widget-api/pullrequests/412/merge", RequestUrl.Path(request.Uri));

        // A queued merge answers with nothing but task_status; leaving it out of the field list
        // would leave the "merge was queued" error with nothing to quote.
        Assert.Equal(
            FieldSets.PullRequestDetail + ",task_status",
            RequestUrl.QueryValue(request.Uri, "fields"));

        Assert.Equal(
            """{"message":"Merged via MCP","close_source_branch":true,"merge_strategy":"squash"}""",
            request.Body);

        Assert.Equal("MERGED", merged.State);
        Assert.Equal("fedcba987654", merged.MergeCommit?.Hash);
        Assert.Equal("Ada Byron", merged.ClosedBy?.DisplayName);
    }

    [Fact]
    public async Task AQueuedMergeIsReportedAsAnActionableFailureRatherThanPolled()
    {
        var body = HttpFixtures.Read("http-merge-queued.json");

        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(body, HttpStatusCode.Accepted);

        using var client = CreateClient(stub);

        var exception = await Assert.ThrowsAsync<BitbucketApiException>(() => client.MergeAsync(
            Workspace,
            Repository,
            412,
            new MergeRequest(),
            TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.Accepted, exception.StatusCode);
        Assert.Equal(body, exception.RawBody);

        // The message has to say what happened and what to do; re-running a merge that may have
        // succeeded is the expensive mistake here.
        Assert.Contains("queued", exception.Message, StringComparison.Ordinal);
        Assert.Contains("task status PENDING", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Bitbucket UI", exception.Message, StringComparison.Ordinal);
        Assert.Contains("only re-run the merge if it did not", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptyMergeRequestSerialisesToAnEmptyObject()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(HttpFixtures.Read("http-pullrequest-merged.json"));

        using var client = CreateClient(stub);

        _ = await client.MergeAsync(
            Workspace,
            Repository,
            412,
            new MergeRequest(),
            TestContext.Current.CancellationToken);

        Assert.Equal("{}", Assert.Single(stub.Requests).Body);
    }

    // ---------------------------------------------------------------- decline

    [Fact]
    public async Task DeclinePostsTheReasonBestEffort()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(HttpFixtures.Read("http-pullrequest-detail.json"));

        using var client = CreateClient(stub);

        _ = await client.DeclineAsync(
            Workspace,
            Repository,
            412,
            "Superseded by #500",
            TestContext.Current.CancellationToken);

        var request = Assert.Single(stub.Requests);

        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/2.0/repositories/acme/widget-api/pullrequests/412/decline", RequestUrl.Path(request.Uri));
        Assert.Equal(FieldSets.PullRequestDetail, RequestUrl.QueryValue(request.Uri, "fields"));
        Assert.Equal("""{"reason":"Superseded by #500"}""", request.Body);
    }

    [Fact]
    public async Task DeclineWithoutAReasonPostsAnEmptyObject()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(HttpFixtures.Read("http-pullrequest-detail.json"));

        using var client = CreateClient(stub);

        _ = await client.DeclineAsync(
            Workspace,
            Repository,
            412,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("{}", Assert.Single(stub.Requests).Body);
    }

    // ----------------------------------------------------------------- errors

    [Fact]
    public async Task AValidationFailureCarriesTheParsedEnvelopeAndTheRawBody()
    {
        var body = HttpFixtures.Read("http-error-validation.json");

        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(body, HttpStatusCode.BadRequest);

        using var client = CreateClient(stub);

        var exception = await Assert.ThrowsAsync<BitbucketApiException>(() => client.CreatePullRequestAsync(
            Workspace,
            Repository,
            new CreatePullRequestRequest
            {
                Title = "x",
                Source = new PullRequestEndpointRequest { Branch = new BranchRequest { Name = "feature/does-not-exist" } },
            },
            TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
        Assert.Equal(body, exception.RawBody);
        Assert.Equal(0, exception.RetryAttempts);

        Assert.Equal("title: This field is required.", exception.Error?.Error?.Message);
        Assert.StartsWith("The pull request could not be created", exception.Error?.Error?.Detail, StringComparison.Ordinal);

        // The per-field errors are the useful part of a rejected write.
        var fields = exception.Error?.Error?.Fields;
        Assert.NotNull(fields);
        Assert.Equal(TitleFieldErrors, fields["title"]);
        Assert.Equal(2, fields["source"].Count);

        Assert.Contains("HTTP 400 (BadRequest)", exception.Message, StringComparison.Ordinal);
        Assert.Contains("title: This field is required.", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BitbucketsNonStandard555KeepsItsNumericStatus()
    {
        var body = HttpFixtures.Read("http-error-555-diff-too-large.json");

        using var stub = new StubHttpMessageHandler();
        stub.Enqueue((HttpStatusCode) 555, body);

        using var client = CreateClient(stub);

        var exception = await Assert.ThrowsAsync<BitbucketApiException>(() => client.GetDiffAsync(
            Workspace,
            Repository,
            412,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(555, (int) exception.StatusCode);
        Assert.Equal(body, exception.RawBody);
        Assert.Equal("Diff too large to render.", exception.Error?.Error?.Message);

        // 555 has no name in HttpStatusCode, so the message must not invent one.
        Assert.Contains("HTTP 555.", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("(555)", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Diff too large to render.", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANonJsonErrorBodyIsToleratedAndKeptVerbatim()
    {
        const string Html = "<html>\n  <head><title>503 Service Unavailable</title></head>\n  <body>Bitbucket is down.</body>\n</html>";

        using var stub = new StubHttpMessageHandler();

        // Every attempt fails, so the retry budget is spent before the exception is built.
        stub.Fallback = _ => StubHttpMessageHandler.CreateResponse(HttpStatusCode.ServiceUnavailable, Html, "text/html");

        using var client = CreateClient(stub);

        var exception = await Assert.ThrowsAsync<BitbucketApiException>(() => client.GetPullRequestAsync(
            Workspace,
            Repository,
            412,
            TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.StatusCode);
        Assert.Null(exception.Error);
        Assert.Equal(Html, exception.RawBody);

        // Retries are reported so a 5xx message can say the request was not simply unlucky once.
        Assert.Equal(RetryHandler.MaxAttempts - 1, exception.RetryAttempts);
        Assert.Contains("after 3 retries", exception.Message, StringComparison.Ordinal);

        // The message quotes a collapsed single-line snippet, never the raw multi-line page.
        Assert.False(exception.Message.Contains('\n', StringComparison.Ordinal));
    }

    /// <summary>
    /// Both documented forms of <c>Retry-After</c> reach the exception as seconds, so the 429 tool
    /// error can name the wait instead of guessing at it. Ninety seconds is past
    /// <see cref="RetryHandler.MaxRetryAfter"/>, so the response comes back unretried — which is
    /// exactly the case where the caller has to be told how long is left.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARetryAfterOnTheFinalResponseTravelsOnTheException(bool asHttpDate)
    {
        var time = new ManualTimeProvider();

        using var stub = new StubHttpMessageHandler();
        stub.Enqueue(_ =>
        {
            var response = StubHttpMessageHandler.CreateResponse(
                HttpStatusCode.TooManyRequests,
                """{"type":"error","error":{"message":"Rate limit exceeded"}}""");

            response.Headers.RetryAfter = asHttpDate
                ? new RetryConditionHeaderValue(time.GetUtcNow() + TimeSpan.FromSeconds(90))
                : new RetryConditionHeaderValue(TimeSpan.FromSeconds(90));

            return response;
        });

        using var client = CreateClient(stub, timeProvider: time);

        var exception = await Assert.ThrowsAsync<BitbucketApiException>(() => client.GetPullRequestAsync(
            Workspace,
            Repository,
            412,
            TestContext.Current.CancellationToken));

        Assert.Single(stub.Requests);
        Assert.Equal(HttpStatusCode.TooManyRequests, exception.StatusCode);
        Assert.Equal(90, exception.RetryAfterSeconds);
        Assert.Equal(0, exception.RetryAttempts);
    }

    [Fact]
    public async Task AFailureWithoutARetryAfterHeaderReportsNoWait()
    {
        using var stub = new StubHttpMessageHandler();
        stub.Fallback = _ => StubHttpMessageHandler.CreateResponse(
            HttpStatusCode.TooManyRequests,
            """{"type":"error","error":{"message":"Rate limit exceeded"}}""");

        using var client = CreateClient(stub);

        var exception = await Assert.ThrowsAsync<BitbucketApiException>(() => client.GetPullRequestAsync(
            Workspace,
            Repository,
            412,
            TestContext.Current.CancellationToken));

        // Null rather than a default: "Bitbucket did not say" and "Bitbucket said zero" are
        // different pieces of advice.
        Assert.Null(exception.RetryAfterSeconds);
        Assert.Equal(RetryHandler.MaxAttempts - 1, exception.RetryAttempts);
    }

    [Fact]
    public async Task AnEmptyBodyOnASuccessIsAFailure()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson("");

        using var client = CreateClient(stub);

        var exception = await Assert.ThrowsAsync<BitbucketApiException>(() => client.GetPullRequestAsync(
            Workspace,
            Repository,
            412,
            TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.OK, exception.StatusCode);
        Assert.Contains("could not parse", exception.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- headers

    [Fact]
    public async Task EveryRequestIdentifiesTheServerAndItsRepository()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(EmptyPage);

        using var client = CreateClient(stub);

        _ = await client.ListPullRequestsAsync(
            Workspace,
            Repository,
            cancellationToken: TestContext.Current.CancellationToken);

        var request = Assert.Single(stub.Requests);
        var userAgent = request.Headers["User-Agent"];

        Assert.StartsWith($"{ServerVersion.Name}/{ServerVersion.Value}", userAgent, StringComparison.Ordinal);
        Assert.Contains(ProjectUrl, userAgent, StringComparison.Ordinal);
        Assert.Equal("application/json", request.Headers["Accept"]);
    }

    [Fact]
    public async Task TheAuthorizationHeaderComesFromTheCredentialProviderPerRequest()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(EmptyPage);
        stub.EnqueueJson(EmptyPage);

        var credentials = new StubCredentialProvider("token-1");
        using var client = CreateClient(stub, credentials);

        _ = await client.ListPullRequestsAsync(Workspace, Repository, cancellationToken: TestContext.Current.CancellationToken);
        _ = await client.ListPullRequestsAsync(Workspace, Repository, cancellationToken: TestContext.Current.CancellationToken);

        Assert.All(stub.Requests, request => Assert.Equal("Bearer token-1", request.Headers["Authorization"]));

        // Per request, never a default header: a default one would survive the 302 the diff
        // endpoints answer with and hand the credential to another host.
        Assert.Equal(2, credentials.HeaderRequestCount);
        Assert.Equal(0, credentials.InvalidateCount);
    }

    [Fact]
    public async Task A401InvalidatesTheCredentialAndRetriesOnceWithAFreshHeader()
    {
        using var stub = new StubHttpMessageHandler();
        stub.Enqueue(HttpStatusCode.Unauthorized, """{"type":"error","error":{"message":"Invalid token"}}""");
        stub.EnqueueJson(HttpFixtures.Read("http-pullrequest-detail.json"));

        var credentials = new StubCredentialProvider("stale-token", "fresh-token");
        using var client = CreateClient(stub, credentials);

        var pullRequest = await client.GetPullRequestAsync(
            Workspace,
            Repository,
            412,
            TestContext.Current.CancellationToken);

        Assert.Equal(412, pullRequest.Id);

        Assert.Equal(2, stub.Requests.Count);
        Assert.Equal("Bearer stale-token", stub.Requests[0].Headers["Authorization"]);
        Assert.Equal("Bearer fresh-token", stub.Requests[1].Headers["Authorization"]);

        Assert.Equal(1, credentials.InvalidateCount);
        Assert.Equal(2, credentials.HeaderRequestCount);
    }

    [Fact]
    public async Task ASecond401IsReportedRatherThanRetriedAgain()
    {
        using var stub = new StubHttpMessageHandler();
        stub.Fallback = _ => StubHttpMessageHandler.CreateResponse(
            HttpStatusCode.Unauthorized,
            """{"type":"error","error":{"message":"Invalid token"}}""");

        var credentials = new StubCredentialProvider("stale-token", "also-wrong");
        using var client = CreateClient(stub, credentials);

        var exception = await Assert.ThrowsAsync<BitbucketApiException>(() => client.GetPullRequestAsync(
            Workspace,
            Repository,
            412,
            TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
        Assert.Equal("Invalid token", exception.Error?.Error?.Message);

        // Exactly one retry: a freshly acquired credential that is also rejected is wrong rather
        // than stale, and looping would only turn a clear 401 into a slow one. A 401 is also never
        // retried by the backoff handler.
        Assert.Equal(2, stub.Requests.Count);
        Assert.Equal(1, credentials.InvalidateCount);
        Assert.Equal(0, exception.RetryAttempts);
    }

    private static async Task<RecordedRequest> PostCommentAsync(CommentRequest body)
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(HttpFixtures.Read("http-comment-created.json"), HttpStatusCode.Created);

        using var client = CreateClient(stub);

        _ = await client.AddCommentAsync(
            Workspace,
            Repository,
            412,
            body,
            TestContext.Current.CancellationToken);

        return Assert.Single(stub.Requests);
    }

    private static BitbucketApiClient CreateClient(
        StubHttpMessageHandler stub,
        StubCredentialProvider? credentials = null,
        ManualTimeProvider? timeProvider = null) =>
        new(credentials ?? new StubCredentialProvider(),
            NullLoggerFactory.Instance,
            stub,
            baseAddress: null,
            timeProvider: timeProvider ?? new ManualTimeProvider());
}
