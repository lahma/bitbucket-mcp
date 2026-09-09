using System.ComponentModel;

using Bitbucket.Mcp.Configuration;
using Bitbucket.Mcp.Http;
using Bitbucket.Mcp.Pipelines;
using Bitbucket.Mcp.Tools.Models;

using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Bitbucket.Mcp.Tools;

/// <summary>
/// Read-only tools for answering "what is CI doing?" and "why did the build fail?".
/// </summary>
/// <remarks>
/// <para>
/// The same shape as <see cref="PullRequestReadTools"/> in every respect: static methods, the
/// client and options bound from DI as plain leading parameters (D8), sealed rather than static so
/// it can be a type argument to <c>WithTools&lt;T&gt;</c>, and every body inside
/// <see cref="ToolErrors.ExecuteAsync"/>.
/// </para>
/// <para>
/// <b>The surface is read-only on purpose</b> (D20). Running or stopping a pipeline needs a write
/// scope on top of the read one and spends the workspace's build minutes; the problem these tools
/// exist for is diagnosis, and a re-run tool without polling only invites a model into a loop.
/// </para>
/// <para>
/// <c>listCodeInsights</c> lives here although it is not a Pipelines endpoint. It answers the same
/// question — why is the build red — from the commit side, and it needs no scope the pull-request
/// tools do not already hold, which makes it the fallback when the pipeline scope is missing. A
/// class of its own for one tool would buy nothing.
/// </para>
/// </remarks>
[McpServerToolType]
internal sealed class PipelineReadTools
{
    private PipelineReadTools()
    {
    }

    [McpServerTool(
        Name = "listPipelines",
        Title = "List pipelines",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = true,
        UseStructuredContent = true)]
    [Description(
        "Lists a repository's pipeline runs, newest first — the answer to \"is CI green?\". Each entry carries " +
        "the flattened state (SUCCESSFUL, FAILED, ERROR, STOPPED, EXPIRED, PENDING, RUNNING, PAUSED or " +
        "HALTED), the branch and commit it ran on, how long it took, and both of the identifiers that address " +
        "it later: uuid and buildNumber. To find a pull request's builds, pass its source branch as " +
        "targetBranch, or its sourceCommit as commit. An empty list means the repository has never run a " +
        "pipeline, not that the build failed. Results are paginated: pass the returned nextCursor back as " +
        "cursor for the next page.")]
    public static async Task<PipelineListResult> ListPipelinesAsync(
        BitbucketApiClient client,
        BitbucketMcpOptions options,
        [Description("Repository slug — the second URL segment of bitbucket.org/{workspace}/{repository}, not the repository's display name.")]
        string repository,
        [Description("Workspace slug — the first URL segment of bitbucket.org/{workspace}/{repository}, not the workspace's display name. Optional when BITBUCKET_DEFAULT_WORKSPACE is set.")]
        string? workspace = null,
        [Description("Only runs for this branch, without any refs/heads/ prefix. A pull request's sourceBranch is what finds its builds.")]
        string? targetBranch = null,
        [Description("Only runs for this commit hash. More precise than targetBranch when a branch has moved on.")]
        string? commit = null,
        [Description("Only runs in this state, spelled the way results spell it: SUCCESSFUL, FAILED, ERROR, STOPPED, EXPIRED, PENDING, RUNNING, PAUSED or HALTED. Omit for every run.")]
        string? status = null,
        [Description("Runs per page, clamped to 1-50. Omit for Bitbucket's default.")]
        int? pageSize = null,
        [Description("Opaque cursor from a previous response's nextCursor — pass it back verbatim. Omit to start from the first page.")]
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        var slug = ToolDefaults.RequireRepository(repository);
        var resolvedWorkspace = ToolDefaults.ResolveWorkspace(workspace, options);
        var resolvedStatus = ToolDefaults.ResolvePipelineStatus(status);
        var branch = ToolDefaults.ResolveBranchFilter(targetBranch);
        var resolvedCommit = string.IsNullOrWhiteSpace(commit) ? null : ToolDefaults.RequireCommit(commit);

        var context = new ToolCallContext(
            "listPipelines", resolvedWorkspace, slug, Scope: ToolScope.Pipeline, Resource: "this repository's pipelines");

        return await ToolErrors.ExecuteAsync(context, async () =>
        {
            var page = await client.ListPipelinesAsync(
                    resolvedWorkspace,
                    slug,
                    branch,
                    resolvedCommit,
                    resolvedStatus,
                    ToolDefaults.ClampPageSize(pageSize),
                    cursor,
                    cancellationToken)
                .ConfigureAwait(false);

            return ResultMapper.Pipelines(page, resolvedWorkspace, slug);
        }).ConfigureAwait(false);
    }

    [McpServerTool(
        Name = "getPipeline",
        Title = "Get pipeline",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = true,
        UseStructuredContent = true)]
    [Description(
        "Reads one pipeline run together with its steps — which step failed, and usually why. Start here after " +
        "listPipelines, and read each step's errorMessage BEFORE fetching any log: \"the step timed out\" or " +
        "\"the image could not be pulled\" is frequently the whole answer. The result reports failedStepUuid, " +
        "which is exactly what getPipelineStepLog wants as stepUuid, and commitHash, which listCodeInsights " +
        "takes as commit. Accepts either identifier as pipeline: a build number like \"42\" or a braced uuid.")]
    public static async Task<PipelineDetail> GetPipelineAsync(
        BitbucketApiClient client,
        BitbucketMcpOptions options,
        [Description("Repository slug — the second URL segment of bitbucket.org/{workspace}/{repository}, not the repository's display name.")]
        string repository,
        [Description("The run to read: a build number as it appears in the pipeline's URL, or a pipeline uuid in braced form. Both come from listPipelines.")]
        string pipeline,
        [Description("Workspace slug — the first URL segment of bitbucket.org/{workspace}/{repository}, not the workspace's display name. Optional when BITBUCKET_DEFAULT_WORKSPACE is set.")]
        string? workspace = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        var slug = ToolDefaults.RequireRepository(repository);
        var resolvedWorkspace = ToolDefaults.ResolveWorkspace(workspace, options);
        var id = ToolDefaults.RequirePipelineId(pipeline);

        var context = new ToolCallContext(
            "getPipeline", resolvedWorkspace, slug, Scope: ToolScope.Pipeline, Resource: $"pipeline {id}");

        return await ToolErrors.ExecuteAsync(context, async () =>
        {
            // Two requests, always. A pipeline on its own cannot answer the question the tool is
            // for — the steps are where a failure actually lives.
            var dto = await client.GetPipelineAsync(resolvedWorkspace, slug, id, cancellationToken)
                .ConfigureAwait(false);

            var steps = await client.ListPipelineStepsAsync(
                    resolvedWorkspace, slug, id, ToolDefaults.MaxPageSize, cursor: null, cancellationToken)
                .ConfigureAwait(false);

            return ResultMapper.Pipeline(dto, steps.Items, steps.NextCursor is not null, resolvedWorkspace, slug);
        }).ConfigureAwait(false);
    }

    [McpServerTool(
        Name = "getPipelineStepLog",
        Title = "Get pipeline step log",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = true,
        UseStructuredContent = true)]
    [Description(
        "Reads one step's build log. By default it returns the END of the log, which is where a failing step " +
        "reports why — a pipeline step runs under set -e, so the last thing printed is the thing that broke. " +
        "Pass pattern to search the whole log for a literal substring instead (case-insensitive, not a regular " +
        "expression), with contextLines around each hit; supplying pattern selects search mode on its own, so " +
        "mode never has to be set. Use mode=\"head\" only for a step that died before producing output of its " +
        "own, such as an image that would not pull. The response is always truncated to maxLines with the cut " +
        "marked inside the text and reported by truncated — never present a truncated log as the whole log. " +
        "Ask getPipeline for the failing step's errorMessage first; it often makes this call unnecessary.")]
    public static async Task<PipelineStepLogResult> GetPipelineStepLogAsync(
        BitbucketApiClient client,
        BitbucketMcpOptions options,
        [Description("Repository slug — the second URL segment of bitbucket.org/{workspace}/{repository}, not the repository's display name.")]
        string repository,
        [Description("The run the step belongs to: a build number, or a pipeline uuid in braced form.")]
        string pipeline,
        [Description("The step's uuid in braced form, from getPipeline — failedStepUuid is usually the one wanted.")]
        string stepUuid,
        [Description("Workspace slug — the first URL segment of bitbucket.org/{workspace}/{repository}, not the workspace's display name. Optional when BITBUCKET_DEFAULT_WORKSPACE is set.")]
        string? workspace = null,
        [Description("\"tail\" (default) for the end of the log, \"head\" for the start, or \"search\" to keep only matching lines. Omit it: supplying pattern already selects search.")]
        string? mode = null,
        [Description("Literal text to search for, case-insensitive — not a regular expression. Supplying this selects search mode.")]
        string? pattern = null,
        [Description("Lines to keep on either side of each search hit. Only meaningful with pattern. Omit for 3.")]
        int? contextLines = null,
        [Description("Cap on the log lines returned, clamped to 1-5000. Omit for the server default (BITBUCKET_MCP_MAX_LOG_LINES, 200).")]
        int? maxLines = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);

        var slug = ToolDefaults.RequireRepository(repository);
        var resolvedWorkspace = ToolDefaults.ResolveWorkspace(workspace, options);
        var id = ToolDefaults.RequirePipelineId(pipeline);
        var step = ToolDefaults.RequireStepUuid(stepUuid);
        var resolved = ResolveLogMode(mode, pattern);

        var lines = ToolDefaults.ClampLogLines(maxLines, options.MaxLogLines);

        var context = new ToolCallContext(
            "getPipelineStepLog",
            resolvedWorkspace,
            slug,
            Scope: ToolScope.Pipeline,
            Resource: $"the log of step {step} of pipeline {id}");

        return await ToolErrors.ExecuteAsync(context, async () =>
        {
            var excerpt = await client.GetPipelineStepLogAsync(
                    resolvedWorkspace,
                    slug,
                    id,
                    step,
                    resolved,
                    pattern?.Trim(),
                    ToolDefaults.ClampContextLines(contextLines),
                    lines,
                    // Enough bytes to make the line cap the binding constraint rather than the byte
                    // one, without letting a huge maxLines turn into an unbounded buffer.
                    Math.Min((long) lines * 512, options.MaxLogBytes),
                    cancellationToken)
                .ConfigureAwait(false);

            return ResultMapper.StepLog(excerpt);
        }).ConfigureAwait(false);
    }

    [McpServerTool(
        Name = "listCodeInsights",
        Title = "List code insights",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = true,
        UseStructuredContent = true)]
    [Description(
        "Lists the Code Insights reports on a commit and the file-and-line findings behind the failing ones — " +
        "what a linter, scanner, coverage or test tool published about that revision. This is the one build " +
        "signal that points at a place in the code rather than at a log, so it is often faster than reading " +
        "one: a failing analyzer names the file, the line and the message directly. Pass a pull request's " +
        "sourceCommit, or a pipeline's commitHash. With no reportId it returns every report plus the " +
        "annotations of the FAILED ones, up to maxAnnotations; pass reportId to page through one report's " +
        "annotations in full. An empty result means no tool published anything for that commit.")]
    public static async Task<CodeInsightsResult> ListCodeInsightsAsync(
        BitbucketApiClient client,
        BitbucketMcpOptions options,
        [Description("Repository slug — the second URL segment of bitbucket.org/{workspace}/{repository}, not the repository's display name.")]
        string repository,
        [Description("The commit hash to read reports for — a pull request's sourceCommit, or a pipeline's commitHash.")]
        string commit,
        [Description("Workspace slug — the first URL segment of bitbucket.org/{workspace}/{repository}, not the workspace's display name. Optional when BITBUCKET_DEFAULT_WORKSPACE is set.")]
        string? workspace = null,
        [Description("Read one report's annotations in full, paginated. The reportId comes from a previous call's reports list. Omit to see every report with the failing ones expanded.")]
        string? reportId = null,
        [Description("Cap on annotations pulled in across the failing reports, clamped to 1-100. Only meaningful without reportId. Omit for 20.")]
        int? maxAnnotations = null,
        [Description("Annotations per page, clamped to 1-50. Only meaningful with reportId. Omit for Bitbucket's default.")]
        int? pageSize = null,
        [Description("Opaque cursor from a previous response's nextCursor — pass it back verbatim, together with the same reportId. Omit to start from the first page.")]
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        var slug = ToolDefaults.RequireRepository(repository);
        var resolvedWorkspace = ToolDefaults.ResolveWorkspace(workspace, options);
        var sha = ToolDefaults.RequireCommit(commit);
        var report = string.IsNullOrWhiteSpace(reportId) ? null : reportId.Trim();

        if (cursor is not null && report is null)
        {
            throw new McpException(
                "cursor is only meaningful together with reportId: it pages through one report's annotations. "
                + "Call listCodeInsights with just the commit to see the reports, then pass one report's "
                + "reportId back with the cursor.");
        }

        // The commit scope is the repository one, which every credential that can read a pull
        // request already holds — so this must not send anyone looking for a pipeline scope.
        var context = new ToolCallContext(
            "listCodeInsights", resolvedWorkspace, slug, Resource: $"the code insights on commit {sha}");

        return await ToolErrors.ExecuteAsync(context, async () =>
            report is null
                ? await AllReportsAsync(
                        client, resolvedWorkspace, slug, sha, ToolDefaults.ClampAnnotations(maxAnnotations), cancellationToken)
                    .ConfigureAwait(false)
                : await OneReportAsync(
                        client, resolvedWorkspace, slug, sha, report, ToolDefaults.ClampPageSize(pageSize), cursor, cancellationToken)
                    .ConfigureAwait(false))
            .ConfigureAwait(false);
    }

    /// <summary>Every report on the commit, with the failing ones expanded into annotations.</summary>
    /// <remarks>
    /// Only <c>FAILED</c> reports are expanded. A passing coverage report has annotations too, and
    /// fetching them would spend the budget on findings nobody is looking for.
    /// </remarks>
    private static async Task<CodeInsightsResult> AllReportsAsync(
        BitbucketApiClient client,
        string workspace,
        string repositorySlug,
        string commit,
        int maxAnnotations,
        CancellationToken cancellationToken)
    {
        var page = await client
            .ListCodeInsightsReportsAsync(workspace, repositorySlug, commit, ToolDefaults.MaxPageSize, null, cancellationToken)
            .ConfigureAwait(false);

        var reports = new List<CodeInsightReport>(page.Items.Count);
        var annotations = new List<CodeInsightAnnotation>();
        var truncated = false;

        foreach (var dto in page.Items)
        {
            var id = dto.Uuid ?? dto.ExternalId;
            var expand = string.Equals(dto.Result, "FAILED", StringComparison.OrdinalIgnoreCase) && id is not null;

            if (!expand)
            {
                reports.Add(ResultMapper.Report(dto, annotationsShown: null));
                continue;
            }

            if (annotations.Count >= maxAnnotations)
            {
                truncated = true;
                reports.Add(ResultMapper.Report(dto, annotationsShown: 0));
                continue;
            }

            var found = await client
                .ListCodeInsightsAnnotationsAsync(
                    workspace, repositorySlug, commit, id!, ToolDefaults.MaxPageSize, null, cancellationToken)
                .ConfigureAwait(false);

            var room = maxAnnotations - annotations.Count;
            var taken = Math.Min(room, found.Items.Count);

            for (var i = 0; i < taken; i++)
            {
                annotations.Add(ResultMapper.Annotation(found.Items[i], dto.Title));
            }

            truncated |= taken < found.Items.Count || found.NextCursor is not null;
            reports.Add(ResultMapper.Report(dto, taken));
        }

        return new CodeInsightsResult
        {
            Reports = reports,
            Annotations = annotations,
            Truncated = truncated,
            Hint = Hint(reports.Count, annotations.Count, truncated),
        };
    }

    /// <summary>One report's annotations, paginated.</summary>
    private static async Task<CodeInsightsResult> OneReportAsync(
        BitbucketApiClient client,
        string workspace,
        string repositorySlug,
        string commit,
        string reportId,
        int? pageSize,
        string? cursor,
        CancellationToken cancellationToken)
    {
        var page = await client
            .ListCodeInsightsAnnotationsAsync(
                workspace, repositorySlug, commit, reportId, pageSize, cursor, cancellationToken)
            .ConfigureAwait(false);

        var annotations = new List<CodeInsightAnnotation>(page.Items.Count);

        foreach (var item in page.Items)
        {
            annotations.Add(ResultMapper.Annotation(item, reportTitle: null));
        }

        return new CodeInsightsResult
        {
            Annotations = annotations,
            NextCursor = page.NextCursor,
        };
    }

    private static string? Hint(int reports, int annotations, bool truncated)
    {
        if (reports == 0)
        {
            return "No tool published a report for this commit. The build's own log is the remaining source: "
                + "listPipelines with commit set, then getPipeline and getPipelineStepLog.";
        }

        if (truncated)
        {
            return "More findings exist than were returned. Raise maxAnnotations, or pass one report's "
                + "reportId to page through it in full.";
        }

        return annotations == 0
            ? "No report failed, so no findings were pulled in. Pass a reportId to read a passing report's "
              + "annotations anyway."
            : null;
    }

    /// <summary>
    /// Resolves <c>mode</c> against <c>pattern</c>.
    /// </summary>
    /// <remarks>
    /// Supplying <c>pattern</c> <em>is</em> the request to search, the same way supplying
    /// <c>paths</c> is the request for a diff in <c>getPullRequestDiff</c>. Naming a contradictory
    /// mode alongside it is refused rather than silently ignored — the caller believes one of the
    /// two things they asked for, and guessing which is how a search quietly becomes a tail.
    /// </remarks>
    private static LogReadMode ResolveLogMode(string? mode, string? pattern)
    {
        var resolved = ToolDefaults.ResolveLogMode(mode);
        var hasPattern = !string.IsNullOrWhiteSpace(pattern);

        if (hasPattern && resolved is not null && resolved != ToolDefaults.LogModeSearch)
        {
            throw new McpException(
                $"mode=\"{resolved}\" contradicts pattern, which searches the whole log. Drop mode to search "
                + "(pattern selects it on its own), or drop pattern to read the "
                + (resolved == ToolDefaults.LogModeHead ? "start" : "end") + " of the log.");
        }

        if (!hasPattern && resolved == ToolDefaults.LogModeSearch)
        {
            throw new McpException(
                "mode=\"search\" needs pattern: the literal text to look for. Supply pattern, or drop mode to "
                + "read the end of the log, which is where a failing step reports why.");
        }

        return resolved switch
        {
            ToolDefaults.LogModeHead => LogReadMode.Head,
            ToolDefaults.LogModeSearch => LogReadMode.Search,
            _ => hasPattern ? LogReadMode.Search : LogReadMode.Tail,
        };
    }
}
