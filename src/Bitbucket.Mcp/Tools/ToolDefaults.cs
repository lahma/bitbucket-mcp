using System.Globalization;

using Bitbucket.Mcp.Configuration;

using ModelContextProtocol;

namespace Bitbucket.Mcp.Tools;

/// <summary>
/// The limits and shared argument handling every tool applies before it touches the API.
/// </summary>
/// <remarks>
/// Validation lives here rather than in each tool so that ten tools cannot drift into ten
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

    /// <summary>The states <c>listPullRequests</c> can filter on, plus the <c>ALL</c> escape.</summary>
    private static readonly string[] PullRequestStates =
        ["OPEN", "MERGED", "DECLINED", "SUPERSEDED", "ALL"];

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

    /// <summary>Validates the <c>mode</c> parameter of <c>getPullRequestDiff</c>.</summary>
    /// <exception cref="McpException"><paramref name="mode"/> is neither mode.</exception>
    internal static string ResolveDiffMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            return DiffModeDiffStat;
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
