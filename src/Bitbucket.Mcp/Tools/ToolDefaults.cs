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

    /// <summary>
    /// Annotations pulled in by one listCodeInsights call when the caller names no cap. Twenty is
    /// about as many findings as are worth reading before fixing some of them.
    /// </summary>
    internal const int DefaultMaxAnnotations = 20;

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
    /// <summary>Log read modes, in the tool's own spelling.</summary>
    internal const string LogModeTail = "tail";

    /// <inheritdoc cref="LogModeTail"/>
    internal const string LogModeHead = "head";

    /// <inheritdoc cref="LogModeTail"/>
    internal const string LogModeSearch = "search";

    private static readonly string[] LogModes = [LogModeTail, LogModeHead, LogModeSearch];

    /// <summary>
    /// Pipeline states as they appear in a <em>response</em>, which is the vocabulary this server
    /// accepts, paired with the vocabulary Bitbucket's <c>status</c> <em>filter</em> uses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two are not the same list, and the difference is not cosmetic: a run whose response says
    /// <c>state.result.name = "SUCCESSFUL"</c> is matched by <c>status=PASSED</c>, and
    /// <c>status=SUCCESSFUL</c> matches nothing at all. Measured against the live API on
    /// 2026-09-09, where the per-status counts summed to the repository's total only once
    /// <c>PASSED</c> was used.
    /// </para>
    /// <para>
    /// Translating here rather than making a caller learn both vocabularies is most of the value of
    /// the parameter — and the allow-list matters just as much, because Bitbucket answers an
    /// unrecognised <c>status</c> with <c>200</c> and an empty page rather than an error, which
    /// reads exactly like "this repository has never run a pipeline".
    /// </para>
    /// </remarks>
    private static readonly (string Result, string Filter)[] PipelineStatuses =
    [
        ("SUCCESSFUL", "PASSED"),
        ("FAILED", "FAILED"),
        ("ERROR", "ERROR"),
        ("STOPPED", "STOPPED"),
        ("EXPIRED", "EXPIRED"),
        ("PENDING", "PENDING"),
        ("PAUSED", "PAUSED"),
        ("HALTED", "HALTED"),
        ("RUNNING", "BUILDING"),
        ("IN_PROGRESS", "BUILDING"),
    ];

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
    /// Trims and drops blanks from a repeated string argument (<c>paths</c>).
    /// </summary>
    /// <returns><see langword="null"/> when nothing usable was supplied.</returns>
    /// <summary>
    /// Trims and drops blanks from a repeated string argument, <b>preserving an explicitly empty
    /// array</b>.
    /// </summary>
    /// <remarks>
    /// The difference from <see cref="CleanList"/> is the whole point: for <c>reviewers</c>, "not
    /// supplied" and "supplied as empty" are different instructions. Omitting the argument leaves
    /// the field out of the body — Bitbucket then applies whatever default-reviewer rule the
    /// repository or its project carries — while <c>[]</c> sends <c>"reviewers": []</c>, which is
    /// the only way to say "nobody". On <c>updatePullRequest</c> the same distinction is what makes
    /// clearing an existing reviewer list expressible at all.
    /// </remarks>
    /// <returns>
    /// <see langword="null"/> only when <paramref name="values"/> is <see langword="null"/>;
    /// otherwise a list, possibly empty.
    /// </returns>
    internal static IReadOnlyList<string>? CleanListPreservingEmpty(string[]? values)
    {
        if (values is null)
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

        return cleaned;
    }

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

    /// <summary>
    /// Validates the <c>pipeline</c> argument, which Bitbucket accepts either as a braced UUID or
    /// as a build number.
    /// </summary>
    /// <remarks>
    /// A bare number and a <c>#</c>-prefixed one both mean the build number, because that is how
    /// Bitbucket's own UI writes it. A bare UUID is braced on the caller's behalf — the endpoint
    /// wants the braces, and a 404 is a poor way to teach that.
    /// </remarks>
    internal static string RequirePipelineId(string? pipeline)
    {
        if (string.IsNullOrWhiteSpace(pipeline))
        {
            throw new McpException(
                "pipeline is required: pass either a build number (for example \"42\") or a pipeline UUID in "
                + "braced form. Both come back from listPipelines, as buildNumber and uuid.");
        }

        var value = pipeline.Trim().TrimStart('#');

        if (value.Length > 0 && value.All(char.IsAsciiDigit))
        {
            return value;
        }

        var braced = Brace(value);

        if (IsBracedUuid(braced))
        {
            return braced;
        }

        throw new McpException(
            $"'{pipeline}' is not a pipeline. Pass a build number (for example \"42\") or a pipeline UUID in "
            + "braced form, both of which listPipelines returns. A branch name is not accepted here — use "
            + "listPipelines with targetBranch to find the run first.");
    }

    /// <summary>Validates a step UUID, adding the braces Bitbucket's path expects.</summary>
    internal static string RequireStepUuid(string? stepUuid)
    {
        if (string.IsNullOrWhiteSpace(stepUuid))
        {
            throw new McpException(
                "stepUuid is required: it is the uuid of one step, from getPipeline's steps list. getPipeline "
                + "also reports failedStepUuid, which is usually the one wanted.");
        }

        var braced = Brace(stepUuid.Trim());

        if (!IsBracedUuid(braced))
        {
            throw new McpException(
                $"'{stepUuid}' is not a step UUID. Step UUIDs are braced, for example "
                + "{01234567-89ab-cdef-0123-456789abcdef}, and come from getPipeline's steps list. A step "
                + "name is not accepted.");
        }

        return braced;
    }

    /// <summary>Validates a commit hash argument.</summary>
    internal static string RequireCommit(string? commit)
    {
        if (string.IsNullOrWhiteSpace(commit))
        {
            throw new McpException(
                "commit is required: a full or abbreviated commit hash. getPullRequest reports one as "
                + "sourceCommit, and getPipeline as commitHash.");
        }

        var value = commit.Trim();

        if (value.Length is < 7 or > 40 || !value.All(char.IsAsciiHexDigit))
        {
            throw new McpException(
                $"'{commit}' is not a commit hash. Pass 7 to 40 hexadecimal characters — a branch name or a "
                + "tag is not accepted here.");
        }

        return value;
    }

    /// <summary>
    /// Translates a pipeline status from the vocabulary responses use into the one the
    /// <c>status</c> filter uses. See <see cref="PipelineStatuses"/> for why they differ.
    /// </summary>
    internal static string? ResolvePipelineStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return null;
        }

        var normalised = status.Trim().ToUpperInvariant();

        foreach (var (result, filter) in PipelineStatuses)
        {
            if (string.Equals(result, normalised, StringComparison.Ordinal)
                || string.Equals(filter, normalised, StringComparison.Ordinal))
            {
                return filter;
            }
        }

        throw new McpException(
            $"'{status}' is not a pipeline status. Use one of SUCCESSFUL, FAILED, ERROR, STOPPED, EXPIRED, "
            + "PENDING, RUNNING, PAUSED or HALTED — the same words listPipelines reports in state. Omit "
            + "status to see every run. (Bitbucket answers an unrecognised status with an empty page rather "
            + "than an error, which is why this is refused here instead.)");
    }

    /// <summary>Resolves the <c>mode</c> argument of the log tool.</summary>
    internal static string? ResolveLogMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            return null;
        }

        var normalised = mode.Trim().ToLowerInvariant();

        if (!LogModes.Contains(normalised, StringComparer.Ordinal))
        {
            throw new McpException(
                $"'{mode}' is not a log mode. Use \"tail\" for the end of the log (the default, and where a "
                + "failing step reports why), \"head\" for the start, or \"search\" with pattern to find "
                + "text anywhere in it.");
        }

        return normalised;
    }

    /// <summary>Clamps the log line budget, falling back to the configured default.</summary>
    internal static int ClampLogLines(int? maxLines, int fallback) =>
        maxLines is null ? fallback : Math.Clamp(maxLines.GetValueOrDefault(), 1, 5_000);

    /// <summary>Clamps the context-line count of a log search.</summary>
    internal static int ClampContextLines(int? contextLines) =>
        contextLines is null ? DefaultContextLines : Math.Clamp(contextLines.GetValueOrDefault(), 0, 50);

    /// <summary>Clamps how many annotations one listCodeInsights call pulls in.</summary>
    internal static int ClampAnnotations(int? maxAnnotations) =>
        maxAnnotations is null ? DefaultMaxAnnotations : Math.Clamp(maxAnnotations.GetValueOrDefault(), 1, 100);

    /// <summary>Wraps a bare UUID in the braces Bitbucket's paths use.</summary>
    private static string Brace(string value) =>
        value.StartsWith('{') && value.EndsWith('}') ? value : $"{{{value}}}";

    /// <summary>Whether a braced string is shaped like a UUID.</summary>
    private static bool IsBracedUuid(string value) =>
        value.Length == 38 && Guid.TryParseExact(value, "B", out _);
}
