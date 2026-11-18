using System.Net;
using System.Text;

using Bitbucket.Mcp.Tools;

using ModelContextProtocol;


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

    [Test]
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
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(result.Pipelines.Count, Is.EqualTo(3));

        // {name: COMPLETED, result: {name: FAILED}} -> FAILED
        Assert.That(result.Pipelines[0].State, Is.EqualTo("FAILED"));
        Assert.That(result.Pipelines[0].Completed, Is.True);

        // {name: IN_PROGRESS, stage: {name: RUNNING}} -> RUNNING, and not finished
        Assert.That(result.Pipelines[1].State, Is.EqualTo("RUNNING"));
        Assert.That(result.Pipelines[1].Completed, Is.False);

        // {name: PENDING} -> PENDING
        Assert.That(result.Pipelines[2].State, Is.EqualTo("PENDING"));
        Assert.That(result.Pipelines[2].Completed, Is.False);
    }

    [Test]
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
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var uri = Expect.Single(handler.Requests).Uri!;

        Assert.That(uri.AbsolutePath, Does.Contain("/repositories/acme/widgets/pipelines"));
        Assert.That(uri.Query, Does.Contain("target.branch=feature%2Fclamp"));
        Assert.That(uri.Query, Does.Contain("sort=-created_on"));
        Assert.That(uri.Query, Does.Contain("fields="));

        // A pipeline carries no html link, so the page URL is composed from the build number.
        Assert.That(result.Pipelines[0].Url, Is.EqualTo("https://bitbucket.org/acme/widgets/pipelines/results/8173"));
    }

    /// <summary>
    /// Bitbucket's <c>status</c> filter does not speak the vocabulary its responses use:
    /// <c>SUCCESSFUL</c> in a result is <c>PASSED</c> in the filter, and asking for
    /// <c>status=SUCCESSFUL</c> matches nothing at all.
    /// </summary>
    [TestCase("SUCCESSFUL", "PASSED")]
    [TestCase("successful", "PASSED")]
    [TestCase("PASSED", "PASSED")]
    [TestCase("FAILED", "FAILED")]
    [TestCase("RUNNING", "BUILDING")]
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
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(Expect.Single(handler.Requests).Uri!.Query, Does.Contain($"status={sent}"));
    }

    /// <summary>
    /// Bitbucket answers an unrecognised status with 200 and an empty page, which reads exactly like
    /// "this repository has never run a pipeline". Refusing it here is the only way the caller finds
    /// out they asked the wrong question.
    /// </summary>
    [Test]
    public async Task AnUnknownStatusIsRefusedRatherThanSentAndSilentlyIgnored()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Expect.ThrowsAsync<McpException>(() => PipelineReadTools.ListPipelinesAsync(
            client,
            ToolTestHost.CreateOptions(),
            Repository,
            Workspace,
            status: "COMPLETED",
            cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception.Message, Does.Contain("SUCCESSFUL"));
        Assert.That(handler.Requests, Is.Empty);
    }

    /// <summary>
    /// The pipelines endpoint is the one that does <b>not</b> echo <c>fields=</c> into its
    /// <c>next</c> link. Following the cursor verbatim would return full objects — measured at
    /// 2,615 bytes each against 50 trimmed — so the field set is re-applied.
    /// </summary>
    [Test]
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
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(first.NextCursor, Is.Not.Null);

        handler.EnqueueJson(PipelineFixtures.PipelinePage);

        _ = await PipelineReadTools.ListPipelinesAsync(
            client,
            ToolTestHost.CreateOptions(),
            Repository,
            Workspace,
            cursor: first.NextCursor,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var second = handler.Requests[1].Uri!;

        Assert.That(second.Query, Does.Contain("page=2"));
        Assert.That(second.Query, Does.Contain("fields="));
    }

    [Test]
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
            TestContext.CurrentContext.CancellationToken);

        Assert.That(handler.Requests.Count, Is.EqualTo(2));
        Assert.That(result.Steps.Count, Is.EqualTo(2));

        Assert.That(result.Steps[0].Name, Is.EqualTo("Test and Build"));
        Assert.That(result.Steps[0].State, Is.EqualTo("FAILED"));
        Assert.That(result.Steps[0].ErrorMessage, Is.EqualTo("The step timed out."));
        Assert.That(result.Steps[1].State, Is.EqualTo("NOT_RUN"));

        Assert.That(result.FailedStepUuid, Is.EqualTo("{951411fa-aa47-472c-80ce-4f4f4899d1b3}"));

        // The hint carries the error and the exact next call, so the log is a choice rather than a
        // reflex.
        Assert.That(result.Hint!, Does.Contain("The step timed out."));
        Assert.That(result.Hint!, Does.Contain("getPipelineStepLog"));
        Assert.That(result.Hint!, Does.Contain("{951411fa-aa47-472c-80ce-4f4f4899d1b3}"));
    }

    /// <summary>
    /// A run can carry more steps than one page holds, and a truncated step list must not read like
    /// a complete one — the same rule the diff tools follow.
    /// </summary>
    [Test]
    public async Task AStepListCutShortByPagingSaysSo()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(PipelineFixtures.Pipeline);
        handler.EnqueueJson(PipelineFixtures.StepPageWithMore);

        using var client = ToolTestHost.CreateClient(handler);

        var result = await PipelineReadTools.GetPipelineAsync(
            client, ToolTestHost.CreateOptions(), Repository, "8173", Workspace, TestContext.CurrentContext.CancellationToken);

        Assert.That(result.Hint!, Does.Contain("only the first"));
    }

    [TestCase("8173", "/pipelines/8173")]
    [TestCase("#8173", "/pipelines/8173")]
    [TestCase("{c1db2bb8-6168-4675-b85a-e95f450523f9}", "/pipelines/%7Bc1db2bb8-6168-4675-b85a-e95f450523f9%7D")]
    [TestCase("c1db2bb8-6168-4675-b85a-e95f450523f9", "/pipelines/%7Bc1db2bb8-6168-4675-b85a-e95f450523f9%7D")]
    public async Task APipelineIsAddressableByBuildNumberOrUuid(string pipeline, string expectedPath)
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(PipelineFixtures.Pipeline);
        handler.EnqueueJson(PipelineFixtures.StepPage);

        using var client = ToolTestHost.CreateClient(handler);

        _ = await PipelineReadTools.GetPipelineAsync(
            client, ToolTestHost.CreateOptions(), Repository, pipeline, Workspace, TestContext.CurrentContext.CancellationToken);

        Assert.That(handler.Requests[0].Uri!.AbsolutePath, Does.Contain(expectedPath));
    }

    [Test]
    public async Task ABranchNameIsNotAPipelineIdentifier()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Expect.ThrowsAsync<McpException>(() => PipelineReadTools.GetPipelineAsync(
            client, ToolTestHost.CreateOptions(), Repository, "main", Workspace, TestContext.CurrentContext.CancellationToken));

        Assert.That(exception.Message, Does.Contain("listPipelines"));
        Assert.That(handler.Requests, Is.Empty);
    }

    /// <summary>
    /// The regression test for D16 on an <b>off-host</b> redirect, which nothing covered before:
    /// the log 307s to presigned storage, the <c>Range</c> header has to survive the hop, and the
    /// credential must not — a presigned URL carries its signature in the query string and storage
    /// rejects a request that also presents an <c>Authorization</c> header.
    /// </summary>
    [Test]
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
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(handler.Requests.Count, Is.EqualTo(2));

        var api = handler.Requests[0];
        var storage = handler.Requests[1];

        Assert.That(api.Uri!.Host, Is.EqualTo("api.bitbucket.org"));
        Assert.That(api.Headers.ContainsKey("Authorization"), Is.True);

        Assert.That(storage.Uri!.Host, Does.EndWith("s3.amazonaws.com"));
        Assert.That(storage.Headers.ContainsKey("Authorization"), Is.False);
        Assert.That(storage.Headers["Range"], Does.Contain("bytes="));

        // The ranged body began mid-line, so the half line is dropped rather than reported whole.
        Assert.That(result.Log, Does.Not.Contain("first partial line"));
        Assert.That(result.Log, Does.Contain("second"));
        Assert.That(result.Truncated, Is.True);
        Assert.That(result.TotalBytes, Is.EqualTo(1000));
    }

    /// <summary>
    /// The log endpoint serves <c>application/octet-stream</c> and answers <b>406</b> to an
    /// <c>Accept</c> that offers only <c>text/plain</c> — unlike the diff endpoint, which is where
    /// that header was copied from. Found against the live API, not by a stub, because a stub does
    /// no content negotiation.
    /// </summary>
    [Test]
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
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var accept = Expect.Single(handler.Requests).Headers["Accept"];

        Assert.That(accept, Does.Contain("application/octet-stream"));
        Assert.That(accept, Does.Not.Contain("application/json"));
    }

    /// <summary>Storage may ignore <c>Range</c> and send the whole log; the tail still comes back.</summary>
    [Test]
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
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(result.LinesShown, Is.EqualTo(5));
        Assert.That(result.Log, Does.Contain("line 499"));
        Assert.That(result.Truncated, Is.True);
    }

    /// <summary>A 416 means the range was refused outright; one unranged retry recovers.</summary>
    [Test]
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
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(handler.Requests.Count, Is.EqualTo(2));
        Assert.That(handler.Requests[0].Headers.ContainsKey("Range"), Is.True);
        Assert.That(handler.Requests[1].Headers.ContainsKey("Range"), Is.False);
        Assert.That(result.Log, Is.EqualTo("only\nlines\n"));
    }

    [Test]
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
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        // A grep has to see every line, so this one is deliberately not ranged.
        Assert.That(Expect.Single(handler.Requests).Headers.ContainsKey("Range"), Is.False);

        Assert.That(result.MatchCount, Is.EqualTo(2));
        Assert.That(result.Log, Does.Contain("ERROR: boom"));
        Assert.That(result.Log, Does.Not.Contain("quiet"));
    }

    [Test]
    public async Task AModeThatContradictsPatternIsRefused()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Expect.ThrowsAsync<McpException>(() => PipelineReadTools.GetPipelineStepLogAsync(
            client,
            ToolTestHost.CreateOptions(),
            Repository,
            "8173",
            "{951411fa-aa47-472c-80ce-4f4f4899d1b3}",
            Workspace,
            mode: "tail",
            pattern: "boom",
            cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception.Message, Does.Contain("contradicts pattern"));
        Assert.That(handler.Requests, Is.Empty);
    }

    [Test]
    public async Task SearchModeWithoutAPatternIsRefused()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Expect.ThrowsAsync<McpException>(() => PipelineReadTools.GetPipelineStepLogAsync(
            client,
            ToolTestHost.CreateOptions(),
            Repository,
            "8173",
            "{951411fa-aa47-472c-80ce-4f4f4899d1b3}",
            Workspace,
            mode: "search",
            cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception.Message, Does.Contain("needs pattern"));
    }

    /// <summary>
    /// A pipeline 403 must name the scope that is actually missing. A credential created before
    /// these tools existed has every pull-request scope and still cannot read a pipeline.
    /// </summary>
    [Test]
    public async Task APipelineForbiddenNamesThePipelineScopeAndTheReLoginStep()
    {
        using var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.Forbidden, """{"type":"error","error":{"message":"Access denied."}}""");

        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Expect.ThrowsAsync<McpException>(() => PipelineReadTools.ListPipelinesAsync(
            client,
            ToolTestHost.CreateOptions(),
            Repository,
            Workspace,
            cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception.Message, Does.Contain("read:pipeline:bitbucket"));
        Assert.That(exception.Message, Does.Contain("bitbucket-mcp logout"));
        Assert.That(exception.Message, Does.Contain("listCodeInsights"));

        // It must not recite the pull-request scopes, which are not the problem here.
        Assert.That(exception.Message, Does.Not.Contain("write:pullrequest:bitbucket"));
    }

    /// <summary>
    /// listCodeInsights runs on the repository scope every credential already holds, so it must not
    /// tell anyone to add a pipeline scope it does not need.
    /// </summary>
    [Test]
    public async Task ACodeInsightsForbiddenDoesNotAskForThePipelineScope()
    {
        using var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.Forbidden, """{"type":"error","error":{"message":"Access denied."}}""");

        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Expect.ThrowsAsync<McpException>(() => PipelineReadTools.ListCodeInsightsAsync(
            client,
            ToolTestHost.CreateOptions(),
            Repository,
            "cbdb1c937854dda0367163e91df93f55356e79cd",
            Workspace,
            cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception.Message, Does.Not.Contain("read:pipeline:bitbucket"));
    }

    [Test]
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
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        // Two reports, but only the FAILED one costs a second request.
        Assert.That(handler.Requests.Count, Is.EqualTo(2));
        Assert.That(result.Reports.Count, Is.EqualTo(2));

        var annotation = Expect.Single(result.Annotations);

        Assert.That(annotation.Path, Is.EqualTo("src/Api/Retry.cs"));
        Assert.That(annotation.Line, Is.EqualTo(42));
        Assert.That(annotation.Severity, Is.EqualTo("HIGH"));
        Assert.That(annotation.ReportTitle, Is.EqualTo("Static analysis"));
    }

    [Test]
    public async Task ACodeInsightsCursorWithoutAReportIdIsRefused()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Expect.ThrowsAsync<McpException>(() => PipelineReadTools.ListCodeInsightsAsync(
            client,
            ToolTestHost.CreateOptions(),
            Repository,
            "cbdb1c937854dda0367163e91df93f55356e79cd",
            Workspace,
            cursor: "whatever",
            cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception.Message, Does.Contain("reportId"));
        Assert.That(handler.Requests, Is.Empty);
    }

    [Test]
    public async Task ABranchNameIsNotACommit()
    {
        using var handler = new StubHttpMessageHandler();
        using var client = ToolTestHost.CreateClient(handler);

        var exception = await Expect.ThrowsAsync<McpException>(() => PipelineReadTools.ListCodeInsightsAsync(
            client, ToolTestHost.CreateOptions(), Repository, "main", Workspace,
            cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception.Message, Does.Contain("commit hash"));
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
