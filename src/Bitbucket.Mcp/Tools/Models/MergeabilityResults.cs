namespace Bitbucket.Mcp.Tools.Models;

/// <summary>Whether a pull request can be merged now, and what stops it.</summary>
internal sealed record PullRequestMergeabilityResult
{
    /// <summary>
    /// True when no check blocks the merge and every check could be evaluated. Not a guarantee:
    /// Bitbucket leaves out conditions it can only find by attempting the merge.
    /// </summary>
    public bool Mergeable { get; init; }

    /// <summary>Every check Bitbucket evaluated, the blocking ones first.</summary>
    public IReadOnlyList<MergeabilityCheck> Checks { get; init; } = [];
}

/// <summary>One check a merge has to pass.</summary>
internal sealed record MergeabilityCheck
{
    /// <summary>
    /// Which check: <c>pullrequest_state_check</c>, <c>current_user_permission_check</c>,
    /// <c>git_mergeability_check</c>, <c>standard_merge_check</c>, <c>custom_pre_merge_check</c>,
    /// <c>custom_on_merge_check</c> or <c>merge_queue_check</c>.
    /// </summary>
    public string? Type { get; init; }

    /// <summary>
    /// A standard check's kind (<c>minimum_approvals</c>, <c>minimum_successful_builds</c>,
    /// <c>resolved_tasks</c>, …) or a custom check's name.
    /// </summary>
    public string? Name { get; init; }

    /// <summary><c>PASSED</c>, <c>FAILED</c>, <c>PENDING</c>, <c>SKIPPED</c> or <c>UNKNOWN</c> (not evaluated).</summary>
    public string? Status { get; init; }

    /// <summary>Whether this check is preventing the merge right now. A failed check need not be.</summary>
    public bool Blocking { get; init; }

    /// <summary>
    /// What the check found: the git outcome, the pull request's state, the required against the
    /// observed value of a standard check, a custom check's message, or whether it is already queued.
    /// </summary>
    public string? Detail { get; init; }
}
