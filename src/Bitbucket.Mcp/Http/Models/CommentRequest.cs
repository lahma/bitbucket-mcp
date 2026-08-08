using System.Text.Json.Serialization;

namespace Bitbucket.Mcp.Http.Models;

/// <summary>
/// Body of <c>POST /pullrequests/{id}/comments</c>. A comment is general unless
/// <see cref="Inline"/> is set, and a reply unless <see cref="Parent"/> is set; the two are
/// independent — a reply to an inline comment carries both.
/// </summary>
internal sealed record CommentRequest
{
    /// <summary>The comment body. Required.</summary>
    [JsonPropertyName("content")]
    public required CommentContentRequest Content { get; init; }

    /// <summary>
    /// The diff anchor, for an inline comment. <c>InlineDto.Path</c> must be set — the API rejects
    /// an inline object without one.
    /// </summary>
    [JsonPropertyName("inline")]
    public InlineDto? Inline { get; init; }

    /// <summary>The comment being replied to.</summary>
    [JsonPropertyName("parent")]
    public CommentParentRequest? Parent { get; init; }
}

/// <summary>A comment body in a request.</summary>
internal sealed record CommentContentRequest
{
    /// <summary>The Markdown source of the comment.</summary>
    [JsonPropertyName("raw")]
    public required string Raw { get; init; }
}

/// <summary>The parent link of a threaded reply in a request.</summary>
internal sealed record CommentParentRequest
{
    /// <summary>The parent comment's id.</summary>
    [JsonPropertyName("id")]
    public required long Id { get; init; }
}
