using System.Net;
using System.Text;

using Bitbucket.Mcp.Tools;

using ModelContextProtocol;

using Xunit;

namespace Bitbucket.Mcp.Tests.Tools;

/// <summary>
/// The pipeline and build-diagnosis tools, over the stub transport.
/// </summary>
/// <remarks>
/// A sibling of <c>ToolBehaviourTests</c> rather than more of it: that file is already very large,
/// and the pipeline surface has its own set of traps worth keeping together — Bitbucket's two
/// disagreeing status vocabularies, a <c>next</c> link that drops <c>fields=</c>, and the first
/// endpoint in the server that redirects off the API host.
/// </remarks>
public sealed class PipelineToolTests
{
    private const string Workspace = "acme";
    private const string Repository = "widgets";

    [Fact]
    public async Task ListingPipelinesFlattensEveryStateShape()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(PipelineFixtures.PipelinePage);

        using var client = ToolTestHost.CreateClient(handler);

        var result = await PipelineReadTools.ListPipelinesAsync(
            client,
            ToolTestHost.CreateOptions(),
            Repository,
            Workspace,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, result.Pipelines.Count);

        // {name: COMPLETED, result: {name: FAILED}} -> FAILED
        Assert.Equal("FAILED", result.Pipelines[0].State);
        Assert.True(result.Pipelines[0].Completed);

        // {name: IN_PROGRESS, stage: {name: RUNNING}} -> RUNNING, and not finished
        Assert.Equal("RUNNING", result.Pipelines[1].State);
        Assert.False(result.Pipelines[1].Completed);

        // {name: PENDING} -> PENDING
        Assert.Equal("PENDING", result.Pipelines[2].State);
        Assert.False(result.Pipelines[2].Completed);
    }

    [Fact]
    public async Task ListingPipelinesComposesTheQueryAndBuildsTheWebUrl()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(PipelineFixtures.PipelinePage);

        using var client = ToolTestHost.CreateClient(handler);

        var result = await PipelineReadTools.ListPipelinesAsync(
            client,
            ToolTestHost.CreateOptions(),
            Repository,
            Workspace,
            targetBranch: "refs/heads/feature/clamp",
            cancellationToken: TestContext.Current.CancellationToken);

        var uri = Assert.Single(handler.Requests).Uri!;

        Assert.Contains("/repositories/acme/widgets/pipelines", uri.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains("target.branch=feature%2Fclamp", uri.Query, StringComparison.Ordinal);
        Assert.Contains("sort=-created_on", uri.Query, StringComparison.Ordinal);
        Assert.Contains("fields=", uri.Query, StringComparison.Ordinal);

        // A pipeline carries no html link, so the page URL is composed from the build number.
        Assert.Equal(
            "https://bitbucket.org/acme/widgets/pipelines/results/8173",
            result.Pipelines[0].Url);
    }

    /// <summary>
    /// Bitbucket's <c>status</c> filter does not speak the vocabulary its responses use:
    /// <c>SUCCESSFUL</c> in a result is <c>PASSED</c> in the filter, and asking for
    /// <c>status=SUCCESSFUL</c> matches nothing at all.
    /// </summary>
    [Theory]
    [InlineData("SUCCESSFUL", "PASSED")]
    [InlineData("successful", "PASSED")]
    [InlineData("PASSED", "PASSED")]
    [InlineData("FAILED", "FAILED")]
    [InlineData("RUNNING", "BUILDING")]
    public async Task StatusIsTranslatedIntoBitbucketsFilterVocabulary(string given, string sent)
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(PipelineFixtures.PipelinePage);

        using var client = ToolTestHost.CreateClient(handler);

        _ = await PipelineReadTools.ListPipelinesAsync(
            client,
            ToolTestHost.CreateOptions(),
            Repository,
            Workspace,
            status: given,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains($"status={sent}", Assert.Single(handler.Requests).Uri!.Query, StringComparison.Ordinal);
    }

    /// <summary>
    /// Bitbucket answers an unrecognised status with 200 and an empty page, which reads exactly like
    /// "this repository has never run a pipeline". Refusing it here is the only way the caller finds
    /// out they asked the wrong question.
    /// </summary>
    [Fact]
    public async Task AnUnknownStatusIsRefusedRatherThanSentAndSilentlyIgnored()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() => PipelineReadTools.ListPipelinesAsync(
            client,
            ToolTestHost.CreateOptions(),
            Repository,
            Workspace,
            status: "COMPLETED",
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("SUCCESSFUL", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    /// <summary>
    /// The pipelines endpoint is the one that does <b>not</b> echo <c>fields=</c> into its
    /// <c>next</c> link. Following the cursor verbatim would return full objects — measured at
    /// 2,615 bytes each against 50 trimmed — so the field set is re-applied.
    /// </summary>
    [Fact]
    public async Task APipelineCursorHasTheFieldSetReappliedToIt()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(PipelineFixtures.PipelinePage);

        using var client = ToolTestHost.CreateClient(handler);

        var first = await PipelineReadTools.ListPipelinesAsync(
            client,
            ToolTestHost.CreateOptions(),
            Repository,
            Workspace,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(first.NextCursor);

        handler.EnqueueJson(PipelineFixtures.PipelinePage);

        _ = await PipelineReadTools.ListPipelinesAsync(
            client,
            ToolTestHost.CreateOptions(),
            Repository,
            Workspace,
            cursor: first.NextCursor,
            cancellationToken: TestContext.Current.CancellationToken);

        var second = handler.Requests[1].Uri!;

        Assert.Contains("page=2", second.Query, StringComparison.Ordinal);
        Assert.Contains("fields=", second.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GettingAPipelineMergesItsStepsAndNamesTheFailingOne()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(PipelineFixtures.Pipeline);
        handler.EnqueueJson(PipelineFixtures.StepPage);

        using var client = ToolTestHost.CreateClient(handler);

        var result = await PipelineReadTools.GetPipelineAsync(
            client,
            ToolTestHost.CreateOptions(),
            Repository,
            "8173",
            Workspace,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(2, result.Steps.Count);

        Assert.Equal("Test and Build", result.Steps[0].Name);
        Assert.Equal("FAILED", result.Steps[0].State);
        Assert.Equal("The step timed out.", result.Steps[0].ErrorMessage);
        Assert.Equal("NOT_RUN", result.Steps[1].State);

        Assert.Equal("{951411fa-aa47-472c-80ce-4f4f4899d1b3}", result.FailedStepUuid);

        // The hint carries the error and the exact next call, so the log is a choice rather than a
        // reflex.
        Assert.Contains("The step timed out.", result.Hint!, StringComparison.Ordinal);
        Assert.Contains("getPipelineStepLog", result.Hint!, StringComparison.Ordinal);
        Assert.Contains("{951411fa-aa47-472c-80ce-4f4f4899d1b3}", result.Hint!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A run can carry more steps than one page holds, and a truncated step list must not read like
    /// a complete one — the same rule the diff tools follow.
    /// </summary>
    [Fact]
    public async Task AStepListCutShortByPagingSaysSo()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(PipelineFixtures.Pipeline);
        handler.EnqueueJson(PipelineFixtures.StepPageWithMore);

        using var client = ToolTestHost.CreateClient(handler);

        var result = await PipelineReadTools.GetPipelineAsync(
            client, ToolTestHost.CreateOptions(), Repository, "8173", Workspace, TestContext.Current.CancellationToken);

        Assert.Contains("only the first", result.Hint!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("8173", "/pipelines/8173")]
    [InlineData("#8173", "/pipelines/8173")]
    [InlineData("{c1db2bb8-6168-4675-b85a-e95f450523f9}", "/pipelines/%7Bc1db2bb8-6168-4675-b85a-e95f450523f9%7D")]
    [InlineData("c1db2bb8-6168-4675-b85a-e95f450523f9", "/pipelines/%7Bc1db2bb8-6168-4675-b85a-e95f450523f9%7D")]
    public async Task APipelineIsAddressableByBuildNumberOrUuid(string pipeline, string expectedPath)
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(PipelineFixtures.Pipeline);
        handler.EnqueueJson(PipelineFixtures.StepPage);

        using var client = ToolTestHost.CreateClient(handler);

        _ = await PipelineReadTools.GetPipelineAsync(
            client, ToolTestHost.CreateOptions(), Repository, pipeline, Workspace, TestContext.Current.CancellationToken);

        Assert.Contains(expectedPath, handler.Requests[0].Uri!.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABranchNameIsNotAPipelineIdentifier()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() => PipelineReadTools.GetPipelineAsync(
            client, ToolTestHost.CreateOptions(), Repository, "main", Workspace, TestContext.Current.CancellationToken));

        Assert.Contains("listPipelines", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    /// <summary>
    /// The regression test for D16 on an <b>off-host</b> redirect, which nothing covered before:
    /// the log 307s to presigned storage, the <c>Range</c> header has to survive the hop, and the
    /// credential must not — a presigned URL carries its signature in the query string and storage
    /// rejects a request that also presents an <c>Authorization</c> header.
    /// </summary>
    [Fact]
    public async Task TheLogRedirectCarriesTheRangeToStorageButNotTheCredential()
    {
        using var handler = new StubHttpMessageHandler();

        handler.Enqueue(_ => StubHttpMessageHandler.CreateRedirect(
            HttpStatusCode.TemporaryRedirect,
            "https://micros--prod-east--bitbucketci-file-service--files.s3.amazonaws.com/log/x?X-Amz-Signature=abc"));

        handler.Enqueue(_ => PartialContent("first partial line\nsecond\nthird\n", from: 900, total: 1000));

        using var client = ToolTestHost.CreateClient(handler);

        var result = await PipelineReadTools.GetPipelineStepLogAsync(
            client,
            ToolTestHost.CreateOptions(),
            Repository,
            "8173",
            "{951411fa-aa47-472c-80ce-4f4f4899d1b3}",
            Workspace,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.Requests.Count);

        var api = handler.Requests[0];
        var storage = handler.Requests[1];

        Assert.Equal("api.bitbucket.org", api.Uri!.Host);
        Assert.True(api.Headers.ContainsKey("Authorization"));

        Assert.EndsWith("s3.amazonaws.com", storage.Uri!.Host, StringComparison.Ordinal);
        Assert.False(storage.Headers.ContainsKey("Authorization"));
        Assert.Contains("bytes=", storage.Headers["Range"], StringComparison.Ordinal);

        // The ranged body began mid-line, so the half line is dropped rather than reported whole.
        Assert.DoesNotContain("first partial line", result.Log, StringComparison.Ordinal);
        Assert.Contains("second", result.Log, StringComparison.Ordinal);
        Assert.True(result.Truncated);
        Assert.Equal(1000, result.TotalBytes);
    }

    /// <summary>
    /// The log endpoint serves <c>application/octet-stream</c> and answers <b>406</b> to an
    /// <c>Accept</c> that offers only <c>text/plain</c> — unlike the diff endpoint, which is where
    /// that header was copied from. Found against the live API, not by a stub, because a stub does
    /// no content negotiation.
    /// </summary>
    [Fact]
    public async Task TheLogRequestAcceptsAnOctetStream()
    {
        using var handler = new StubHttpMessageHandler();
        handler.Enqueue(_ => StubHttpMessageHandler.CreateResponse(HttpStatusCode.OK, "one line", "text/plain"));

        using var client = ToolTestHost.CreateClient(handler);

        _ = await PipelineReadTools.GetPipelineStepLogAsync(
            client,
            ToolTestHost.CreateOptions(),
            Repository,
            "8173",
            "{951411fa-aa47-472c-80ce-4f4f4899d1b3}",
            Workspace,
            cancellationToken: TestContext.Current.CancellationToken);

        var accept = Assert.Single(handler.Requests).Headers["Accept"];

        Assert.Contains("application/octet-stream", accept, StringComparison.Ordinal);
        Assert.DoesNotContain("application/json", accept, StringComparison.Ordinal);
    }

    /// <summary>Storage may ignore <c>Range</c> and send the whole log; the tail still comes back.</summary>
    [Fact]
    public async Task AnUnrangedLogResponseIsStillReducedToItsTail()
    {
        var builder = new StringBuilder();

        for (var i = 0; i < 500; i++)
        {
            builder.Append("line ").Append(i).Append('\n');
        }

        using var handler = new StubHttpMessageHandler();
        handler.Enqueue(_ => StubHttpMessageHandler.CreateResponse(HttpStatusCode.OK, builder.ToString(), "text/plain"));

        using var client = ToolTestHost.CreateClient(handler);

        var result = await PipelineReadTools.GetPipelineStepLogAsync(
            client,
            ToolTestHost.CreateOptions(),
            Repository,
            "8173",
            "{951411fa-aa47-472c-80ce-4f4f4899d1b3}",
            Workspace,
            maxLines: 5,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(5, result.LinesShown);
        Assert.Contains("line 499", result.Log, StringComparison.Ordinal);
        Assert.True(result.Truncated);
    }

    /// <summary>A 416 means the range was refused outright; one unranged retry recovers.</summary>
    [Fact]
    public async Task ARefusedRangeIsRetriedOnceWithoutTheHeader()
    {
        using var handler = new StubHttpMessageHandler();
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable));
        handler.Enqueue(_ => StubHttpMessageHandler.CreateResponse(HttpStatusCode.OK, "only\nlines\n", "text/plain"));

        using var client = ToolTestHost.CreateClient(handler);

        var result = await PipelineReadTools.GetPipelineStepLogAsync(
            client,
            ToolTestHost.CreateOptions(),
            Repository,
            "8173",
            "{951411fa-aa47-472c-80ce-4f4f4899d1b3}",
            Workspace,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.Requests.Count);
        Assert.True(handler.Requests[0].Headers.ContainsKey("Range"));
        Assert.False(handler.Requests[1].Headers.ContainsKey("Range"));
        Assert.Equal("only\nlines\n", result.Log);
    }

    [Fact]
    public async Task SearchingALogSendsNoRangeAndReportsTheMatchCount()
    {
        using var handler = new StubHttpMessageHandler();
        handler.Enqueue(_ => StubHttpMessageHandler.CreateResponse(
            HttpStatusCode.OK, "quiet\nERROR: boom\nquiet\nERROR: bang\n", "text/plain"));

        using var client = ToolTestHost.CreateClient(handler);

        var result = await PipelineReadTools.GetPipelineStepLogAsync(
            client,
            ToolTestHost.CreateOptions(),
            Repository,
            "8173",
            "{951411fa-aa47-472c-80ce-4f4f4899d1b3}",
            Workspace,
            pattern: "error",
            contextLines: 0,
            cancellationToken: TestContext.Current.CancellationToken);

        // A grep has to see every line, so this one is deliberately not ranged.
        Assert.False(Assert.Single(handler.Requests).Headers.ContainsKey("Range"));

        Assert.Equal(2, result.MatchCount);
        Assert.Contains("ERROR: boom", result.Log, StringComparison.Ordinal);
        Assert.DoesNotContain("quiet", result.Log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AModeThatContradictsPatternIsRefused()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() => PipelineReadTools.GetPipelineStepLogAsync(
            client,
            ToolTestHost.CreateOptions(),
            Repository,
            "8173",
            "{951411fa-aa47-472c-80ce-4f4f4899d1b3}",
            Workspace,
            mode: "tail",
            pattern: "boom",
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("contradicts pattern", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SearchModeWithoutAPatternIsRefused()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() => PipelineReadTools.GetPipelineStepLogAsync(
            client,
            ToolTestHost.CreateOptions(),
            Repository,
            "8173",
            "{951411fa-aa47-472c-80ce-4f4f4899d1b3}",
            Workspace,
            mode: "search",
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("needs pattern", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A pipeline 403 must name the scope that is actually missing. A credential created before
    /// these tools existed has every pull-request scope and still cannot read a pipeline.
    /// </summary>
    [Fact]
    public async Task APipelineForbiddenNamesThePipelineScopeAndTheReLoginStep()
    {
        using var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.Forbidden, """{"type":"error","error":{"message":"Access denied."}}""");

        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() => PipelineReadTools.ListPipelinesAsync(
            client,
            ToolTestHost.CreateOptions(),
            Repository,
            Workspace,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("read:pipeline:bitbucket", exception.Message, StringComparison.Ordinal);
        Assert.Contains("bitbucket-mcp logout", exception.Message, StringComparison.Ordinal);
        Assert.Contains("listCodeInsights", exception.Message, StringComparison.Ordinal);

        // It must not recite the pull-request scopes, which are not the problem here.
        Assert.DoesNotContain("write:pullrequest:bitbucket", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// listCodeInsights runs on the repository scope every credential already holds, so it must not
    /// tell anyone to add a pipeline scope it does not need.
    /// </summary>
    [Fact]
    public async Task ACodeInsightsForbiddenDoesNotAskForThePipelineScope()
    {
        using var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.Forbidden, """{"type":"error","error":{"message":"Access denied."}}""");

        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() => PipelineReadTools.ListCodeInsightsAsync(
            client,
            ToolTestHost.CreateOptions(),
            Repository,
            "cbdb1c937854dda0367163e91df93f55356e79cd",
            Workspace,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.DoesNotContain("read:pipeline:bitbucket", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CodeInsightsExpandsOnlyTheFailingReport()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(PipelineFixtures.ReportPage);
        handler.EnqueueJson(PipelineFixtures.AnnotationPage);

        using var client = ToolTestHost.CreateClient(handler);

        var result = await PipelineReadTools.ListCodeInsightsAsync(
            client,
            ToolTestHost.CreateOptions(),
            Repository,
            "cbdb1c937854dda0367163e91df93f55356e79cd",
            Workspace,
            cancellationToken: TestContext.Current.CancellationToken);

        // Two reports, but only the FAILED one costs a second request.
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(2, result.Reports.Count);

        var annotation = Assert.Single(result.Annotations);

        Assert.Equal("src/Api/Retry.cs", annotation.Path);
        Assert.Equal(42, annotation.Line);
        Assert.Equal("HIGH", annotation.Severity);
        Assert.Equal("Static analysis", annotation.ReportTitle);
    }

    [Fact]
    public async Task ACodeInsightsCursorWithoutAReportIdIsRefused()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() => PipelineReadTools.ListCodeInsightsAsync(
            client,
            ToolTestHost.CreateOptions(),
            Repository,
            "cbdb1c937854dda0367163e91df93f55356e79cd",
            Workspace,
            cursor: "whatever",
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("reportId", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ABranchNameIsNotACommit()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Assert.ThrowsAsync<McpException>(() => PipelineReadTools.ListCodeInsightsAsync(
            client, ToolTestHost.CreateOptions(), Repository, "main", Workspace,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("commit hash", exception.Message, StringComparison.Ordinal);
    }

    private static HttpResponseMessage PartialContent(string body, long from, long total)
    {
        var response = StubHttpMessageHandler.CreateResponse(HttpStatusCode.PartialContent, body, "text/plain");

        response.Content.Headers.ContentRange =
            new System.Net.Http.Headers.ContentRangeHeaderValue(from, from + body.Length - 1, total);

        return response;
    }
}

/// <summary>
/// Golden Bitbucket responses for the pipeline tools, trimmed exactly the way the field sets ask
/// for them. Shapes taken from the live API on 2026-09-09.
/// </summary>
internal static class PipelineFixtures
{
    internal const string PipelinePage = """
        {
          "size": 3,
          "next": "https://api.bitbucket.org/2.0/repositories/%7Bb2919ebc%7D/%7B55c162b8%7D/pipelines/?page=2&pagelen=10",
          "values": [
            {
              "uuid": "{c1db2bb8-6168-4675-b85a-e95f450523f9}",
              "build_number": 8173,
              "created_on": "2026-09-08T23:55:57.826992388Z",
              "completed_on": "2026-09-08T23:57:09.127136647Z",
              "duration_in_seconds": 53,
              "state": { "name": "COMPLETED", "result": { "name": "FAILED" } },
              "target": {
                "ref_type": "branch",
                "ref_name": "master",
                "commit": { "hash": "cbdb1c937854dda0367163e91df93f55356e79cd" },
                "selector": { "type": "branches", "pattern": "master" }
              },
              "trigger": { "name": "PUSH" },
              "creator": { "display_name": "Ada Lovelace", "uuid": "{11111111-2222-3333-4444-555555555555}" }
            },
            {
              "uuid": "{aaaaaaaa-0000-0000-0000-000000000001}",
              "build_number": 8174,
              "state": { "name": "IN_PROGRESS", "stage": { "name": "RUNNING" } },
              "target": { "ref_type": "branch", "ref_name": "master" },
              "trigger": { "name": "MANUAL" }
            },
            {
              "uuid": "{aaaaaaaa-0000-0000-0000-000000000002}",
              "build_number": 8175,
              "state": { "name": "PENDING" },
              "target": { "ref_type": "branch", "ref_name": "master" },
              "trigger": { "name": "SCHEDULED" }
            }
          ]
        }
        """;

    internal const string Pipeline = """
        {
          "uuid": "{c1db2bb8-6168-4675-b85a-e95f450523f9}",
          "build_number": 8173,
          "created_on": "2026-09-08T23:55:57.826992388Z",
          "completed_on": "2026-09-08T23:57:09.127136647Z",
          "duration_in_seconds": 53,
          "state": { "name": "COMPLETED", "result": { "name": "FAILED" } },
          "target": {
            "ref_type": "branch",
            "ref_name": "master",
            "commit": { "hash": "cbdb1c937854dda0367163e91df93f55356e79cd" },
            "selector": { "type": "branches", "pattern": "master" }
          },
          "trigger": { "name": "PUSH" },
          "creator": { "display_name": "Ada Lovelace", "uuid": "{11111111-2222-3333-4444-555555555555}" }
        }
        """;

    internal const string StepPage = """
        {
          "size": 2,
          "values": [
            {
              "uuid": "{951411fa-aa47-472c-80ce-4f4f4899d1b3}",
              "name": "Test and Build",
              "started_on": "2026-09-08T23:56:15.975066468Z",
              "completed_on": "2026-09-08T23:57:08.874950599Z",
              "duration_in_seconds": 53,
              "image": { "name": "python:3.11" },
              "state": {
                "name": "COMPLETED",
                "result": { "name": "FAILED", "error": { "key": "step-timeout", "message": "The step timed out." } }
              }
            },
            {
              "uuid": "{951411fa-aa47-472c-80ce-4f4f4899d1b4}",
              "name": "Deploy",
              "state": { "name": "COMPLETED", "result": { "name": "NOT_RUN" } }
            }
          ]
        }
        """;

    /// <summary>A step page that reports a continuation, so the list is knowingly incomplete.</summary>
    internal const string StepPageWithMore = """
        {
          "size": 120,
          "next": "https://api.bitbucket.org/2.0/repositories/acme/widgets/pipelines/8173/steps?page=2",
          "values": [
            {
              "uuid": "{951411fa-aa47-472c-80ce-4f4f4899d1b3}",
              "name": "Test and Build",
              "state": { "name": "COMPLETED", "result": { "name": "SUCCESSFUL" } }
            }
          ]
        }
        """;

    internal const string ReportPage = """
        {
          "size": 2,
          "values": [
            {
              "uuid": "{report-0000-0000-0000-000000000001}",
              "title": "Static analysis",
              "details": "12 rules evaluated",
              "reporter": "analyzer",
              "report_type": "BUG",
              "result": "FAILED",
              "link": "https://example.invalid/report/1",
              "created_on": "2026-09-08T23:57:00Z"
            },
            {
              "uuid": "{report-0000-0000-0000-000000000002}",
              "title": "Coverage",
              "reporter": "coverage-tool",
              "report_type": "COVERAGE",
              "result": "PASSED"
            }
          ]
        }
        """;

    internal const string AnnotationPage = """
        {
          "size": 1,
          "values": [
            {
              "uuid": "{annot-0000-0000-0000-000000000001}",
              "path": "src/Api/Retry.cs",
              "line": 42,
              "summary": "Retry-After is ignored.",
              "details": "The handler sleeps a fixed delay instead of honouring the header.",
              "annotation_type": "BUG",
              "result": "FAILED",
              "severity": "HIGH",
              "link": "https://example.invalid/report/1/1"
            }
          ]
        }
        """;
}
