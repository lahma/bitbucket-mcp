using System.Text.Json.Serialization;

namespace Bitbucket.Mcp.Http.Models;

/// <summary>
/// A pull request comment — general when <see cref="Inline"/> is <see langword="null"/>, anchored
/// to a line of the diff when it is not.
/// </summary>
internal sealed record CommentDto
{
    /// <summary>
    /// The comment id, used as the <c>parent</c> of a reply. Typed <see cref="long"/> rather than
    /// <see cref="int"/> because comment ids are allocated globally, not per repository, and there
    /// is no reason to bet on the headroom.
    /// </summary>
    [JsonPropertyName("id")]
    public long? Id { get; init; }

    /// <summary>The comment body.</summary>
    [JsonPropertyName("content")]
    public CommentContentDto? Content { get; init; }

    /// <summary>Who wrote it.</summary>
    [JsonPropertyName("user")]
    public AccountDto? User { get; init; }

    /// <summary>When it was posted.</summary>
    [JsonPropertyName("created_on")]
    public DateTimeOffset? CreatedOn { get; init; }

    /// <summary>When it was last edited.</summary>
    [JsonPropertyName("updated_on")]
    public DateTimeOffset? UpdatedOn { get; init; }

    /// <summary>
    /// Whether the comment has been deleted. Deleted comments are still returned, with their
    /// content blanked, so a thread keeps its shape — they must be filtered out, not rendered.
    /// </summary>
    [JsonPropertyName("deleted")]
    public bool? Deleted { get; init; }

    /// <summary>Set once the thread has been marked resolved.</summary>
    [JsonPropertyName("resolution")]
    public CommentResolutionDto? Resolution { get; init; }

    /// <summary>The comment this one replies to, if any.</summary>
    [JsonPropertyName("parent")]
    public CommentParentDto? Parent { get; init; }

    /// <summary>The diff anchor, for an inline comment.</summary>
    [JsonPropertyName("inline")]
    public InlineDto? Inline { get; init; }

    /// <summary>Bitbucket's own links; only <c>html.href</c> is ever requested.</summary>
    [JsonPropertyName("links")]
    public LinksDto? Links { get; init; }
}

/// <summary>A comment body. Bitbucket also renders <c>html</c> and <c>markup</c>; we only ever want the source.</summary>
internal sealed record CommentContentDto
{
    /// <summary>The raw Markdown the author typed.</summary>
    [JsonPropertyName("raw")]
    public string? Raw { get; init; }
}

/// <summary>The parent link of a threaded reply.</summary>
internal sealed record CommentParentDto
{
    /// <summary>The parent comment's id.</summary>
    [JsonPropertyName("id")]
    public long? Id { get; init; }
}

/// <summary>Who resolved a comment thread, and when.</summary>
internal sealed record CommentResolutionDto
{
    /// <summary>The person who resolved it.</summary>
    [JsonPropertyName("user")]
    public AccountDto? User { get; init; }

    /// <summary>When it was resolved.</summary>
    [JsonPropertyName("created_on")]
    public DateTimeOffset? CreatedOn { get; init; }
}
