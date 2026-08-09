namespace Bitbucket.Mcp.Tools.Models;

/// <summary>
/// One pull request comment. General when <see cref="Path"/> is absent, anchored to a line of the
/// diff when it is present.
/// </summary>
internal sealed record CommentSummary
{
    /// <summary>The comment id — pass it as <c>parentCommentId</c> to reply to this comment.</summary>
    public long Id { get; init; }

    /// <summary>Who wrote it.</summary>
    public UserSummary? Author { get; init; }

    /// <summary>When it was posted.</summary>
    public DateTimeOffset? CreatedOn { get; init; }

    /// <summary>The comment body, as Markdown source.</summary>
    public string? Content { get; init; }

    /// <summary>The commented file, for an inline comment.</summary>
    public string? Path { get; init; }

    /// <summary>
    /// The commented line: numbered in the file after the change for an added or context line, in
    /// the file before it for a removed line.
    /// </summary>
    public int? Line { get; init; }

    /// <summary>The comment this one replies to, when it is a reply.</summary>
    public long? ParentId { get; init; }

    /// <summary>Whether the thread has been marked resolved.</summary>
    public bool Resolved { get; init; }

    /// <summary>The comment's anchor on bitbucket.org — the link to hand a human.</summary>
    public string? Url { get; init; }
}

/// <summary>One page of a pull request's comments, with deleted ones filtered out.</summary>
internal sealed record CommentListResult
{
    /// <summary>The comments on this page, in Bitbucket's order.</summary>
    public IReadOnlyList<CommentSummary> Comments { get; init; } = [];

    /// <summary>Cursor for the next page, or absent on the last one.</summary>
    public string? NextCursor { get; init; }
}

/// <summary>
/// A comment that was just posted, echoing where it landed.
/// </summary>
/// <remarks>
/// <see cref="LineType"/> and <see cref="MatchedText"/> exist for inline comments placed by
/// <c>codeSnippet</c>: they are the cheapest way for the caller to notice that the snippet matched
/// something other than what it meant.
/// </remarks>
internal sealed record CommentResult
{
    /// <summary>The new comment's id — pass it as <c>parentCommentId</c> to reply to it.</summary>
    public long Id { get; init; }

    /// <summary>Who it was posted as.</summary>
    public UserSummary? Author { get; init; }

    /// <summary>When it was posted.</summary>
    public DateTimeOffset? CreatedOn { get; init; }

    /// <summary>The comment body, as Markdown source.</summary>
    public string? Content { get; init; }

    /// <summary>The commented file, for an inline comment.</summary>
    public string? Path { get; init; }

    /// <summary>The commented line, for an inline comment.</summary>
    public int? Line { get; init; }

    /// <summary><c>ADDED</c>, <c>REMOVED</c> or <c>CONTEXT</c> — which side of the diff the anchor landed on.</summary>
    public string? LineType { get; init; }

    /// <summary>The text of the line the comment was anchored to, for an inline comment.</summary>
    public string? MatchedText { get; init; }

    /// <summary>The comment this one replies to, when it is a reply.</summary>
    public long? ParentId { get; init; }

    /// <summary>The comment's anchor on bitbucket.org — the link to hand a human.</summary>
    public string? Url { get; init; }
}

/// <summary>
/// A comment thread's resolution state after <c>resolvePullRequestComment</c>.
/// </summary>
/// <remarks>
/// Reopening answers <c>204</c> with no body, and resolving an already-resolved thread answers
/// <c>409</c>, so <see cref="Resolved"/> is the state that was asked for and reached — not
/// necessarily one Bitbucket echoed back. <see cref="ResolvedBy"/> and <see cref="ResolvedOn"/> are
/// present only when it did.
/// </remarks>
internal sealed record CommentResolutionResult
{
    /// <summary>The comment whose thread this is.</summary>
    public long CommentId { get; init; }

    /// <summary>Whether the thread is now resolved.</summary>
    public bool Resolved { get; init; }

    /// <summary>Who resolved it, when Bitbucket reported it back.</summary>
    public UserSummary? ResolvedBy { get; init; }

    /// <summary>When it was resolved, when Bitbucket reported it back.</summary>
    public DateTimeOffset? ResolvedOn { get; init; }
}
