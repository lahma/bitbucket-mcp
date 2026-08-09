using System.ComponentModel;

using Bitbucket.Mcp.Configuration;
using Bitbucket.Mcp.Diffs;
using Bitbucket.Mcp.Http;
using Bitbucket.Mcp.Tools.Models;

using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Bitbucket.Mcp.Tools;

/// <summary>
/// The read half of the pull request surface: list, read, diff, comments, default reviewers, build
/// statuses and tasks.
/// </summary>
/// <remarks>
/// <para>
/// Every method is static (D8): <c>BitbucketApiClient</c> and <c>BitbucketMcpOptions</c> arrive as
/// plain parameters that the SDK binds from DI — it excludes anything
/// <c>IServiceProviderIsService</c> recognises, along with the <see cref="CancellationToken"/>,
/// from the generated schema. That avoids activating an instance per call and leaves each method
/// directly unit-testable with a stub client.
/// </para>
/// <para>
/// The class itself is sealed rather than <c>static</c> for one reason: C# forbids a static class
/// as a type argument (CS0718), and the registration is
/// <c>WithTools&lt;PullRequestReadTools&gt;(jsonOptions)</c> — never
/// <c>WithToolsFromAssembly()</c>, which is not AOT-safe (IL2026). The private constructor keeps
/// the type uninstantiable anyway.
/// </para>
/// <para>
/// Nothing here throws anything but <see cref="ModelContextProtocol.McpException"/>: every body
/// runs inside <see cref="ToolErrors.ExecuteAsync"/>.
/// </para>
/// </remarks>
[McpServerToolType]
internal sealed class PullRequestReadTools
{
    private PullRequestReadTools()
    {
    }

    [McpServerTool(
        Name = "listPullRequests",
        Title = "List pull requests",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = true,
        UseStructuredContent = true)]
    [Description(
        "Lists a repository's pull requests, most recently updated first. Returns a summary per pull request " +
        "(id, title, state, author, branches, url) — call getPullRequest for the description, reviewers and " +
        "approvals. Defaults to open pull requests only; pass state to widen that. Pass sourceBranch to ask " +
        "whether a branch already has a pull request, which is the check to make before createPullRequest. " +
        "Results are paginated: pass the returned nextCursor back as cursor for the next page.")]
    public static async Task<PullRequestListResult> ListPullRequestsAsync(
        BitbucketApiClient client,
        BitbucketMcpOptions options,
        [Description("Repository slug — the second URL segment of bitbucket.org/{workspace}/{repository}, not the repository's display name.")]
        string repository,
        [Description("Workspace slug — the first URL segment of bitbucket.org/{workspace}/{repository}, not the workspace's display name. Optional when BITBUCKET_DEFAULT_WORKSPACE is set.")]
        string? workspace = null,
        [Description("Which pull requests to include: OPEN, MERGED, DECLINED, SUPERSEDED or ALL (no state filter). Defaults to OPEN.")]
        string state = "OPEN",
        [Description("Restrict to one author: a Bitbucket account UUID in braced form ({...}), or a nickname. Omit for all authors.")]
        string? author = null,
        [Description("Restrict to pull requests opened from this branch, without any refs/heads/ prefix and spelled exactly. Combined with state, so pass state=\"ALL\" to find a merged or declined one too.")]
        string? sourceBranch = null,
        [Description("Pull requests per page, clamped to 1-50. Omit for Bitbucket's default.")]
        int? pageSize = null,
        [Description("Opaque cursor from a previous response's nextCursor — pass it back verbatim. Omit to start from the first page. When set, every other filter is ignored because the cursor already encodes them.")]
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        var slug = ToolDefaults.RequireRepository(repository);
        var resolvedWorkspace = ToolDefaults.ResolveWorkspace(workspace, options);
        var states = ToolDefaults.ResolvePullRequestStates(state);
        var branch = ToolDefaults.ResolveBranchFilter(sourceBranch);

        var context = new ToolCallContext("listPullRequests", resolvedWorkspace, slug);

        return await ToolErrors.ExecuteAsync(context, async () =>
        {
            var page = await client.ListPullRequestsAsync(
                    resolvedWorkspace,
                    slug,
                    states,
                    author,
                    branch,
                    query: null,
                    sort: "-updated_on",
                    ToolDefaults.ClampPageSize(pageSize),
                    cursor,
                    cancellationToken)
                .ConfigureAwait(false);

            return ResultMapper.List(page);
        }).ConfigureAwait(false);
    }

    [McpServerTool(
        Name = "getPullRequest",
        Title = "Get pull request",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = true,
        UseStructuredContent = true)]
    [Description(
        "Reads one pull request in full: title, description, state, branches, and the reviewers and " +
        "participants with their approvals. This is where reviewer UUIDs come from — the values " +
        "createPullRequest and updatePullRequest expect. Does not include the diff or the comments; use " +
        "getPullRequestDiff and getPullRequestComments for those.")]
    public static async Task<PullRequestDetail> GetPullRequestAsync(
        BitbucketApiClient client,
        BitbucketMcpOptions options,
        [Description("Repository slug — the second URL segment of bitbucket.org/{workspace}/{repository}, not the repository's display name.")]
        string repository,
        [Description("The pull request number, as it appears in the pull request's URL.")]
        int pullRequestId,
        [Description("Workspace slug — the first URL segment of bitbucket.org/{workspace}/{repository}, not the workspace's display name. Optional when BITBUCKET_DEFAULT_WORKSPACE is set.")]
        string? workspace = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        var slug = ToolDefaults.RequireRepository(repository);
        var resolvedWorkspace = ToolDefaults.ResolveWorkspace(workspace, options);
        var id = ToolDefaults.RequirePullRequestId(pullRequestId);

        var context = new ToolCallContext("getPullRequest", resolvedWorkspace, slug, id);

        return await ToolErrors.ExecuteAsync(context, async () =>
        {
            var dto = await client.GetPullRequestAsync(resolvedWorkspace, slug, id, cancellationToken)
                .ConfigureAwait(false);

            return ResultMapper.Detail(dto);
        }).ConfigureAwait(false);
    }

    [McpServerTool(
        Name = "getPullRequestDiff",
        Title = "Get pull request diff",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = true,
        UseStructuredContent = true)]
    [Description(
        "Fetches a pull request's changes. With no mode and no paths it lists the changed files " +
        "(mode=\"diffstat\"); passing paths switches it to the unified diff for exactly those files, so the " +
        "two-step workflow is diffstat first, then paths. Asking for a whole pull request's diff fails on " +
        "large ones. In diff mode the output is truncated to a line budget and every cut is marked inline — " +
        "never treat a truncated diff as the whole change. Copy a line out of the returned diff verbatim to " +
        "use as codeSnippet when commenting with addPullRequestComment.")]
    public static async Task<PullRequestDiffResult> GetPullRequestDiffAsync(
        BitbucketApiClient client,
        BitbucketMcpOptions options,
        [Description("Repository slug — the second URL segment of bitbucket.org/{workspace}/{repository}, not the repository's display name.")]
        string repository,
        [Description("The pull request number, as it appears in the pull request's URL.")]
        int pullRequestId,
        [Description("Workspace slug — the first URL segment of bitbucket.org/{workspace}/{repository}, not the workspace's display name. Optional when BITBUCKET_DEFAULT_WORKSPACE is set.")]
        string? workspace = null,
        [Description("\"diffstat\" lists the changed files with per-file line counts; \"diff\" returns the unified diff text. Omit it: without paths that means diffstat, and with paths it means diff. Setting \"diffstat\" together with paths is rejected rather than silently ignored.")]
        string? mode = null,
        [Description("Repository-relative paths to fetch, spelled exactly as diffstat reported them. Supplying them selects mode=\"diff\" on its own. Omitting them in diff mode asks for the whole diff, which is what fails on a large pull request.")]
        string[]? paths = null,
        [Description("Unchanged context lines to show around each hunk. Only used by mode=\"diff\". Defaults to 3.")]
        int contextLines = ToolDefaults.DefaultContextLines,
        [Description("Ignore whitespace-only changes. Only used by mode=\"diff\". Defaults to false.")]
        bool ignoreWhitespace = false,
        [Description("Cap on the diff lines returned per file before truncation. Only meaningful in mode=\"diff\". Omit for the server default (BITBUCKET_MCP_MAX_LINES_PER_FILE, 400).")]
        int? maxLinesPerFile = null,
        [Description("Opaque cursor from a previous response's nextCursor — pass it back verbatim. Only meaningful in mode=\"diffstat\", which is the paginated mode.")]
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);

        var slug = ToolDefaults.RequireRepository(repository);
        var resolvedWorkspace = ToolDefaults.ResolveWorkspace(workspace, options);
        var id = ToolDefaults.RequirePullRequestId(pullRequestId);

        var cleanedPaths = ToolDefaults.CleanList(paths);
        var resolvedMode = ResolveDiffMode(ToolDefaults.ResolveDiffMode(mode), cleanedPaths, maxLinesPerFile, cursor);

        var context = new ToolCallContext("getPullRequestDiff", resolvedWorkspace, slug, id);

        return await ToolErrors.ExecuteAsync(context, async () =>
        {
            if (string.Equals(resolvedMode, ToolDefaults.DiffModeDiffStat, StringComparison.Ordinal))
            {
                var page = await client.GetDiffStatAsync(
                        resolvedWorkspace,
                        slug,
                        id,
                        pageSize: null,
                        cursor,
                        cancellationToken)
                    .ConfigureAwait(false);

                return new PullRequestDiffResult
                {
                    Mode = ToolDefaults.DiffModeDiffStat,
                    Diffstat = ResultMapper.DiffStat(page),
                };
            }

            var raw = await client.GetDiffAsync(
                    resolvedWorkspace,
                    slug,
                    id,
                    cleanedPaths,
                    contextLines,
                    ignoreWhitespace,
                    cancellationToken)
                .ConfigureAwait(false);

            var files = UnifiedDiffParser.Split(raw);

            var truncated = DiffTruncator.Truncate(
                files,
                maxLinesPerFile ?? options.MaxLinesPerFile,
                options.MaxDiffLines);

            return new PullRequestDiffResult
            {
                Mode = ToolDefaults.DiffModeDiff,
                Diff = ResultMapper.Diff(truncated),
            };
        }).ConfigureAwait(false);
    }

    [McpServerTool(
        Name = "getPullRequestComments",
        Title = "Get pull request comments",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = true,
        UseStructuredContent = true)]
    [Description(
        "Lists a pull request's comments — both general ones and the inline ones anchored to a file and line " +
        "— oldest first, with deleted comments filtered out. Each comment's id is what addPullRequestComment " +
        "takes as parentCommentId to reply. Results are paginated: pass the returned nextCursor back as " +
        "cursor for the next page.")]
    public static async Task<CommentListResult> GetPullRequestCommentsAsync(
        BitbucketApiClient client,
        BitbucketMcpOptions options,
        [Description("Repository slug — the second URL segment of bitbucket.org/{workspace}/{repository}, not the repository's display name.")]
        string repository,
        [Description("The pull request number, as it appears in the pull request's URL.")]
        int pullRequestId,
        [Description("Workspace slug — the first URL segment of bitbucket.org/{workspace}/{repository}, not the workspace's display name. Optional when BITBUCKET_DEFAULT_WORKSPACE is set.")]
        string? workspace = null,
        [Description("Comments per page, clamped to 1-50. Omit for Bitbucket's default.")]
        int? pageSize = null,
        [Description("Opaque cursor from a previous response's nextCursor — pass it back verbatim. Omit to start from the first page.")]
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        var slug = ToolDefaults.RequireRepository(repository);
        var resolvedWorkspace = ToolDefaults.ResolveWorkspace(workspace, options);
        var id = ToolDefaults.RequirePullRequestId(pullRequestId);

        var context = new ToolCallContext("getPullRequestComments", resolvedWorkspace, slug, id);

        return await ToolErrors.ExecuteAsync(context, async () =>
        {
            var page = await client.GetCommentsAsync(
                    resolvedWorkspace,
                    slug,
                    id,
                    ToolDefaults.ClampPageSize(pageSize),
                    cursor,
                    cancellationToken)
                .ConfigureAwait(false);

            return ResultMapper.Comments(page);
        }).ConfigureAwait(false);
    }

    [McpServerTool(
        Name = "listDefaultReviewers",
        Title = "List default reviewers",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = true,
        UseStructuredContent = true)]
    [Description(
        "Lists the people Bitbucket adds as reviewers on a new pull request in this repository, with their " +
        "account UUIDs. This is where reviewer UUIDs come from when there is no pull request to read them off " +
        "— createPullRequest and updatePullRequest accept UUIDs and nothing else. Covers both the " +
        "repository's own default reviewers and the ones it inherits from its project (reviewerType says " +
        "which). Results are paginated: pass the returned nextCursor back as cursor for the next page.")]
    public static async Task<DefaultReviewerListResult> ListDefaultReviewersAsync(
        BitbucketApiClient client,
        BitbucketMcpOptions options,
        [Description("Repository slug — the second URL segment of bitbucket.org/{workspace}/{repository}, not the repository's display name.")]
        string repository,
        [Description("Workspace slug — the first URL segment of bitbucket.org/{workspace}/{repository}, not the workspace's display name. Optional when BITBUCKET_DEFAULT_WORKSPACE is set.")]
        string? workspace = null,
        [Description("Reviewers per page, clamped to 1-50. Omit for Bitbucket's default.")]
        int? pageSize = null,
        [Description("Opaque cursor from a previous response's nextCursor — pass it back verbatim. Omit to start from the first page.")]
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        var slug = ToolDefaults.RequireRepository(repository);
        var resolvedWorkspace = ToolDefaults.ResolveWorkspace(workspace, options);

        var context = new ToolCallContext("listDefaultReviewers", resolvedWorkspace, slug);

        return await ToolErrors.ExecuteAsync(context, async () =>
        {
            var page = await client.ListDefaultReviewersAsync(
                    resolvedWorkspace,
                    slug,
                    ToolDefaults.ClampPageSize(pageSize),
                    cursor,
                    cancellationToken)
                .ConfigureAwait(false);

            return ResultMapper.DefaultReviewers(page);
        }).ConfigureAwait(false);
    }

    [McpServerTool(
        Name = "listPullRequestStatuses",
        Title = "List pull request statuses",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = true,
        UseStructuredContent = true)]
    [Description(
        "Lists the build statuses reported against a pull request — CI results, deployments, external checks. " +
        "Call this before mergePullRequest: a state other than SUCCESSFUL is a reason not to merge, and " +
        "Bitbucket will happily merge over a failing build if the repository does not require it. An empty " +
        "list means nothing has reported yet, which is not the same as passing. Results are paginated: pass " +
        "the returned nextCursor back as cursor for the next page.")]
    public static async Task<PullRequestStatusListResult> ListPullRequestStatusesAsync(
        BitbucketApiClient client,
        BitbucketMcpOptions options,
        [Description("Repository slug — the second URL segment of bitbucket.org/{workspace}/{repository}, not the repository's display name.")]
        string repository,
        [Description("The pull request number, as it appears in the pull request's URL.")]
        int pullRequestId,
        [Description("Workspace slug — the first URL segment of bitbucket.org/{workspace}/{repository}, not the workspace's display name. Optional when BITBUCKET_DEFAULT_WORKSPACE is set.")]
        string? workspace = null,
        [Description("Statuses per page, clamped to 1-50. Omit for Bitbucket's default.")]
        int? pageSize = null,
        [Description("Opaque cursor from a previous response's nextCursor — pass it back verbatim. Omit to start from the first page.")]
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        var slug = ToolDefaults.RequireRepository(repository);
        var resolvedWorkspace = ToolDefaults.ResolveWorkspace(workspace, options);
        var id = ToolDefaults.RequirePullRequestId(pullRequestId);

        var context = new ToolCallContext("listPullRequestStatuses", resolvedWorkspace, slug, id);

        return await ToolErrors.ExecuteAsync(context, async () =>
        {
            var page = await client.ListStatusesAsync(
                    resolvedWorkspace,
                    slug,
                    id,
                    ToolDefaults.ClampPageSize(pageSize),
                    cursor,
                    cancellationToken)
                .ConfigureAwait(false);

            return ResultMapper.Statuses(page);
        }).ConfigureAwait(false);
    }

    [McpServerTool(
        Name = "listPullRequestTasks",
        Title = "List pull request tasks",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = true,
        UseStructuredContent = true)]
    [Description(
        "Lists a pull request's tasks — the tracked checklist a reviewer leaves behind, each RESOLVED or " +
        "UNRESOLVED. Unresolved tasks are the outstanding work on a pull request, so this answers \"what is " +
        "still open?\" in a way the comments do not. Each task's id is what updatePullRequestTask takes as " +
        "taskId. Results are paginated: pass the returned nextCursor back as cursor for the next page.")]
    public static async Task<PullRequestTaskListResult> ListPullRequestTasksAsync(
        BitbucketApiClient client,
        BitbucketMcpOptions options,
        [Description("Repository slug — the second URL segment of bitbucket.org/{workspace}/{repository}, not the repository's display name.")]
        string repository,
        [Description("The pull request number, as it appears in the pull request's URL.")]
        int pullRequestId,
        [Description("Workspace slug — the first URL segment of bitbucket.org/{workspace}/{repository}, not the workspace's display name. Optional when BITBUCKET_DEFAULT_WORKSPACE is set.")]
        string? workspace = null,
        [Description("Tasks per page, clamped to 1-50. Omit for Bitbucket's default.")]
        int? pageSize = null,
        [Description("Opaque cursor from a previous response's nextCursor — pass it back verbatim. Omit to start from the first page.")]
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        var slug = ToolDefaults.RequireRepository(repository);
        var resolvedWorkspace = ToolDefaults.ResolveWorkspace(workspace, options);
        var id = ToolDefaults.RequirePullRequestId(pullRequestId);

        var context = new ToolCallContext("listPullRequestTasks", resolvedWorkspace, slug, id);

        return await ToolErrors.ExecuteAsync(context, async () =>
        {
            var page = await client.ListTasksAsync(
                    resolvedWorkspace,
                    slug,
                    id,
                    ToolDefaults.ClampPageSize(pageSize),
                    cursor,
                    cancellationToken)
                .ConfigureAwait(false);

            return ResultMapper.Tasks(page);
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Decides which of <c>getPullRequestDiff</c>'s two modes the arguments describe.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>paths</c> names files to read, and only the diff mode reads files — so supplying it
    /// <em>is</em> the request for a diff. Before this, a model that asked for
    /// <c>paths=["src/Widget.cs"]</c> without naming a mode got the file list back instead, with
    /// nothing saying why, and typically concluded the file was missing.
    /// </para>
    /// <para>
    /// An explicit mode is never overridden: <c>mode="diffstat"</c> with <c>paths</c> is two
    /// incompatible requests in one call, so it is refused with the two ways out rather than
    /// silently resolved in either direction.
    /// </para>
    /// </remarks>
    /// <exception cref="McpException">The arguments name both modes at once.</exception>
    private static string ResolveDiffMode(
        string? explicitMode,
        IReadOnlyList<string>? paths,
        int? maxLinesPerFile,
        string? cursor)
    {
        var diffOnly = new List<string>(2);

        if (paths is not null)
        {
            diffOnly.Add("paths");
        }

        if (maxLinesPerFile is not null)
        {
            diffOnly.Add("maxLinesPerFile");
        }

        var wantsDiffStat = string.Equals(explicitMode, ToolDefaults.DiffModeDiffStat, StringComparison.Ordinal);

        if (wantsDiffStat && diffOnly.Count > 0)
        {
            throw new McpException(
                $"mode=\"{ToolDefaults.DiffModeDiffStat}\" conflicts with {string.Join(" and ", diffOnly)}: " +
                $"{string.Join(" and ", diffOnly)} only means something when fetching diff content, and " +
                $"\"{ToolDefaults.DiffModeDiffStat}\" only lists the changed files. Drop mode to fetch those " +
                $"files' diffs, or drop {string.Join(" and ", diffOnly)} to list the changed files.");
        }

        // paths alone is enough: it is the file selection, and only the diff mode has files to
        // select. maxLinesPerFile is not, because a caller may set it once and reuse the call.
        var resolved = wantsDiffStat || (explicitMode is null && paths is null)
            ? ToolDefaults.DiffModeDiffStat
            : ToolDefaults.DiffModeDiff;

        if (string.Equals(resolved, ToolDefaults.DiffModeDiff, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(cursor))
        {
            throw new McpException(
                $"cursor conflicts with mode=\"{ToolDefaults.DiffModeDiff}\": only the changed-file list is " +
                "paginated, so a cursor can only continue a " +
                $"mode=\"{ToolDefaults.DiffModeDiffStat}\" call. Drop cursor to fetch the diff of the files " +
                $"you named, or drop paths to continue listing the changed files.");
        }

        return resolved;
    }
}
