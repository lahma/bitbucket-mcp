using Bitbucket.Mcp.Http.Models;

namespace Bitbucket.Mcp.Http;

/// <summary>
/// What a merge came to within the time <see cref="BitbucketApiClient.MergeAsync"/> waits for it:
/// either the merged pull request, or the task Bitbucket is still working on.
/// </summary>
internal sealed record MergeOutcome
{
    /// <summary>The merged pull request; <see langword="null"/> while the merge has not landed.</summary>
    public PullRequestDto? PullRequest { get; init; }

    /// <summary>
    /// The task Bitbucket is merging under, while the merge has not landed. <see langword="null"/>
    /// both after a landed merge and when Bitbucket accepted the merge without naming a task this
    /// client is willing to follow.
    /// </summary>
    public string? TaskId { get; init; }

    /// <summary>Whether the pull request is waiting in the destination branch's merge queue.</summary>
    public bool InMergeQueue { get; init; }
}

/// <summary>One unanswered look at a merge task, reported while the client keeps waiting.</summary>
/// <param name="Attempt">The look just taken, from 1.</param>
/// <param name="Attempts">How many looks the client takes before it returns the task instead.</param>
/// <param name="InMergeQueue">Whether the pull request is waiting in a merge queue.</param>
internal readonly record struct MergeWait(int Attempt, int Attempts, bool InMergeQueue);
