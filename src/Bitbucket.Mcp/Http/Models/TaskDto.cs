using System.Text.Json.Serialization;

namespace Bitbucket.Mcp.Http.Models;

/// <summary>
/// A pull request task: one line of "this has to happen before merge", with a state that flips
/// between <c>UNRESOLVED</c> and <c>RESOLVED</c>.
/// </summary>
/// <remarks>
/// Bitbucket also returns <c>pending</c>, <c>resolved_on</c> and a full <c>comment</c> object when a
/// task hangs off one. Only the comment's id is requested — the comment itself is already reachable
/// through <c>getPullRequestComments</c>, and duplicating it here would put the same text in the
/// model's context twice.
/// </remarks>
internal sealed record TaskDto
{
    /// <summary>The task id, unique within its pull request.</summary>
    [JsonPropertyName("id")]
    public long? Id { get; init; }

    /// <summary><c>UNRESOLVED</c> or <c>RESOLVED</c>.</summary>
    [JsonPropertyName("state")]
    public string? State { get; init; }

    /// <summary>The task text. Shares the comment content shape — Bitbucket uses the same object.</summary>
    [JsonPropertyName("content")]
    public CommentContentDto? Content { get; init; }

    /// <summary>Who created the task.</summary>
    [JsonPropertyName("creator")]
    public AccountDto? Creator { get; init; }

    /// <summary>Who resolved it, once someone has.</summary>
    [JsonPropertyName("resolved_by")]
    public AccountDto? ResolvedBy { get; init; }

    /// <summary>The comment the task hangs off, when it was created against one.</summary>
    [JsonPropertyName("comment")]
    public TaskCommentDto? Comment { get; init; }

    /// <summary>When the task was created.</summary>
    [JsonPropertyName("created_on")]
    public DateTimeOffset? CreatedOn { get; init; }

    /// <summary>When it last changed.</summary>
    [JsonPropertyName("updated_on")]
    public DateTimeOffset? UpdatedOn { get; init; }
}

/// <summary>The comment a task is attached to, trimmed to the id that identifies it.</summary>
internal sealed record TaskCommentDto
{
    /// <summary>The comment's id.</summary>
    [JsonPropertyName("id")]
    public long? Id { get; init; }
}

/// <summary>
/// Body of <c>POST /pullrequests/{id}/tasks</c>. Bitbucket rejects anything but these two keys.
/// </summary>
internal sealed record CreateTaskRequest
{
    /// <summary>The task text. Required.</summary>
    [JsonPropertyName("content")]
    public required CommentContentRequest Content { get; init; }

    /// <summary>
    /// The comment to attach the task to. Optional — a task with one is rendered underneath that
    /// comment in the Bitbucket UI instead of in the pull request's task list alone.
    /// </summary>
    [JsonPropertyName("comment")]
    public TaskCommentRequest? Comment { get; init; }
}

/// <summary>
/// Body of <c>PUT /pullrequests/{id}/tasks/{task_id}</c>. Both fields are optional in the published
/// schema, so a state-only body is a legal partial update.
/// </summary>
internal sealed record UpdateTaskRequest
{
    /// <summary>Replacement task text. Omitted to leave it alone.</summary>
    [JsonPropertyName("content")]
    public CommentContentRequest? Content { get; init; }

    /// <summary><c>RESOLVED</c> or <c>UNRESOLVED</c>. Omitted to leave the state alone.</summary>
    [JsonPropertyName("state")]
    public string? State { get; init; }
}

/// <summary>The comment a task is being attached to, in a request body.</summary>
internal sealed record TaskCommentRequest
{
    /// <summary>The comment's id.</summary>
    [JsonPropertyName("id")]
    public required long Id { get; init; }
}
