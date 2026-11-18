using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bitbucket.Mcp.Http.Models;

/// <summary>
/// The state of a merge Bitbucket is performing in the background, read from
/// <c>…/pullrequests/{id}/merge/task-status/{task_id}</c>.
/// </summary>
/// <remarks>
/// A merge becomes a task in two cases, and the <c>202</c> that starts one looks the same in both:
/// a synchronous merge that ran past Bitbucket's timeout, and any merge into a branch with an active
/// merge queue — which answers <c>202</c> even when <c>async</c> is false. A failed task answers with
/// an error envelope instead of this body (a <c>400</c>, whose message may be Markdown), so this type
/// only ever describes a task that is still running or has succeeded.
/// </remarks>
internal sealed record MergeTaskStatusDto
{
    /// <summary>
    /// <c>PENDING</c> while the merge is running or queued, <c>SUCCESS</c> once it has landed.
    /// (Older Atlassian documentation spells the latter <c>COMPLETED</c>; both are accepted.)
    /// </summary>
    [JsonPropertyName("task_status")]
    public string? TaskStatus { get; init; }

    /// <summary>The merged pull request, present once the task has succeeded.</summary>
    [JsonPropertyName("merge_result")]
    public PullRequestDto? MergeResult { get; init; }

    /// <summary>
    /// The pull request's place in the merge queue, present while it waits in one. Only its
    /// presence is used, so its shape is not modelled.
    /// </summary>
    [JsonPropertyName("queued_pullrequest")]
    public JsonElement? QueuedPullRequest { get; init; }

    /// <summary>Whether the pull request is waiting in a merge queue rather than being merged.</summary>
    [JsonIgnore]
    public bool InMergeQueue => QueuedPullRequest is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) };

    /// <summary>Whether the task reports a landed merge.</summary>
    [JsonIgnore]
    public bool Succeeded => TaskStatus is "SUCCESS" or "COMPLETED";
}
