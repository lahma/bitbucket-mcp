using System.Text.Json.Serialization;

namespace Bitbucket.Mcp.Http.Models;

/// <summary>
/// The anchor that turns a comment into an inline comment: a file, and a line in the old or the
/// new side of the diff.
/// </summary>
/// <remarks>
/// <para>
/// Exactly one of <see cref="To"/> and <see cref="From"/> should be set. <see cref="To"/> anchors
/// to a line in the file <em>after</em> the change (an added or context line); <see cref="From"/>
/// anchors to a line in the file <em>before</em> it (a removed line). Setting the matching
/// <see cref="StartTo"/> or <see cref="StartFrom"/> turns it into a multi-line comment.
/// </para>
/// <para>
/// This same shape is used for both directions. On a request <see cref="Path"/> is mandatory — the
/// API rejects an inline comment without one — but it is typed nullable because a
/// <c>fields=</c>-trimmed response may not carry it back; the client layer validates before
/// sending.
/// </para>
/// </remarks>
internal sealed record InlineDto
{
    /// <summary>Repository-relative path of the commented file. Required when posting.</summary>
    [JsonPropertyName("path")]
    public string? Path { get; init; }

    /// <summary>Line number in the file before the change (a removed line).</summary>
    [JsonPropertyName("from")]
    public int? From { get; init; }

    /// <summary>Line number in the file after the change (an added or context line).</summary>
    [JsonPropertyName("to")]
    public int? To { get; init; }

    /// <summary>First line of a multi-line comment on the old side.</summary>
    [JsonPropertyName("start_from")]
    public int? StartFrom { get; init; }

    /// <summary>First line of a multi-line comment on the new side.</summary>
    [JsonPropertyName("start_to")]
    public int? StartTo { get; init; }
}
