using System.Text.Json.Serialization;

namespace Bitbucket.Mcp.Http.Models;

/// <summary>
/// The body Bitbucket returns when a merge is queued instead of performed — a <c>202</c> with a
/// task handle rather than the merged pull request.
/// </summary>
/// <remarks>
/// Polling a merge task is out of scope: the tool reports the queued state and tells the user to
/// check the pull request, which is honest and avoids a tool call that blocks for minutes. This
/// type exists so that the message can quote the task's actual status instead of guessing.
/// </remarks>
internal sealed record MergeTaskStatusDto
{
    /// <summary><c>PENDING</c> while queued, <c>COMPLETED</c> once the merge has been performed.</summary>
    [JsonPropertyName("task_status")]
    public string? TaskStatus { get; init; }

    /// <summary>The merged pull request, present once <see cref="TaskStatus"/> is <c>COMPLETED</c>.</summary>
    [JsonPropertyName("merge_result")]
    public PullRequestDto? MergeResult { get; init; }
}
