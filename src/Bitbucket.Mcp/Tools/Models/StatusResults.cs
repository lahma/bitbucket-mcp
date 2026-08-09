namespace Bitbucket.Mcp.Tools.Models;

/// <summary>One build status reported against a pull request's commits.</summary>
internal sealed record PullRequestStatusSummary
{
    /// <summary>
    /// <c>SUCCESSFUL</c>, <c>FAILED</c>, <c>INPROGRESS</c> or <c>STOPPED</c>. Anything but
    /// <c>SUCCESSFUL</c> is a reason not to merge yet.
    /// </summary>
    public string? State { get; init; }

    /// <summary>The status's identifier, unique per provider — for example <c>BB-DEPLOY</c>.</summary>
    public string? Key { get; init; }

    /// <summary>The build's name, for example <c>BB-DEPLOY-1</c>.</summary>
    public string? Name { get; init; }

    /// <summary>Where a human reads the build output.</summary>
    public string? Url { get; init; }

    /// <summary>What the provider had to say about this run.</summary>
    public string? Description { get; init; }

    /// <summary>The ref the status was reported against, when the provider named one.</summary>
    public string? Refname { get; init; }

    /// <summary>When the status last changed.</summary>
    public DateTimeOffset? UpdatedOn { get; init; }
}

/// <summary>
/// One page of a pull request's build statuses — the merge-readiness check.
/// </summary>
/// <remarks>
/// An empty list means no provider has reported anything, which is not the same as "the checks
/// passed": a pipeline that has not started yet also reports nothing.
/// </remarks>
internal sealed record PullRequestStatusListResult
{
    /// <summary>The statuses on this page, in Bitbucket's order.</summary>
    public IReadOnlyList<PullRequestStatusSummary> Statuses { get; init; } = [];

    /// <summary>Cursor for the next page, or absent on the last one.</summary>
    public string? NextCursor { get; init; }

    /// <summary>Total statuses, when Bitbucket reported a count.</summary>
    public int? TotalSize { get; init; }
}
