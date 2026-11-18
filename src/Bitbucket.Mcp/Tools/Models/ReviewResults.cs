namespace Bitbucket.Mcp.Tools.Models;

/// <summary>The authenticated user's review stance after <c>setPullRequestReviewStatus</c>.</summary>
internal sealed record ReviewStatusResult
{
    /// <summary><c>APPROVED</c>, <c>CHANGES_REQUESTED</c> or <c>UNAPPROVED</c>.</summary>
    public string? Status { get; init; }

    /// <summary>Whether the pull request now counts as approved by this user.</summary>
    public bool Approved { get; init; }

    /// <summary>The user whose stance changed, when Bitbucket reported it back.</summary>
    public UserSummary? User { get; init; }

    /// <summary>The id of the comment posted alongside the change, when one was requested.</summary>
    public long? CommentId { get; init; }
}

/// <summary>The outcome of a merge.</summary>
internal sealed record MergeResult
{
    /// <summary>
    /// <c>MERGED</c> once the merge has landed. <c>QUEUED</c> while Bitbucket is still merging it
    /// in the background or holding it in the destination branch's merge queue — the merge is under
    /// way, and calling mergePullRequest again is not how to wait for it.
    /// </summary>
    public string? State { get; init; }

    /// <summary>The merge commit hash, when Bitbucket reported one.</summary>
    public string? MergeCommitHash { get; init; }

    /// <summary>The merge task Bitbucket is working on, while the state is <c>QUEUED</c>.</summary>
    public string? TaskId { get; init; }

    /// <summary>Whether the pull request is waiting in its destination branch's merge queue.</summary>
    public bool? InMergeQueue { get; init; }

    /// <summary>How to find out when a <c>QUEUED</c> merge lands; absent once it has.</summary>
    public string? Next { get; init; }
}

/// <summary>The outcome of a decline.</summary>
internal sealed record DeclineResult
{
    /// <summary>The pull request's state after declining — <c>DECLINED</c> on success.</summary>
    public string? State { get; init; }

    /// <summary>
    /// The reason as Bitbucket stored it. Bitbucket documents no request body for this endpoint,
    /// so a supplied reason may come back absent.
    /// </summary>
    public string? Reason { get; init; }
}
