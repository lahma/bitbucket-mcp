namespace Bitbucket.Mcp.Tools.Models;

/// <summary>
/// One pull request task: a tracked "this has to happen before merge", either free-standing or
/// hanging off a comment.
/// </summary>
/// <remarks>
/// The same record is what <c>addPullRequestTask</c> and <c>updatePullRequestTask</c> answer with,
/// so a caller reads one shape whether it listed a task or just changed one.
/// </remarks>
internal sealed record PullRequestTask
{
    /// <summary>The task id — pass it as <c>taskId</c> to updatePullRequestTask.</summary>
    public long Id { get; init; }

    /// <summary><c>UNRESOLVED</c> or <c>RESOLVED</c>.</summary>
    public string? State { get; init; }

    /// <summary>The task text, as Markdown source.</summary>
    public string? Content { get; init; }

    /// <summary>Who created it.</summary>
    public UserSummary? Creator { get; init; }

    /// <summary>Who resolved it, once someone has.</summary>
    public UserSummary? ResolvedBy { get; init; }

    /// <summary>
    /// The comment the task hangs off, when it was created against one. Read the comment itself
    /// with getPullRequestComments.
    /// </summary>
    public long? CommentId { get; init; }

    /// <summary>When the task was created.</summary>
    public DateTimeOffset? CreatedOn { get; init; }

    /// <summary>When it last changed.</summary>
    public DateTimeOffset? UpdatedOn { get; init; }
}

/// <summary>One page of a pull request's tasks.</summary>
internal sealed record PullRequestTaskListResult
{
    /// <summary>The tasks on this page, in Bitbucket's order.</summary>
    public IReadOnlyList<PullRequestTask> Tasks { get; init; } = [];

    /// <summary>Cursor for the next page, or absent on the last one.</summary>
    public string? NextCursor { get; init; }

    /// <summary>Total tasks, when Bitbucket reported a count.</summary>
    public int? TotalSize { get; init; }
}
