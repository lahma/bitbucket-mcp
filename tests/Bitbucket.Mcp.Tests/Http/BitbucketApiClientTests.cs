using System.Globalization;
using System.Net;
using System.Net.Http.Headers;

using Bitbucket.Mcp.Http;
using Bitbucket.Mcp.Http.Models;

using Microsoft.Extensions.Logging.Abstractions;


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
/// The stub replaces the innermost handler, but redirect following lives in
/// <c>AuthenticationHandler</c> rather than in the transport (D16) — so a stubbed <c>302</c>
/// followed by a stubbed <c>200</c> goes through the same code the diff endpoints go through in
/// production. <see cref="AuthenticationHandlerTests"/> covers the hop itself; the two tests here
/// check that the client surface comes out the other side with its result.
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
    public static object[][] PageSizes =>
    [
        [-5, "1"],
        [0, "1"],
        [1, "1"],
        [50, "50"],
        [100, "100"],
        [101, "100"],
        [5000, "100"],
    ];

    // ---------------------------------------------------------------- listing

    [Test]
    public async Task ListPullRequestsAsksForTheListFieldSetAndNothingElse()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(EmptyPage);

        using var client = CreateClient(stub);

        _ = await client.ListPullRequestsAsync(
            Workspace,
            Repository,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var request = Expect.Single(stub.Requests);

        Assert.That(request.Method, Is.EqualTo(HttpMethod.Get));
        Assert.That(RequestUrl.Path(request.Uri), Is.EqualTo("/2.0/repositories/acme/widget-api/pullrequests"));

        // Exactly the constant, so a field set edited without editing FieldSets shows up here.
        Assert.That(RequestUrl.QueryValue(request.Uri, "fields"), Is.EqualTo(FieldSets.PullRequestList));

        // Unset filters are absent rather than empty: "" is a different request to Bitbucket.
        Assert.That(Expect.Single(RequestUrl.Query(request.Uri)).Key, Is.EqualTo("fields"));

        // Commas are escaped on the wire even though the decoded value reads as a plain list.
        Assert.That(RequestUrl.PathAndQuery(request.Uri), Does.Contain("%2C"));
    }

    [TestCaseSource(nameof(PageSizes))]
    public async Task ListPullRequestsClampsPageSizeToBitbucketsCeiling(int pageSize, string expected)
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(EmptyPage);

        using var client = CreateClient(stub);

        _ = await client.ListPullRequestsAsync(
            Workspace,
            Repository,
            pageSize: pageSize,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(RequestUrl.QueryValue(Expect.Single(stub.Requests).Uri, "pagelen"), Is.EqualTo(expected));
    }

    [TestCase(new[] { "OPEN" }, "state = \"OPEN\"")]
    [TestCase(new[] { "open" }, "state = \"OPEN\"")]
    [TestCase(new[] { "OPEN", "MERGED" }, "(state = \"OPEN\" OR state = \"MERGED\")")]
    [TestCase(new[] { "OPEN", "MERGED", "DECLINED" }, "(state = \"OPEN\" OR state = \"MERGED\" OR state = \"DECLINED\")")]
    [TestCase(new[] { "OPEN", "  " }, "state = \"OPEN\"")]
    public async Task ListPullRequestsComposesStateClauses(string[] states, string expected)
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(EmptyPage);

        using var client = CreateClient(stub);

        _ = await client.ListPullRequestsAsync(
            Workspace,
            Repository,
            states: states,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(RequestUrl.QueryValue(Expect.Single(stub.Requests).Uri, "q"), Is.EqualTo(expected));
    }

    [TestCase("{9e1f2a3b-4c5d-6e7f-8091-a2b3c4d5e6f7}", "author.uuid = \"{9e1f2a3b-4c5d-6e7f-8091-a2b3c4d5e6f7}\"")]
    [TestCase("jane", "author.nickname = \"jane\"")]
    [TestCase("  jane  ", "author.nickname = \"jane\"")]
    public async Task ListPullRequestsMatchesAuthorsByUuidOrNickname(string author, string expected)
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(EmptyPage);

        using var client = CreateClient(stub);

        _ = await client.ListPullRequestsAsync(
            Workspace,
            Repository,
            author: author,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(RequestUrl.QueryValue(Expect.Single(stub.Requests).Uri, "q"), Is.EqualTo(expected));
    }

    [Test]
    public async Task ListPullRequestsQuotesAndEscapesAuthorValues()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(EmptyPage);

        using var client = CreateClient(stub);

        _ = await client.ListPullRequestsAsync(
            Workspace,
            Repository,
            author: "ja\"ne\\x",
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        // BBQL string literals are double-quoted; a quote or a backslash in the value has to be
        // escaped or the filter parses as something else entirely.
        Assert.That(RequestUrl.QueryValue(Expect.Single(stub.Requests).Uri, "q"), Is.EqualTo("author.nickname = \"ja\\\"ne\\\\x\""));
    }

    [Test]
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
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var request = Expect.Single(stub.Requests);

        // The caller's fragment is wrapped so its OR cannot rearrange the state and author
        // clauses it is ANDed with.
        Assert.That(RequestUrl.QueryValue(request.Uri, "q"), Is.EqualTo("(state = \"OPEN\" OR state = \"MERGED\") AND author.nickname = \"jane\" "
            + "AND (title ~ \"retry\" OR reviewers.nickname = \"ada\")"));

        Assert.That(RequestUrl.QueryValue(request.Uri, "sort"), Is.EqualTo("-updated_on"));
    }

    [Test]
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
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(RequestUrl.QueryValue(Expect.Single(stub.Requests).Uri, "q"), Is.Null);
    }

    [Test]
    public async Task ListPullRequestsMapsTheFixturePage()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(HttpFixtures.Read("http-pullrequest-list.json"));

        using var client = CreateClient(stub);

        var page = await client.ListPullRequestsAsync(
            Workspace,
            Repository,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(page.TotalSize, Is.EqualTo(47));
        Assert.That(page.Items.Count, Is.EqualTo(3));

        Assert.That(page.NextCursor, Is.Not.Null);
        Assert.That(BitbucketCursor.TryDecode(page.NextCursor, out var next), Is.True);
        Assert.That(next, Is.EqualTo("https://api.bitbucket.org/2.0/repositories/acme/widget-api/pullrequests?fields=next%2Csize%2Cvalues.id&page=2&pagelen=3"));

        var first = page.Items[0];
        Assert.That(first.Id, Is.EqualTo(412));
        Assert.That(first.Title, Is.EqualTo("Add retry handler for 429 responses"));
        Assert.That(first.State, Is.EqualTo("OPEN"));
        Assert.That(first.Draft, Is.False);
        Assert.That(first.CommentCount, Is.EqualTo(7));
        Assert.That(first.TaskCount, Is.EqualTo(1));
        Assert.That(first.CloseSourceBranch, Is.True);
        Assert.That(first.CreatedOn, Is.EqualTo(DateTimeOffset.Parse("2026-07-14T09:12:33.415882+00:00", CultureInfo.InvariantCulture)));
        Assert.That(first.Author?.DisplayName, Is.EqualTo("Jane Doe"));
        Assert.That(first.Author?.Uuid, Is.EqualTo("{9e1f2a3b-4c5d-6e7f-8091-a2b3c4d5e6f7}"));
        Assert.That(first.Author?.Nickname, Is.EqualTo("jane"));
        Assert.That(first.Source?.Branch?.Name, Is.EqualTo("feature/retry-handler"));
        Assert.That(first.Source?.Commit?.Hash, Is.EqualTo("a1b2c3d4e5f6"));
        Assert.That(first.Source?.Repository?.FullName, Is.EqualTo("acme/widget-api"));
        Assert.That(first.Destination?.Branch?.Name, Is.EqualTo("main"));

        var second = page.Items[1];
        Assert.That(second.Draft, Is.True);
        Assert.That(second.Author?.Nickname, Is.Null);
        Assert.That(second.Source?.Repository?.FullName, Is.EqualTo("sam-fork/widget-api"));

        // A fields=-trimmed or genuinely unset value is null, not a default: nothing here may
        // invent a zero or an empty author.
        var third = page.Items[2];
        Assert.That(third.State, Is.EqualTo("MERGED"));
        Assert.That(third.Author, Is.Null);
        Assert.That(third.Draft, Is.Null);
        Assert.That(third.CommentCount, Is.Null);
        Assert.That(third.CloseSourceBranch, Is.Null);
        Assert.That(third.Source?.Commit, Is.Null);
        Assert.That(third.Source?.Repository, Is.Null);
    }

    [Test]
    public async Task ListPullRequestsSurvivesAPageWithoutValues()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson("""{"size":0}""");

        using var client = CreateClient(stub);

        var page = await client.ListPullRequestsAsync(
            Workspace,
            Repository,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(page.Items, Is.Empty);
        Assert.That(page.NextCursor, Is.Null);
    }

    [Test]
    public async Task ANextLinkOutsideTheApiDoesNotBecomeACursor()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson("""{"values":[],"next":"https://evil.example/2.0/pullrequests?page=2"}""");

        using var client = CreateClient(stub);

        var page = await client.ListPullRequestsAsync(
            Workspace,
            Repository,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        // Pagination stops rather than handing out a cursor that would be refused on the way back
        // in — "no more pages" is the safe direction to fail.
        Assert.That(page.NextCursor, Is.Null);
    }

    // ------------------------------------------------------------ single item

    [Test]
    public async Task GetPullRequestAsksForTheDetailFieldSetAndMapsReviewersAndParticipants()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(HttpFixtures.Read("http-pullrequest-detail.json"));

        using var client = CreateClient(stub);

        var pullRequest = await client.GetPullRequestAsync(
            Workspace,
            Repository,
            412,
            TestContext.CurrentContext.CancellationToken);

        var request = Expect.Single(stub.Requests);

        Assert.That(request.Method, Is.EqualTo(HttpMethod.Get));
        Assert.That(RequestUrl.Path(request.Uri), Is.EqualTo("/2.0/repositories/acme/widget-api/pullrequests/412"));
        Assert.That(RequestUrl.QueryValue(request.Uri, "fields"), Is.EqualTo(FieldSets.PullRequestDetail));

        Assert.That(pullRequest.Id, Is.EqualTo(412));
        Assert.That(pullRequest.Description, Does.StartWith("Retries 408/429/502/503/504"));

        Assert.That(pullRequest.Reviewers, Is.Not.Null);
        Assert.That(pullRequest.Reviewers.Count, Is.EqualTo(2));
        Assert.That(pullRequest.Reviewers[0].Uuid, Is.EqualTo("{aaaabbbb-cccc-dddd-eeee-ffff00001111}"));

        Assert.That(pullRequest.Participants, Is.Not.Null);
        Assert.That(pullRequest.Participants.Count, Is.EqualTo(3));

        Assert.That(pullRequest.Participants[0].Approved, Is.True);
        Assert.That(pullRequest.Participants[0].State, Is.EqualTo("approved"));
        Assert.That(pullRequest.Participants[0].Role, Is.EqualTo("REVIEWER"));

        // The only place a change request is visible; note the lower-case wire value.
        Assert.That(pullRequest.Participants[1].State, Is.EqualTo("changes_requested"));
        Assert.That(pullRequest.Participants[1].Approved, Is.False);

        Assert.That(pullRequest.Participants[2].State, Is.Null);
        Assert.That(pullRequest.Participants[2].User?.DisplayName, Is.EqualTo("Jane Doe"));

        Assert.That(pullRequest.MergeCommit, Is.Null);
        Assert.That(pullRequest.ClosedBy, Is.Null);
    }

    [Test]
    public async Task GetPullRequestToleratesAPullRequestWithNoReviewers()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(HttpFixtures.Read("http-pullrequest-detail-unreviewed.json"));

        using var client = CreateClient(stub);

        var pullRequest = await client.GetPullRequestAsync(
            Workspace,
            Repository,
            500,
            TestContext.CurrentContext.CancellationToken);

        // Bitbucket omits the arrays entirely rather than sending empty ones.
        Assert.That(pullRequest.Reviewers, Is.Null);
        Assert.That(pullRequest.Participants, Is.Null);
        Assert.That(pullRequest.Description, Is.Null);
        Assert.That(pullRequest.Source?.Branch?.Name, Is.EqualTo("docs/diffstat-first"));
    }

    // --------------------------------------------------------------- diffstat

    [Test]
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
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var request = Expect.Single(stub.Requests);

        Assert.That(RequestUrl.Path(request.Uri), Is.EqualTo("/2.0/repositories/acme/widget-api/pullrequests/412/diffstat"));
        Assert.That(RequestUrl.QueryValue(request.Uri, "fields"), Is.EqualTo(FieldSets.DiffStat));
        Assert.That(RequestUrl.QueryValue(request.Uri, "pagelen"), Is.EqualTo("5"));

        Assert.That(page.TotalSize, Is.EqualTo(12));
        Assert.That(page.Items.Count, Is.EqualTo(5));
        Assert.That(page.NextCursor, Is.Not.Null);

        Assert.That(page.Items[0].Status, Is.EqualTo("modified"));
        Assert.That(page.Items[0].LinesAdded, Is.EqualTo(42));
        Assert.That(page.Items[0].LinesRemoved, Is.EqualTo(7));
        Assert.That(page.Items[0].New?.Path, Is.EqualTo("src/Bitbucket.Mcp/Http/RetryHandler.cs"));

        Assert.That(page.Items[1].Old, Is.Null);
        Assert.That(page.Items[1].New?.Path, Is.EqualTo("tests/Bitbucket.Mcp.Tests/Http/RetryHandlerTests.cs"));

        Assert.That(page.Items[2].New, Is.Null);
        Assert.That(page.Items[2].Old?.Path, Is.EqualTo("src/Bitbucket.Mcp/Http/LegacyBackoff.cs"));

        Assert.That(page.Items[3].Status, Is.EqualTo("renamed"));
        Assert.That(page.Items[3].Old?.Path, Is.EqualTo("docs/retry.md"));
        Assert.That(page.Items[3].New?.Path, Is.EqualTo("docs/http/retry.md"));

        // A binary file reports zero on both sides; the path is what the diff endpoint needs back.
        Assert.That(page.Items[4].LinesAdded, Is.EqualTo(0));
        Assert.That(page.Items[4].New?.Path, Is.EqualTo("assets/logo (draft).png"));
    }

    /// <summary>
    /// The live bug this pipeline was fixed for: Bitbucket answers <c>…/diffstat</c> with a
    /// <c>302</c> to another path on the same host, and the page only comes back if the redirected
    /// request still carries the credential.
    /// </summary>
    [Test]
    public async Task GetDiffStatFollowsTheRedirectBitbucketAnswersWith()
    {
        using var stub = new StubHttpMessageHandler();
        stub.Enqueue(_ => StubHttpMessageHandler.CreateRedirect(
            HttpStatusCode.Found,
            "/2.0/repositories/acme/widget-api/pullrequests/412/diffstat-content"));
        stub.EnqueueJson(HttpFixtures.Read("http-diffstat-page.json"));

        using var client = CreateClient(stub);

        var page = await client.GetDiffStatAsync(
            Workspace,
            Repository,
            412,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(page.Items.Count, Is.EqualTo(5));
        Assert.That(stub.Requests.Count, Is.EqualTo(2));

        Assert.That(RequestUrl.Path(stub.Requests[1].Uri), Is.EqualTo("/2.0/repositories/acme/widget-api/pullrequests/412/diffstat-content"));

        Assert.That(stub.Requests[1].Headers["Authorization"], Is.EqualTo("Bearer token-1"));
    }

    // ------------------------------------------------------------------- diff

    [Test]
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
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var request = Expect.Single(stub.Requests);

        Assert.That(diff, Is.EqualTo(Diff));
        Assert.That(RequestUrl.Path(request.Uri), Is.EqualTo("/2.0/repositories/acme/widget-api/pullrequests/412/diff"));

        // Repeated `path=`, not a comma-separated list — the whole diffstat-first workflow rests
        // on this being one parameter per file.
        Assert.That(RequestUrl.QueryValues(request.Uri, "path"), Is.EqualTo(DiffPaths));

        Assert.That(RequestUrl.QueryValue(request.Uri, "context"), Is.EqualTo("3"));
        Assert.That(RequestUrl.QueryValue(request.Uri, "ignore_whitespace"), Is.EqualTo("true"));

        // No `fields=`: this endpoint answers with text, not JSON.
        Assert.That(RequestUrl.QueryValue(request.Uri, "fields"), Is.Null);

        // A request-level Accept replaces the client-wide application/json default outright.
        Assert.That(request.Headers["Accept"], Is.EqualTo("text/plain"));
    }

    [Test]
    public async Task GetDiffSendsNoQueryAtAllForAWholePullRequest()
    {
        using var stub = new StubHttpMessageHandler();
        stub.Enqueue(HttpStatusCode.OK, "diff --git a/x b/x\n", "text/plain");

        using var client = CreateClient(stub);

        _ = await client.GetDiffAsync(
            Workspace,
            Repository,
            412,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var request = Expect.Single(stub.Requests);

        Assert.That(RequestUrl.PathAndQuery(request.Uri), Is.EqualTo("/2.0/repositories/acme/widget-api/pullrequests/412/diff"));
        Assert.That(RequestUrl.Query(request.Uri), Is.Empty);
    }

    [Test]
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
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var request = Expect.Single(stub.Requests);

        Assert.That(RequestUrl.QueryValue(request.Uri, "context"), Is.EqualTo("0"));
        Assert.That(RequestUrl.QueryValue(request.Uri, "ignore_whitespace"), Is.EqualTo("false"));
    }

    /// <summary>
    /// The same hop on the raw-diff endpoint, which additionally has to keep its <c>text/plain</c>
    /// Accept header across the redirect — the request the chain issues itself gets none of
    /// <see cref="HttpClient.DefaultRequestHeaders"/> for free.
    /// </summary>
    [Test]
    public async Task GetDiffFollowsTheRedirectAndKeepsItsCredentialAndAcceptHeader()
    {
        const string Diff = "diff --git a/src/A.cs b/src/A.cs\n--- a/src/A.cs\n+++ b/src/A.cs\n@@ -1 +1 @@\n-old\n+new\n";
        const string Target = "https://api.bitbucket.org/2.0/repositories/acme/widget-api/diff-content/abc123";

        using var stub = new StubHttpMessageHandler();
        stub.Enqueue(_ => StubHttpMessageHandler.CreateRedirect(HttpStatusCode.Found, Target));
        stub.Enqueue(HttpStatusCode.OK, Diff, "text/plain");

        using var client = CreateClient(stub);

        var diff = await client.GetDiffAsync(
            Workspace,
            Repository,
            412,
            paths: ["src/A.cs"],
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(diff, Is.EqualTo(Diff));
        Assert.That(stub.Requests.Count, Is.EqualTo(2));

        Assert.That(stub.Requests[1].Uri?.AbsoluteUri, Is.EqualTo(Target));
        Assert.That(stub.Requests[1].Headers["Authorization"], Is.EqualTo("Bearer token-1"));
        Assert.That(stub.Requests[1].Headers["Accept"], Is.EqualTo("text/plain"));
    }

    // --------------------------------------------------------------- comments

    [Test]
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
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var request = Expect.Single(stub.Requests);

        Assert.That(RequestUrl.Path(request.Uri), Is.EqualTo("/2.0/repositories/acme/widget-api/pullrequests/412/comments"));
        Assert.That(RequestUrl.QueryValue(request.Uri, "fields"), Is.EqualTo(FieldSets.Comments));
        Assert.That(RequestUrl.QueryValue(request.Uri, "pagelen"), Is.EqualTo("5"));

        Assert.That(page.Items.Count, Is.EqualTo(5));
        Assert.That(page.NextCursor, Is.Not.Null);

        var general = page.Items[0];
        Assert.That(general.Inline, Is.Null);
        Assert.That(general.Parent, Is.Null);
        Assert.That(general.Deleted, Is.False);
        Assert.That(general.User?.DisplayName, Is.EqualTo("Ada Byron"));

        var inline = page.Items[1];
        Assert.That(inline.Inline?.Path, Is.EqualTo("src/Bitbucket.Mcp/Http/RetryHandler.cs"));
        Assert.That(inline.Inline?.To, Is.EqualTo(75));
        Assert.That(inline.Inline?.From, Is.Null);

        var resolved = page.Items[2];
        Assert.That(resolved.Inline?.From, Is.EqualTo(118));
        Assert.That(resolved.Inline?.StartFrom, Is.EqualTo(112));
        Assert.That(resolved.Inline?.To, Is.Null);
        Assert.That(resolved.Resolution?.User?.DisplayName, Is.EqualTo("Jane Doe"));
        Assert.That(resolved.Resolution?.CreatedOn, Is.EqualTo(DateTimeOffset.Parse("2026-07-16T10:05:00+00:00", CultureInfo.InvariantCulture)));

        var reply = page.Items[3];
        Assert.That(reply.Parent?.Id, Is.EqualTo(900002));
        Assert.That(reply.Id, Is.EqualTo(900004));

        // Deleted comments still arrive, with their content blanked, so the thread keeps its
        // shape; filtering them out is the caller's job, not the client's.
        var deleted = page.Items[4];
        Assert.That(deleted.Deleted, Is.True);
        Assert.That(deleted.Content?.Raw, Is.EqualTo(string.Empty));
    }

    // ------------------------------------------------------------ path safety

    [TestCase("../../evil", "widget-api", "/2.0/repositories/..%2F..%2Fevil/widget-api/pullrequests")]
    [TestCase("acme", "my/repo", "/2.0/repositories/acme/my%2Frepo/pullrequests")]
    [TestCase("a b", "c?d#e", "/2.0/repositories/a%20b/c%3Fd%23e/pullrequests")]
    [TestCase("acme", "%2e%2e", "/2.0/repositories/acme/%252e%252e/pullrequests")]
    [TestCase("ünïcode", "repo", "/2.0/repositories/%C3%BCn%C3%AFcode/repo/pullrequests")]
    public async Task PathSegmentsAreEscapedExactlyOnce(string workspace, string slug, string expectedPath)
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(EmptyPage);

        using var client = CreateClient(stub);

        _ = await client.ListPullRequestsAsync(
            workspace,
            slug,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        // A slug arrives from the model and may be anything; escaping is what stops it walking out
        // of the repository's namespace or smuggling a query on to the end of the URL.
        Assert.That(RequestUrl.Path(Expect.Single(stub.Requests).Uri), Is.EqualTo(expectedPath));
    }

    /// <summary>
    /// <c>.</c> is unreserved, so <see cref="Uri.EscapeDataString(string)"/> leaves a dot segment untouched
    /// and RFC 3986 dot-segment removal collapses the path when the relative URL is resolved
    /// against the base address — <c>workspace=".."</c> with <c>slug=".."</c> would otherwise
    /// request <c>/pullrequests</c>, outside the <c>/2.0/</c> prefix entirely. Escaping cannot fix
    /// that, so the builder refuses the segment instead.
    /// </summary>
    [TestCase("..", "..")]
    [TestCase("..", "widget-api")]
    [TestCase("acme", ".")]
    [TestCase(".", "widget-api")]
    [TestCase("acme", "..")]
    [TestCase("  ..  ", "widget-api")]
    public async Task ADotSegmentIsRejectedBeforeAnyRequest(string workspace, string slug)
    {
        using var stub = new StubHttpMessageHandler();
        using var client = CreateClient(stub);

        var exception = await Expect.ThrowsAsync<ArgumentException>(() => client.ListPullRequestsAsync(
            workspace,
            slug,
            cancellationToken: TestContext.CurrentContext.CancellationToken));

        // The message is model-facing: it has to say what a slug is, not that a guard tripped.
        Assert.That(exception.Message, Does.Contain("must be real slugs"));
        Assert.That(stub.Requests, Is.Empty);
    }

    [TestCase(null, "repo")]
    [TestCase("", "repo")]
    [TestCase("   ", "repo")]
    [TestCase("acme", null)]
    [TestCase("acme", "")]
    [TestCase("acme", "  ")]
    public async Task ABlankWorkspaceOrSlugIsRejectedBeforeAnyRequest(string? workspace, string? slug)
    {
        using var stub = new StubHttpMessageHandler();
        using var client = CreateClient(stub);

        await Assert.CatchAsync<ArgumentException>(() => client.ListPullRequestsAsync(
            workspace!,
            slug!,
            cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(stub.Requests, Is.Empty);
    }

    /// <summary>
    /// The guard is on the segment, not on <c>listPullRequests</c>: every client method composes
    /// its URL through the same builder, so none of them can be talked out of the <c>/2.0/</c>
    /// prefix.
    /// </summary>
    [Test]
    public async Task EveryEndpointInheritsTheSlugGuard()
    {
        using var stub = new StubHttpMessageHandler();
        using var client = CreateClient(stub);
        var cancellationToken = TestContext.CurrentContext.CancellationToken;

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
            client.MergeAsync("..", Repository, 412, new MergeRequest(), cancellationToken: cancellationToken));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.DeclineAsync(Workspace, "..", 412, cancellationToken: cancellationToken));

        Assert.That(stub.Requests, Is.Empty);
    }

    // ----------------------------------------------------------- write bodies

    [Test]
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
            TestContext.CurrentContext.CancellationToken);

        var request = Expect.Single(stub.Requests);

        Assert.That(request.Method, Is.EqualTo(HttpMethod.Post));
        Assert.That(RequestUrl.Path(request.Uri), Is.EqualTo("/2.0/repositories/acme/widget-api/pullrequests"));
        Assert.That(RequestUrl.QueryValue(request.Uri, "fields"), Is.EqualTo(FieldSets.PullRequestDetail));
        Assert.That(request.Headers["Content-Type"], Is.EqualTo("application/json; charset=utf-8"));

        // A null property must be absent, not `null`: Bitbucket treats an explicit null as "clear
        // this field".
        Assert.That(request.Body, Is.EqualTo("""{"title":"Add retry handler for 429 responses","source":{"branch":{"name":"feature/retry-handler"}}}"""));
    }

    [Test]
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
            TestContext.CurrentContext.CancellationToken);

        Assert.That(Expect.Single(stub.Requests).Body, Is.EqualTo("""{"title":"Add retry handler","source":{"branch":{"name":"feature/retry-handler"}},"destination":{"branch":{"name":"main"}},"description":"Retries the transient statuses.","close_source_branch":true,"draft":false,"reviewers":[{"uuid":"{aaaabbbb-cccc-dddd-eeee-ffff00001111}"}]}"""));
    }

    [Test]
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
            TestContext.CurrentContext.CancellationToken);

        var request = Expect.Single(stub.Requests);

        Assert.That(request.Method, Is.EqualTo(HttpMethod.Put));
        Assert.That(RequestUrl.Path(request.Uri), Is.EqualTo("/2.0/repositories/acme/widget-api/pullrequests/412"));
        Assert.That(RequestUrl.QueryValue(request.Uri, "fields"), Is.EqualTo(FieldSets.PullRequestDetail));

        // Anything omitted keeps its current value; sending nulls would blank the description.
        Assert.That(request.Body, Is.EqualTo("""{"title":"Add retry handler (v2)"}"""));
    }

    [Test]
    public async Task AddCommentPostsAGeneralComment()
    {
        var request = await PostCommentAsync(new CommentRequest
        {
            Content = new CommentContentRequest { Raw = "Looks good to me." },
        });

        Assert.That(request.Method, Is.EqualTo(HttpMethod.Post));
        Assert.That(RequestUrl.Path(request.Uri), Is.EqualTo("/2.0/repositories/acme/widget-api/pullrequests/412/comments"));
        Assert.That(RequestUrl.QueryValue(request.Uri, "fields"), Is.EqualTo(FieldSets.Comment));
        Assert.That(request.Body, Is.EqualTo("""{"content":{"raw":"Looks good to me."}}"""));
    }

    [Test]
    public async Task AddCommentPostsAnInlineComment()
    {
        var request = await PostCommentAsync(new CommentRequest
        {
            Content = new CommentContentRequest { Raw = "This needs a test." },
            Inline = new InlineDto { Path = "src/Bitbucket.Mcp/Http/RetryHandler.cs", To = 218 },
        });

        Assert.That(request.Body, Is.EqualTo("""{"content":{"raw":"This needs a test."},"inline":{"path":"src/Bitbucket.Mcp/Http/RetryHandler.cs","to":218}}"""));
    }

    [Test]
    public async Task AddCommentPostsAMultiLineCommentOnTheOldSide()
    {
        var request = await PostCommentAsync(new CommentRequest
        {
            Content = new CommentContentRequest { Raw = "This block moved." },
            Inline = new InlineDto { Path = "src/A.cs", From = 118, StartFrom = 112 },
        });

        Assert.That(request.Body, Is.EqualTo("""{"content":{"raw":"This block moved."},"inline":{"path":"src/A.cs","from":118,"start_from":112}}"""));
    }

    [Test]
    public async Task AddCommentPostsAReply()
    {
        var request = await PostCommentAsync(new CommentRequest
        {
            Content = new CommentContentRequest { Raw = "Agreed." },
            Parent = new CommentParentRequest { Id = 900002 },
        });

        Assert.That(request.Body, Is.EqualTo("""{"content":{"raw":"Agreed."},"parent":{"id":900002}}"""));
    }

    // --------------------------------------------------------- review actions

    [Test]
    public async Task ApprovePostsToApproveAndReturnsTheCallersParticipantEntry()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(HttpFixtures.Read("http-participant-approved.json"));

        using var client = CreateClient(stub);

        var participant = await client.ApproveAsync(
            Workspace,
            Repository,
            412,
            TestContext.CurrentContext.CancellationToken);

        var request = Expect.Single(stub.Requests);

        Assert.That(request.Method, Is.EqualTo(HttpMethod.Post));
        Assert.That(RequestUrl.Path(request.Uri), Is.EqualTo("/2.0/repositories/acme/widget-api/pullrequests/412/approve"));
        Assert.That(RequestUrl.QueryValue(request.Uri, "fields"), Is.EqualTo(FieldSets.Participant));

        Assert.That(participant.Approved, Is.True);
        Assert.That(participant.State, Is.EqualTo("approved"));
        Assert.That(participant.User?.DisplayName, Is.EqualTo("Ada Byron"));
    }

    [Test]
    public async Task RequestChangesPostsToRequestChanges()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson("""{"role":"REVIEWER","approved":false,"state":"changes_requested"}""");

        using var client = CreateClient(stub);

        var participant = await client.RequestChangesAsync(
            Workspace,
            Repository,
            412,
            TestContext.CurrentContext.CancellationToken);

        var request = Expect.Single(stub.Requests);

        Assert.That(request.Method, Is.EqualTo(HttpMethod.Post));
        Assert.That(RequestUrl.Path(request.Uri), Is.EqualTo("/2.0/repositories/acme/widget-api/pullrequests/412/request-changes"));
        Assert.That(RequestUrl.QueryValue(request.Uri, "fields"), Is.EqualTo(FieldSets.Participant));
        Assert.That(participant.State, Is.EqualTo("changes_requested"));
    }

    [TestCase("approve")]
    [TestCase("request-changes")]
    public async Task WithdrawingAReviewIsADeleteWithNoFieldsAndNoBody(string segment)
    {
        using var stub = new StubHttpMessageHandler();
        stub.Enqueue(HttpStatusCode.NoContent);

        using var client = CreateClient(stub);

        if (segment == "approve")
        {
            await client.UnapproveAsync(Workspace, Repository, 412, TestContext.CurrentContext.CancellationToken);
        }
        else
        {
            await client.UnrequestChangesAsync(Workspace, Repository, 412, TestContext.CurrentContext.CancellationToken);
        }

        var request = Expect.Single(stub.Requests);

        Assert.That(request.Method, Is.EqualTo(HttpMethod.Delete));

        // No `fields=`: a 204 has nothing to shape.
        Assert.That(RequestUrl.PathAndQuery(request.Uri), Is.EqualTo($"/2.0/repositories/acme/widget-api/pullrequests/412/{segment}"));
        Assert.That(request.Body, Is.Null);
    }

    // ------------------------------------------------------------------ merge

    [Test]
    public async Task MergeAsksForThePullRequestDetail()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(HttpFixtures.Read("http-pullrequest-merged.json"));

        using var client = CreateClient(stub);

        var outcome = await client.MergeAsync(
            Workspace,
            Repository,
            412,
            new MergeRequest { Message = "Merged via MCP", CloseSourceBranch = true, MergeStrategy = "squash" },
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var request = Expect.Single(stub.Requests);

        Assert.That(request.Method, Is.EqualTo(HttpMethod.Post));
        Assert.That(RequestUrl.Path(request.Uri), Is.EqualTo("/2.0/repositories/acme/widget-api/pullrequests/412/merge"));
        Assert.That(RequestUrl.QueryValue(request.Uri, "fields"), Is.EqualTo(FieldSets.PullRequestDetail));
        Assert.That(request.Body, Is.EqualTo("""{"message":"Merged via MCP","close_source_branch":true,"merge_strategy":"squash"}"""));

        var merged = Expect.NotNull(outcome.PullRequest);
        Assert.That(merged.State, Is.EqualTo("MERGED"));
        Assert.That(merged.MergeCommit?.Hash, Is.EqualTo("fedcba987654"));
        Assert.That(merged.ClosedBy?.DisplayName, Is.EqualTo("Ada Byron"));
        Assert.That(outcome.TaskId, Is.Null);
    }

    /// <summary>
    /// A merge into a queued branch, or one past Bitbucket's synchronous timeout, answers 202 with
    /// the task in Location. The task is followed, and a merge that lands in time comes back as a
    /// 200 would have.
    /// </summary>
    [Test]
    public async Task AMergeTaskIsFollowedUntilItLands()
    {
        var time = new ManualTimeProvider();
        var merged = HttpFixtures.Read("http-pullrequest-merged.json");

        using var stub = new StubHttpMessageHandler();
        stub.Enqueue(_ => MergeAccepted(MergeTaskUrl));
        stub.EnqueueJson(QueuedTaskStatus);
        stub.EnqueueJson($$"""{"task_status":"SUCCESS","merge_result":{{merged}}}""");

        using var client = CreateClient(stub, timeProvider: time);

        var waits = new List<MergeWait>();
        var outcome = await client.MergeAsync(
            Workspace,
            Repository,
            412,
            new MergeRequest(),
            waits.Add,
            TestContext.CurrentContext.CancellationToken);

        Assert.That(Expect.NotNull(outcome.PullRequest).State, Is.EqualTo("MERGED"));
        Assert.That(outcome.TaskId, Is.Null);

        Assert.That(stub.Requests.Count, Is.EqualTo(3));
        Assert.That(stub.Requests[1].Method, Is.EqualTo(HttpMethod.Get));
        Assert.That(RequestUrl.Path(stub.Requests[1].Uri), Is.EqualTo(MergeTaskPath));
        Assert.That(RequestUrl.Path(stub.Requests[2].Uri), Is.EqualTo(MergeTaskPath));

        // One look per scheduled delay, and the first unanswered one is reported as queued.
        Assert.That(time.Delays, Is.EqualTo(BitbucketApiClient.MergeTaskPollDelays[..2]));
        Assert.That(waits, Is.EqualTo(new[] { new MergeWait(1, BitbucketApiClient.MergeTaskPollDelays.Length, InMergeQueue: true) }));
    }

    /// <summary>
    /// A merge queue can hold a pull request for as long as its builds take. After the schedule the
    /// task itself comes back — never an error, because the merge is under way and merging again is
    /// the mistake to prevent.
    /// </summary>
    [Test]
    public async Task AMergeStillPendingAfterTheScheduleComesBackAsItsTask()
    {
        var time = new ManualTimeProvider();

        using var stub = new StubHttpMessageHandler();
        stub.Enqueue(_ => MergeAccepted(MergeTaskUrl));

        foreach (var _ in BitbucketApiClient.MergeTaskPollDelays)
        {
            stub.EnqueueJson(QueuedTaskStatus);
        }

        using var client = CreateClient(stub, timeProvider: time);

        var outcome = await client.MergeAsync(
            Workspace,
            Repository,
            412,
            new MergeRequest(),
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(outcome.PullRequest, Is.Null);
        Assert.That(outcome.TaskId, Is.EqualTo(MergeTaskId));
        Assert.That(outcome.InMergeQueue, Is.True);
        Assert.That(stub.Requests.Count, Is.EqualTo(1 + BitbucketApiClient.MergeTaskPollDelays.Length));
        Assert.That(time.Delays, Is.EqualTo(BitbucketApiClient.MergeTaskPollDelays));
        Assert.That(time.Delays.Aggregate(TimeSpan.Zero, (sum, delay) => sum + delay), Is.EqualTo(TimeSpan.FromSeconds(25)));
    }

    /// <summary>A task that fails answers the poll with an error, which is the merge's failure.</summary>
    [Test]
    public async Task AFailedMergeTaskSurfacesAsTheMergeFailure()
    {
        using var stub = new StubHttpMessageHandler();
        stub.Enqueue(_ => MergeAccepted(MergeTaskUrl));
        stub.Enqueue(
            HttpStatusCode.BadRequest,
            """{"type":"error","error":{"message":"The pull request left the merge queue without merging."}}""");

        using var client = CreateClient(stub);

        var exception = await Expect.ThrowsAsync<BitbucketApiException>(() => client.MergeAsync(
            Workspace,
            Repository,
            412,
            new MergeRequest(),
            cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(exception.Message, Does.Contain("left the merge queue"));
    }

    /// <summary>A success that does not carry the pull request reads it, so the shape matches a 200.</summary>
    [Test]
    public async Task ASuccessWithoutTheMergedPullRequestReadsItBack()
    {
        using var stub = new StubHttpMessageHandler();
        stub.Enqueue(_ => MergeAccepted(MergeTaskUrl));
        stub.EnqueueJson("""{"task_status":"SUCCESS"}""");
        stub.EnqueueJson(HttpFixtures.Read("http-pullrequest-merged.json"));

        using var client = CreateClient(stub);

        var outcome = await client.MergeAsync(
            Workspace,
            Repository,
            412,
            new MergeRequest(),
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(Expect.NotNull(outcome.PullRequest).MergeCommit?.Hash, Is.EqualTo("fedcba987654"));
        Assert.That(RequestUrl.Path(stub.Requests[2].Uri), Is.EqualTo("/2.0/repositories/acme/widget-api/pullrequests/412"));
    }

    /// <summary>
    /// Location is server-supplied. Only this pull request's own task-status endpoint on the API host
    /// is followed, and only by id: anywhere else is not requested at all.
    /// </summary>
    [TestCase("https://evil.example/2.0/repositories/acme/widget-api/pullrequests/412/merge/task-status/abc")]
    [TestCase("http://api.bitbucket.org/2.0/repositories/acme/widget-api/pullrequests/412/merge/task-status/abc")]
    [TestCase("https://api.bitbucket.org/2.0/repositories/acme/widget-api/pullrequests/413/merge/task-status/abc")]
    [TestCase("https://api.bitbucket.org/2.0/repositories/acme/widget-api/pullrequests/412/merge/task-status/abc/../../../../decline")]
    [TestCase("https://api.bitbucket.org/2.0/repositories/acme/widget-api/pullrequests/412/merge/task-status/")]
    public async Task AMergeTaskLinkThatIsNotThisPullRequestsIsNeverFollowed(string location)
    {
        using var stub = new StubHttpMessageHandler();
        stub.Enqueue(_ => MergeAccepted(location));

        using var client = CreateClient(stub);

        var outcome = await client.MergeAsync(
            Workspace,
            Repository,
            412,
            new MergeRequest(),
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(stub.Requests, Has.Exactly(1).Items);
        Assert.That(outcome.PullRequest, Is.Null);
        Assert.That(outcome.TaskId, Is.Null);
    }

    /// <summary>A relative Location resolves against the merge request, like any other.</summary>
    [Test]
    public async Task ARelativeMergeTaskLinkIsFollowed()
    {
        using var stub = new StubHttpMessageHandler();
        stub.Enqueue(_ => MergeAccepted(MergeTaskPath));
        stub.EnqueueJson($$"""{"task_status":"SUCCESS","merge_result":{{HttpFixtures.Read("http-pullrequest-merged.json")}}}""");

        using var client = CreateClient(stub);

        var outcome = await client.MergeAsync(
            Workspace,
            Repository,
            412,
            new MergeRequest(),
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(outcome.PullRequest, Is.Not.Null);
        Assert.That(RequestUrl.Path(stub.Requests[1].Uri), Is.EqualTo(MergeTaskPath));
    }

    [Test]
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
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(Expect.Single(stub.Requests).Body, Is.EqualTo("{}"));
    }

    // ------------------------------------------- mergeability, comment edits, list filters

    [Test]
    public async Task MergeabilityChecksAreReadFromTheirOwnEndpoint()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(HttpFixtures.Read("http-mergeability-checks.json"));

        using var client = CreateClient(stub);

        var checks = await client.GetMergeabilityChecksAsync(Workspace, Repository, 412, TestContext.CurrentContext.CancellationToken);

        var request = Expect.Single(stub.Requests);
        Assert.That(request.Method, Is.EqualTo(HttpMethod.Get));
        Assert.That(RequestUrl.PathAndQuery(request.Uri), Is.EqualTo("/2.0/repositories/acme/widget-api/pullrequests/412/mergeability/checks"));

        var values = Expect.NotNull(checks.Values);
        Assert.That(values.Count, Is.EqualTo(6));

        var approvals = values[3];
        Assert.That(approvals.Check?.Kind, Is.EqualTo("minimum_approvals"));
        Assert.That(approvals.Blocking, Is.True);
        Assert.That(Expect.NotNull(approvals.Requirement).GetProperty("minimum_approvals").GetInt32(), Is.EqualTo(2));
        Assert.That(Expect.NotNull(approvals.Observed).GetProperty("approval_count").GetInt32(), Is.EqualTo(1));

        Assert.That(values[0].State, Is.EqualTo("OPEN"));
        Assert.That(values[2].Reason, Is.EqualTo("clean"));
        Assert.That(values[5].Check?.Name, Is.EqualTo("License scan"));
        Assert.That(values[5].Message, Is.EqualTo("No new licenses."));
    }

    [Test]
    public async Task EditingACommentPutsOnlyItsNewText()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(HttpFixtures.Read("http-comment-created.json"));

        using var client = CreateClient(stub);

        _ = await client.UpdateCommentAsync(Workspace, Repository, 412, 1001, "Fixed wording.", TestContext.CurrentContext.CancellationToken);

        var request = Expect.Single(stub.Requests);
        Assert.That(request.Method, Is.EqualTo(HttpMethod.Put));
        Assert.That(RequestUrl.Path(request.Uri), Is.EqualTo("/2.0/repositories/acme/widget-api/pullrequests/412/comments/1001"));
        Assert.That(RequestUrl.QueryValue(request.Uri, "fields"), Is.EqualTo(FieldSets.Comment));

        // No inline and no parent: an edit changes the text and nothing else.
        Assert.That(request.Body, Is.EqualTo("""{"content":{"raw":"Fixed wording."}}"""));
    }

    [Test]
    public async Task DeletingACommentSendsADeleteWithNoBody()
    {
        using var stub = new StubHttpMessageHandler();
        stub.Enqueue(HttpStatusCode.NoContent);

        using var client = CreateClient(stub);

        await client.DeleteCommentAsync(Workspace, Repository, 412, 1001, TestContext.CurrentContext.CancellationToken);

        var request = Expect.Single(stub.Requests);
        Assert.That(request.Method, Is.EqualTo(HttpMethod.Delete));
        Assert.That(RequestUrl.PathAndQuery(request.Uri), Is.EqualTo("/2.0/repositories/acme/widget-api/pullrequests/412/comments/1001"));
        Assert.That(request.Body, Is.Null.Or.Empty);
    }

    /// <summary>
    /// Deleted comments stay in Bitbucket's list, blanked. Filtering them in the query keeps a page
    /// from coming back short — or empty, with a next page still set — after they are dropped.
    /// </summary>
    [Test]
    public async Task CommentsAreListedWithoutTheDeletedOnes()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(HttpFixtures.Read("http-comments-page.json"));

        using var client = CreateClient(stub);

        _ = await client.GetCommentsAsync(Workspace, Repository, 412, cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(RequestUrl.QueryValue(Expect.Single(stub.Requests).Uri, "q"), Is.EqualTo("deleted = false"));
    }

    [TestCase("{11111111-2222-3333-4444-555555555555}", "reviewers.uuid = \"{11111111-2222-3333-4444-555555555555}\"")]
    [TestCase("ada", "reviewers.nickname = \"ada\"")]
    public async Task ReviewerAndDestinationBranchBecomeBbqlClauses(string reviewer, string reviewerClause)
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(EmptyPage);

        using var client = CreateClient(stub);

        _ = await client.ListPullRequestsAsync(
            Workspace,
            Repository,
            states: ["OPEN"],
            reviewer: reviewer,
            destinationBranch: "main",
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(
            RequestUrl.QueryValue(Expect.Single(stub.Requests).Uri, "q"),
            Is.EqualTo($"state = \"OPEN\" AND {reviewerClause} AND destination.branch.name = \"main\""));
    }

    // ---------------------------------------------------------------- decline

    [Test]
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
            TestContext.CurrentContext.CancellationToken);

        var request = Expect.Single(stub.Requests);

        Assert.That(request.Method, Is.EqualTo(HttpMethod.Post));
        Assert.That(RequestUrl.Path(request.Uri), Is.EqualTo("/2.0/repositories/acme/widget-api/pullrequests/412/decline"));
        Assert.That(RequestUrl.QueryValue(request.Uri, "fields"), Is.EqualTo(FieldSets.PullRequestDetail));
        Assert.That(request.Body, Is.EqualTo("""{"reason":"Superseded by #500"}"""));
    }

    [Test]
    public async Task DeclineWithoutAReasonPostsAnEmptyObject()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(HttpFixtures.Read("http-pullrequest-detail.json"));

        using var client = CreateClient(stub);

        _ = await client.DeclineAsync(
            Workspace,
            Repository,
            412,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(Expect.Single(stub.Requests).Body, Is.EqualTo("{}"));
    }

    // ----------------------------------------------------------------- errors

    [Test]
    public async Task AValidationFailureCarriesTheParsedEnvelopeAndTheRawBody()
    {
        var body = HttpFixtures.Read("http-error-validation.json");

        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(body, HttpStatusCode.BadRequest);

        using var client = CreateClient(stub);

        var exception = await Expect.ThrowsAsync<BitbucketApiException>(() => client.CreatePullRequestAsync(
            Workspace,
            Repository,
            new CreatePullRequestRequest
            {
                Title = "x",
                Source = new PullRequestEndpointRequest { Branch = new BranchRequest { Name = "feature/does-not-exist" } },
            },
            TestContext.CurrentContext.CancellationToken));

        Assert.That(exception.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(exception.RawBody, Is.EqualTo(body));
        Assert.That(exception.RetryAttempts, Is.EqualTo(0));

        Assert.That(exception.Error?.Error?.Message, Is.EqualTo("title: This field is required."));
        Assert.That(exception.Error?.Error?.Detail, Does.StartWith("The pull request could not be created"));

        // The per-field errors are the useful part of a rejected write.
        var fields = exception.Error?.Error?.Fields;
        Assert.That(fields, Is.Not.Null);
        Assert.That(fields["title"], Is.EqualTo(TitleFieldErrors));
        Assert.That(fields["source"].Count, Is.EqualTo(2));

        Assert.That(exception.Message, Does.Contain("HTTP 400 (BadRequest)"));
        Assert.That(exception.Message, Does.Contain("title: This field is required."));
    }

    [Test]
    public async Task BitbucketsNonStandard555KeepsItsNumericStatus()
    {
        var body = HttpFixtures.Read("http-error-555-diff-too-large.json");

        using var stub = new StubHttpMessageHandler();
        stub.Enqueue((HttpStatusCode) 555, body);

        using var client = CreateClient(stub);

        var exception = await Expect.ThrowsAsync<BitbucketApiException>(() => client.GetDiffAsync(
            Workspace,
            Repository,
            412,
            cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That((int) exception.StatusCode, Is.EqualTo(555));
        Assert.That(exception.RawBody, Is.EqualTo(body));
        Assert.That(exception.Error?.Error?.Message, Is.EqualTo("Diff too large to render."));

        // 555 has no name in HttpStatusCode, so the message must not invent one.
        Assert.That(exception.Message, Does.Contain("HTTP 555."));
        Assert.That(exception.Message, Does.Not.Contain("(555)"));
        Assert.That(exception.Message, Does.Contain("Diff too large to render."));
    }

    [Test]
    public async Task ANonJsonErrorBodyIsToleratedAndKeptVerbatim()
    {
        const string Html = "<html>\n  <head><title>503 Service Unavailable</title></head>\n  <body>Bitbucket is down.</body>\n</html>";

        using var stub = new StubHttpMessageHandler();

        // Every attempt fails, so the retry budget is spent before the exception is built.
        stub.Fallback = _ => StubHttpMessageHandler.CreateResponse(HttpStatusCode.ServiceUnavailable, Html, "text/html");

        using var client = CreateClient(stub);

        var exception = await Expect.ThrowsAsync<BitbucketApiException>(() => client.GetPullRequestAsync(
            Workspace,
            Repository,
            412,
            TestContext.CurrentContext.CancellationToken));

        Assert.That(exception.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        Assert.That(exception.Error, Is.Null);
        Assert.That(exception.RawBody, Is.EqualTo(Html));

        // Retries are reported so a 5xx message can say the request was not simply unlucky once.
        Assert.That(exception.RetryAttempts, Is.EqualTo(RetryHandler.MaxAttempts - 1));
        Assert.That(exception.Message, Does.Contain("after 3 retries"));

        // The message quotes a collapsed single-line snippet, never the raw multi-line page.
        Assert.That(exception.Message.Contains('\n', StringComparison.Ordinal), Is.False);
    }

    /// <summary>
    /// Both documented forms of <c>Retry-After</c> reach the exception as seconds, so the 429 tool
    /// error can name the wait instead of guessing at it. Ninety seconds is past
    /// <see cref="RetryHandler.MaxRetryAfter"/>, so the response comes back unretried — which is
    /// exactly the case where the caller has to be told how long is left.
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
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

        var exception = await Expect.ThrowsAsync<BitbucketApiException>(() => client.GetPullRequestAsync(
            Workspace,
            Repository,
            412,
            TestContext.CurrentContext.CancellationToken));

        Assert.That(stub.Requests, Has.Exactly(1).Items);
        Assert.That(exception.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
        Assert.That(exception.RetryAfterSeconds, Is.EqualTo(90));
        Assert.That(exception.RetryAttempts, Is.EqualTo(0));
    }

    [Test]
    public async Task AFailureWithoutARetryAfterHeaderReportsNoWait()
    {
        using var stub = new StubHttpMessageHandler();
        stub.Fallback = _ => StubHttpMessageHandler.CreateResponse(
            HttpStatusCode.TooManyRequests,
            """{"type":"error","error":{"message":"Rate limit exceeded"}}""");

        using var client = CreateClient(stub);

        var exception = await Expect.ThrowsAsync<BitbucketApiException>(() => client.GetPullRequestAsync(
            Workspace,
            Repository,
            412,
            TestContext.CurrentContext.CancellationToken));

        // Null rather than a default: "Bitbucket did not say" and "Bitbucket said zero" are
        // different pieces of advice.
        Assert.That(exception.RetryAfterSeconds, Is.Null);
        Assert.That(exception.RetryAttempts, Is.EqualTo(RetryHandler.MaxAttempts - 1));
    }

    [Test]
    public async Task AnEmptyBodyOnASuccessIsAFailure()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson("");

        using var client = CreateClient(stub);

        var exception = await Expect.ThrowsAsync<BitbucketApiException>(() => client.GetPullRequestAsync(
            Workspace,
            Repository,
            412,
            TestContext.CurrentContext.CancellationToken));

        Assert.That(exception.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(exception.Message, Does.Contain("could not parse"));
    }

    // ---------------------------------------------------------------- headers

    [Test]
    public async Task EveryRequestIdentifiesTheServerAndItsRepository()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(EmptyPage);

        using var client = CreateClient(stub);

        _ = await client.ListPullRequestsAsync(
            Workspace,
            Repository,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var request = Expect.Single(stub.Requests);
        var userAgent = request.Headers["User-Agent"];

        Assert.That(userAgent, Does.StartWith($"{ServerVersion.Name}/{ServerVersion.Value}"));
        Assert.That(userAgent, Does.Contain(ProjectUrl));
        Assert.That(request.Headers["Accept"], Is.EqualTo("application/json"));
    }

    [Test]
    public async Task TheAuthorizationHeaderComesFromTheCredentialProviderPerRequest()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson(EmptyPage);
        stub.EnqueueJson(EmptyPage);

        var credentials = new StubCredentialProvider("token-1");
        using var client = CreateClient(stub, credentials);

        _ = await client.ListPullRequestsAsync(Workspace, Repository, cancellationToken: TestContext.CurrentContext.CancellationToken);
        _ = await client.ListPullRequestsAsync(Workspace, Repository, cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(stub.Requests.Select(request => request.Headers["Authorization"]), Is.All.EqualTo("Bearer token-1"));

        // Per request, never a default header: the pipeline decides per hop who is allowed the
        // credential, and a default header would be attached to requests it never inspected.
        Assert.That(credentials.HeaderRequestCount, Is.EqualTo(2));
        Assert.That(credentials.InvalidateCount, Is.EqualTo(0));
    }

    [Test]
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
            TestContext.CurrentContext.CancellationToken);

        Assert.That(pullRequest.Id, Is.EqualTo(412));

        Assert.That(stub.Requests.Count, Is.EqualTo(2));
        Assert.That(stub.Requests[0].Headers["Authorization"], Is.EqualTo("Bearer stale-token"));
        Assert.That(stub.Requests[1].Headers["Authorization"], Is.EqualTo("Bearer fresh-token"));

        Assert.That(credentials.InvalidateCount, Is.EqualTo(1));
        Assert.That(credentials.HeaderRequestCount, Is.EqualTo(2));
    }

    [Test]
    public async Task ASecond401IsReportedRatherThanRetriedAgain()
    {
        using var stub = new StubHttpMessageHandler();
        stub.Fallback = _ => StubHttpMessageHandler.CreateResponse(
            HttpStatusCode.Unauthorized,
            """{"type":"error","error":{"message":"Invalid token"}}""");

        var credentials = new StubCredentialProvider("stale-token", "also-wrong");
        using var client = CreateClient(stub, credentials);

        var exception = await Expect.ThrowsAsync<BitbucketApiException>(() => client.GetPullRequestAsync(
            Workspace,
            Repository,
            412,
            TestContext.CurrentContext.CancellationToken));

        Assert.That(exception.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(exception.Error?.Error?.Message, Is.EqualTo("Invalid token"));

        // Exactly one retry: a freshly acquired credential that is also rejected is wrong rather
        // than stale, and looping would only turn a clear 401 into a slow one. A 401 is also never
        // retried by the backoff handler.
        Assert.That(stub.Requests.Count, Is.EqualTo(2));
        Assert.That(credentials.InvalidateCount, Is.EqualTo(1));
        Assert.That(exception.RetryAttempts, Is.EqualTo(0));
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
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        return Expect.Single(stub.Requests);
    }

    private const string MergeTaskId = "e7c0ffee-1234-4a5b-8c9d-0123456789ab";
    private const string MergeTaskPath = "/2.0/repositories/acme/widget-api/pullrequests/412/merge/task-status/" + MergeTaskId;
    private const string MergeTaskUrl = "https://api.bitbucket.org" + MergeTaskPath;

    /// <summary>
    /// A task that is waiting in a merge queue. SYNTHETIC: shaped after the task-status endpoint's
    /// description in Bitbucket's OpenAPI document, not captured from a live account.
    /// </summary>
    private const string QueuedTaskStatus = """{"task_status":"PENDING","queued_pullrequest":{"type":"queued_pullrequest"}}""";

    /// <summary>Bitbucket's 202 for a merge it turned into a task, pointing at the task in Location.</summary>
    private static HttpResponseMessage MergeAccepted(string location)
    {
        var response = StubHttpMessageHandler.CreateResponse(HttpStatusCode.Accepted, HttpFixtures.Read("http-merge-queued.json"));
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
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