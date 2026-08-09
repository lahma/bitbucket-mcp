namespace Bitbucket.Mcp.Tools.Models;

/// <summary>
/// One of a repository's default reviewers, and where the rule that names them lives.
/// </summary>
/// <remarks>
/// The point of this shape is <see cref="UserSummary.Uuid"/>: a reviewer can only be requested by
/// UUID, and before this tool the only place to read one was an existing pull request — which a
/// repository that has never had one does not have.
/// </remarks>
internal sealed record DefaultReviewerSummary
{
    /// <summary>The person's display name, falling back to their nickname.</summary>
    public string? Name { get; init; }

    /// <summary>The account UUID in Bitbucket's braced form; pass this back as a reviewer.</summary>
    public string? Uuid { get; init; }

    /// <summary>
    /// <c>repository</c> for a reviewer configured on this repository, <c>project</c> for one
    /// inherited from the project it belongs to.
    /// </summary>
    public string? ReviewerType { get; init; }
}

/// <summary>One page of a repository's effective default reviewers.</summary>
internal sealed record DefaultReviewerListResult
{
    /// <summary>The default reviewers on this page, in Bitbucket's order.</summary>
    public IReadOnlyList<DefaultReviewerSummary> Reviewers { get; init; } = [];

    /// <summary>Cursor for the next page, or absent on the last one.</summary>
    public string? NextCursor { get; init; }

    /// <summary>Total default reviewers, when Bitbucket reported a count.</summary>
    public int? TotalSize { get; init; }
}
