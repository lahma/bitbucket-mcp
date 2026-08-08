using System.ComponentModel;

using Bitbucket.Mcp.Configuration;
using Bitbucket.Mcp.Diffs;
using Bitbucket.Mcp.Http;
using Bitbucket.Mcp.Tools.Models;

using ModelContextProtocol.Server;

namespace Bitbucket.Mcp.Tools;

/// <summary>
/// The read half of the pull request surface: list, read, diff, comments.
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
        "(id, title, state, author, branches) — call getPullRequest for the description, reviewers and " +
        "approvals. Defaults to open pull requests only; pass state to widen that. Results are paginated: " +
        "pass the returned nextCursor back as cursor for the next page.")]
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

        var context = new ToolCallContext("listPullRequests", resolvedWorkspace, slug);

        return await ToolErrors.ExecuteAsync(context, async () =>
        {
            var page = await client.ListPullRequestsAsync(
                    resolvedWorkspace,
                    slug,
                    states,
                    author,
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
        "Fetches a pull request's changes. Defaults to mode 'diffstat' listing changed files; call again with " +
        "mode 'diff' and specific paths for content. Full-PR diffs fail on large PRs. In diff mode the " +
        "output is truncated to a line budget and every cut is marked inline — never treat a truncated diff " +
        "as the whole change. Copy a line out of the returned diff verbatim to use as codeSnippet when " +
        "commenting with addPullRequestComment.")]
    public static async Task<PullRequestDiffResult> GetPullRequestDiffAsync(
        BitbucketApiClient client,
        BitbucketMcpOptions options,
        [Description("Repository slug — the second URL segment of bitbucket.org/{workspace}/{repository}, not the repository's display name.")]
        string repository,
        [Description("The pull request number, as it appears in the pull request's URL.")]
        int pullRequestId,
        [Description("Workspace slug — the first URL segment of bitbucket.org/{workspace}/{repository}, not the workspace's display name. Optional when BITBUCKET_DEFAULT_WORKSPACE is set.")]
        string? workspace = null,
        [Description("\"diffstat\" (default) lists the changed files with per-file line counts; \"diff\" returns the unified diff text.")]
        string mode = ToolDefaults.DiffModeDiffStat,
        [Description("Repository-relative paths to fetch, spelled exactly as diffstat reported them. Only used by mode=\"diff\"; omitting it asks for the whole diff, which is what fails on a large pull request.")]
        string[]? paths = null,
        [Description("Unchanged context lines to show around each hunk. Only used by mode=\"diff\". Defaults to 3.")]
        int contextLines = ToolDefaults.DefaultContextLines,
        [Description("Ignore whitespace-only changes. Only used by mode=\"diff\". Defaults to false.")]
        bool ignoreWhitespace = false,
        [Description("Cap on the diff lines returned per file before truncation. Only used by mode=\"diff\". Omit for the server default (BITBUCKET_MCP_MAX_LINES_PER_FILE, 400).")]
        int? maxLinesPerFile = null,
        [Description("Opaque cursor from a previous response's nextCursor — pass it back verbatim. Only used by mode=\"diffstat\", which is the paginated mode.")]
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);

        var slug = ToolDefaults.RequireRepository(repository);
        var resolvedWorkspace = ToolDefaults.ResolveWorkspace(workspace, options);
        var id = ToolDefaults.RequirePullRequestId(pullRequestId);
        var resolvedMode = ToolDefaults.ResolveDiffMode(mode);

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
                    ToolDefaults.CleanList(paths),
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
}
