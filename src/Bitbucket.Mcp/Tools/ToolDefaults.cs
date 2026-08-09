using System.Globalization;

using Bitbucket.Mcp.Configuration;

using ModelContextProtocol;

namespace Bitbucket.Mcp.Tools;

/// <summary>
/// The limits and shared argument handling every tool applies before it touches the API.
/// </summary>
/// <remarks>
/// Validation lives here rather than in each tool so that sixteen tools cannot drift into sixteen
/// different opinions about what a page size or a missing workspace means. Everything thrown from
/// this class is already an <see cref="McpException"/>: these are the caller's mistakes, and the
/// message is the fix.
/// </remarks>
internal static class ToolDefaults
{
    /// <summary>Smallest page a tool will ask Bitbucket for.</summary>
    internal const int MinPageSize = 1;

    /// <summary>
    /// Largest page a tool will ask Bitbucket for. Well under Bitbucket's own ceiling of 100: the
    /// binding constraint is the model's context, not the API's.
    /// </summary>
    internal const int MaxPageSize = 50;

    /// <summary>Context lines around each hunk when the caller does not say.</summary>
    internal const int DefaultContextLines = 3;

    /// <summary>The environment variable that makes the <c>workspace</c> parameter optional.</summary>
    internal const string DefaultWorkspaceVariable = "BITBUCKET_DEFAULT_WORKSPACE";

    /// <summary>The <c>diffstat</c> mode of <c>getPullRequestDiff</c> — the default.</summary>
    internal const string DiffModeDiffStat = "diffstat";

    /// <summary>The <c>diff</c> mode of <c>getPullRequestDiff</c>.</summary>
    internal const string DiffModeDiff = "diff";

    /// <summary>The ref prefix Bitbucket never stores on a branch name, stripped where one appears.</summary>
    private const string RefsHeadsPrefix = "refs/heads/";

    /// <summary>The states <c>listPullRequests</c> can filter on, plus the <c>ALL</c> escape.</summary>
    private static readonly string[] PullRequestStates =
        ["OPEN", "MERGED", "DECLINED", "SUPERSEDED", "ALL"];

    /// <summary>The two states a pull request task can be in.</summary>
    private static readonly string[] TaskStates = ["RESOLVED", "UNRESOLVED"];

    /// <summary>The merge strategies Bitbucket Cloud accepts.</summary>
    private static readonly string[] MergeStrategies =
        ["merge_commit", "squash", "fast_forward", "squash_fast_forward", "rebase_fast_forward", "rebase_merge"];

    /// <summary>
    /// Resolves the workspace to operate on, falling back to
    /// <c>BITBUCKET_DEFAULT_WORKSPACE</c>.
    /// </summary>
    /// <exception cref="McpException">Neither the argument nor the environment variable is set.</exception>
    internal static string ResolveWorkspace(string? workspace, BitbucketMcpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!string.IsNullOrWhiteSpace(workspace))
        {
            return workspace.Trim();
        }

        if (!string.IsNullOrWhiteSpace(options.DefaultWorkspace))
        {
            return options.DefaultWorkspace;
        }

        throw new McpException(
            "No workspace was given and " + DefaultWorkspaceVariable + " is not set. Pass workspace " +
            "explicitly — it is the first URL segment of bitbucket.org/{workspace}/{repository}, not the " +
            "workspace's display name — or set " + DefaultWorkspaceVariable + " in the environment the MCP " +
            "client launches this server with.");
    }

    /// <summary>Rejects an empty repository slug before it becomes a confusing 404.</summary>
    /// <exception cref="McpException"><paramref name="repository"/> is missing.</exception>
    internal static string RequireRepository(string? repository)
    {
        if (string.IsNullOrWhiteSpace(repository))
        {
            throw new McpException(
                "repository is required: it is the second URL segment of " +
                "bitbucket.org/{workspace}/{repository}, not the repository's display name.");
        }

        return repository.Trim();
    }

    /// <summary>Rejects a pull request number that cannot exist.</summary>
    /// <exception cref="McpException"><paramref name="pullRequestId"/> is not positive.</exception>
    internal static int RequirePullRequestId(int pullRequestId)
    {
        if (pullRequestId <= 0)
        {
            throw new McpException(string.Create(
                CultureInfo.InvariantCulture,
                $"pullRequestId must be 1 or greater; got {pullRequestId}. It is the number in the pull request's URL."));
        }

        return pullRequestId;
    }

    /// <summary>Clamps a requested page size into <see cref="MinPageSize"/>–<see cref="MaxPageSize"/>.</summary>
    /// <returns><see langword="null"/> when the caller did not ask, leaving Bitbucket's own default.</returns>
    internal static int? ClampPageSize(int? pageSize) =>
        pageSize is null ? null : Math.Clamp(pageSize.GetValueOrDefault(), MinPageSize, MaxPageSize);

    /// <summary>
    /// Turns the <c>state</c> parameter into the state list the client expects.
    /// </summary>
    /// <returns>
    /// A single-element list, or <see langword="null"/> for <c>ALL</c> — which means "send no state
    /// filter at all", not "send every state".
    /// </returns>
    /// <exception cref="McpException"><paramref name="state"/> is not one of the documented values.</exception>
    internal static IReadOnlyList<string>? ResolvePullRequestStates(string? state)
    {
        if (string.IsNullOrWhiteSpace(state))
        {
            return ["OPEN"];
        }

        var normalized = state.Trim().ToUpperInvariant();

        if (!PullRequestStates.Contains(normalized, StringComparer.Ordinal))
        {
            throw new McpException(
                $"state must be one of {string.Join(", ", PullRequestStates)}; got '{state}'.");
        }

        return string.Equals(normalized, "ALL", StringComparison.Ordinal) ? null : [normalized];
    }

    /// <summary>
    /// Validates the <c>mode</c> parameter of <c>getPullRequestDiff</c>, keeping "unspecified"
    /// distinguishable from an explicit choice.
    /// </summary>
    /// <returns>
    /// The normalised mode, or <see langword="null"/> when the caller did not name one — which is
    /// what lets <c>paths</c> imply <see cref="DiffModeDiff"/> while an explicit
    /// <see cref="DiffModeDiffStat"/> alongside it stays a conflict rather than a silent override.
    /// </returns>
    /// <exception cref="McpException"><paramref name="mode"/> is neither mode.</exception>
    internal static string? ResolveDiffMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            return null;
        }

        var normalized = mode.Trim().ToLowerInvariant();

        return normalized switch
        {
            DiffModeDiffStat or DiffModeDiff => normalized,
            _ => throw new McpException(
                $"mode must be \"{DiffModeDiffStat}\" (list the changed files) or \"{DiffModeDiff}\" " +
                $"(fetch file contents); got '{mode}'."),
        };
    }

    /// <summary>
    /// Normalises the <c>sourceBranch</c> filter of <c>listPullRequests</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The value becomes a BBQL string literal (<c>source.branch.name = "…"</c>). BBQL delimits
    /// strings with <c>"</c> and <b>documents no escape sequence at all</b> — verified 2026-08-09
    /// against Atlassian's "Filter and sort API objects" page and the published OpenAPI document,
    /// neither of which mentions escaping or backslashes anywhere, and the anonymous endpoints that
    /// would let the parser be probed now answer <c>410</c>. A quote inside the value is therefore
    /// query injection with no documented defence, so it is refused here rather than escaped on a
    /// guess. A backslash goes with it: if BBQL does turn out to escape, a trailing backslash would
    /// swallow the closing quote.
    /// </para>
    /// <para>
    /// (<c>BitbucketApiClient.Quote</c> still escapes both, as the defence that does not depend on
    /// every caller remembering this one.)
    /// </para>
    /// <para>
    /// A leading <c>refs/heads/</c> is stripped rather than refused: Bitbucket stores the short
    /// name, so a fully-qualified ref would match nothing — silently, which is the worst possible
    /// answer to "does this branch already have a pull request?".
    /// </para>
    /// </remarks>
    /// <returns><see langword="null"/> when the caller did not filter by branch.</returns>
    /// <exception cref="McpException">The value cannot be expressed as a BBQL literal.</exception>
    internal static string? ResolveBranchFilter(string? sourceBranch)
    {
        if (string.IsNullOrWhiteSpace(sourceBranch))
        {
            return null;
        }

        var trimmed = sourceBranch.Trim();

        if (trimmed.StartsWith(RefsHeadsPrefix, StringComparison.Ordinal))
        {
            trimmed = trimmed[RefsHeadsPrefix.Length..];
        }

        if (trimmed.Length == 0)
        {
            throw new McpException(
                "sourceBranch is a branch name, not a ref: pass \"feature/clamp\", not " +
                "\"refs/heads/\" on its own. Omit it to list every pull request.");
        }

        if (trimmed.AsSpan().IndexOfAny('"', '\\') >= 0)
        {
            throw new McpException(
                $"sourceBranch cannot contain a double quote or a backslash; got '{trimmed}'. Bitbucket's " +
                "query language delimits strings with double quotes and documents no way to escape one, so " +
                "such a branch cannot be filtered on safely. List the pull requests without sourceBranch and " +
                "match the branch in the results instead.");
        }

        return trimmed;
    }

    /// <summary>Validates the <c>state</c> parameter of <c>updatePullRequestTask</c>.</summary>
    /// <returns><see langword="null"/> when unset, which leaves the task's state alone.</returns>
    /// <exception cref="McpException"><paramref name="state"/> is not one of the two task states.</exception>
    internal static string? ResolveTaskState(string? state)
    {
        if (string.IsNullOrWhiteSpace(state))
        {
            return null;
        }

        var normalized = state.Trim().ToUpperInvariant();

        if (!TaskStates.Contains(normalized, StringComparer.Ordinal))
        {
            throw new McpException(
                $"state must be {string.Join(" or ", TaskStates)}; got '{state}'. RESOLVED ticks the task " +
                "off, UNRESOLVED reopens it.");
        }

        return normalized;
    }

    /// <summary>Validates the <c>mergeStrategy</c> parameter.</summary>
    /// <returns><see langword="null"/> when unset, which leaves the repository's configured default.</returns>
    /// <exception cref="McpException"><paramref name="mergeStrategy"/> is not a Bitbucket strategy.</exception>
    internal static string? ResolveMergeStrategy(string? mergeStrategy)
    {
        if (string.IsNullOrWhiteSpace(mergeStrategy))
        {
            return null;
        }

        var normalized = mergeStrategy.Trim().ToLowerInvariant();

        if (!MergeStrategies.Contains(normalized, StringComparer.Ordinal))
        {
            throw new McpException(
                $"mergeStrategy must be one of {string.Join(", ", MergeStrategies)}; got '{mergeStrategy}'. " +
                "A strategy the repository has disabled is rejected by Bitbucket rather than substituted.");
        }

        return normalized;
    }

    /// <summary>
    /// Trims and drops blanks from a repeated string argument (<c>paths</c>, <c>reviewers</c>).
    /// </summary>
    /// <returns><see langword="null"/> when nothing usable was supplied.</returns>
    internal static IReadOnlyList<string>? CleanList(string[]? values)
    {
        if (values is null || values.Length == 0)
        {
            return null;
        }

        var cleaned = new List<string>(values.Length);

        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                cleaned.Add(value.Trim());
            }
        }

        return cleaned.Count == 0 ? null : cleaned;
    }
}
