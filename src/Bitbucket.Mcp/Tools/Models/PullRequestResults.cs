namespace Bitbucket.Mcp.Tools.Models;

/// <summary>
/// A Bitbucket account as a tool result sees it: a name to show and the UUID that identifies it.
/// </summary>
/// <remarks>
/// The UUID is the only identifier Bitbucket accepts back — reviewers are added by UUID, never by
/// name — so it travels with every name rather than being fetched again later.
/// </remarks>
internal sealed record UserSummary
{
    /// <summary>The account's display name, falling back to its nickname.</summary>
    public string? Name { get; init; }

    /// <summary>The account UUID in Bitbucket's braced form; pass this back as a reviewer.</summary>
    public string? Uuid { get; init; }
}

/// <summary>
/// One person's stance on a pull request. Used for both <c>reviewers</c> (people whose review was
/// requested) and <c>participants</c> (everyone who has touched it).
/// </summary>
internal sealed record ParticipantSummary
{
    /// <summary>The person's display name.</summary>
    public string? Name { get; init; }

    /// <summary>The account UUID in Bitbucket's braced form.</summary>
    public string? Uuid { get; init; }

    /// <summary>Whether this person has approved. Absent when they have no stance yet.</summary>
    public bool? Approved { get; init; }

    /// <summary><c>approved</c>, <c>changes_requested</c>, or absent for no stance yet.</summary>
    public string? State { get; init; }
}

/// <summary>A pull request as a list entry: enough to choose one, not enough to read it.</summary>
internal sealed record PullRequestSummary
{
    /// <summary>The pull request number, unique within its repository.</summary>
    public int Id { get; init; }

    /// <summary>The title.</summary>
    public string? Title { get; init; }

    /// <summary><c>OPEN</c>, <c>MERGED</c>, <c>DECLINED</c> or <c>SUPERSEDED</c>.</summary>
    public string? State { get; init; }

    /// <summary>Whether the pull request is still a draft.</summary>
    public bool? Draft { get; init; }

    /// <summary>Who opened it.</summary>
    public UserSummary? Author { get; init; }

    /// <summary>The branch being merged from.</summary>
    public string? SourceBranch { get; init; }

    /// <summary>The branch being merged into.</summary>
    public string? DestinationBranch { get; init; }

    /// <summary>When it was opened.</summary>
    public DateTimeOffset? CreatedOn { get; init; }

    /// <summary>When it last changed.</summary>
    public DateTimeOffset? UpdatedOn { get; init; }

    /// <summary>Number of comments, including inline ones.</summary>
    public int? CommentCount { get; init; }

    /// <summary>Number of open tasks.</summary>
    public int? TaskCount { get; init; }

    /// <summary>
    /// Whether merging will delete the source branch. Carried on the summary as well as the detail
    /// because "which of these will leave their branch behind?" is a question about a list, and
    /// answering it per pull request would be one <c>getPullRequest</c> each.
    /// </summary>
    public bool? CloseSourceBranch { get; init; }

    /// <summary>The pull request's page on bitbucket.org — the link to hand a human.</summary>
    public string? Url { get; init; }
}

/// <summary>One page of pull requests.</summary>
internal sealed record PullRequestListResult
{
    /// <summary>The pull requests on this page, in Bitbucket's order.</summary>
    public IReadOnlyList<PullRequestSummary> PullRequests { get; init; } = [];

    /// <summary>
    /// Cursor for the next page, or absent on the last one. Pass it back verbatim as
    /// <c>cursor</c>.
    /// </summary>
    public string? NextCursor { get; init; }

    /// <summary>Total matching pull requests, when Bitbucket reported a count.</summary>
    public int? TotalSize { get; init; }
}

/// <summary>One pull request in full, including who has reviewed it and how.</summary>
internal sealed record PullRequestDetail
{
    /// <summary>The pull request number.</summary>
    public int Id { get; init; }

    /// <summary>The title.</summary>
    public string? Title { get; init; }

    /// <summary><c>OPEN</c>, <c>MERGED</c>, <c>DECLINED</c> or <c>SUPERSEDED</c>.</summary>
    public string? State { get; init; }

    /// <summary>Whether the pull request is still a draft.</summary>
    public bool? Draft { get; init; }

    /// <summary>The description, in Bitbucket-flavoured Markdown.</summary>
    public string? Description { get; init; }

    /// <summary>Who opened it.</summary>
    public UserSummary? Author { get; init; }

    /// <summary>The branch being merged from.</summary>
    public string? SourceBranch { get; init; }

    /// <summary>The branch being merged into.</summary>
    public string? DestinationBranch { get; init; }

    /// <summary>When it was opened.</summary>
    public DateTimeOffset? CreatedOn { get; init; }

    /// <summary>When it last changed.</summary>
    public DateTimeOffset? UpdatedOn { get; init; }

    /// <summary>Number of comments, including inline ones.</summary>
    public int? CommentCount { get; init; }

    /// <summary>Number of open tasks.</summary>
    public int? TaskCount { get; init; }

    /// <summary>Whether merging will delete the source branch.</summary>
    public bool? CloseSourceBranch { get; init; }

    /// <summary>The decline or supersede reason, when there is one.</summary>
    public string? Reason { get; init; }

    /// <summary>The merge commit hash, once merged.</summary>
    public string? MergeCommitHash { get; init; }

    /// <summary>Who merged or declined it.</summary>
    public UserSummary? ClosedBy { get; init; }

    /// <summary>
    /// The people whose review was requested, with their current stance folded in from the
    /// participant list when they have expressed one.
    /// </summary>
    public IReadOnlyList<ParticipantSummary>? Reviewers { get; init; }

    /// <summary>Everyone involved and their stance — the only place approvals are visible.</summary>
    public IReadOnlyList<ParticipantSummary>? Participants { get; init; }

    /// <summary>The pull request's page on bitbucket.org — the link to hand a human.</summary>
    public string? Url { get; init; }
}
