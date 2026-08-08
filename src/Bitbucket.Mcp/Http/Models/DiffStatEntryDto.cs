using System.Text.Json.Serialization;

namespace Bitbucket.Mcp.Http.Models;

/// <summary>
/// One file's worth of change summary. Diffstat is the entry point to every diff workflow: it is
/// paginated, cheap, and survives pull requests whose full diff returns <c>555</c>.
/// </summary>
internal sealed record DiffStatEntryDto
{
    /// <summary>
    /// <c>added</c>, <c>removed</c>, <c>modified</c>, <c>renamed</c>, <c>merge conflict</c> or
    /// <c>local deleted</c>.
    /// </summary>
    [JsonPropertyName("status")]
    public string? Status { get; init; }

    /// <summary>Lines added. Zero for a binary file.</summary>
    [JsonPropertyName("lines_added")]
    public int? LinesAdded { get; init; }

    /// <summary>Lines removed. Zero for a binary file.</summary>
    [JsonPropertyName("lines_removed")]
    public int? LinesRemoved { get; init; }

    /// <summary>The file before the change. <see langword="null"/> when the file was added.</summary>
    [JsonPropertyName("old")]
    public DiffStatFileDto? Old { get; init; }

    /// <summary>The file after the change. <see langword="null"/> when the file was deleted.</summary>
    [JsonPropertyName("new")]
    public DiffStatFileDto? New { get; init; }
}

/// <summary>One side of a diffstat entry.</summary>
internal sealed record DiffStatFileDto
{
    /// <summary>
    /// Repository-relative path. This is the value to feed back as <c>diff?path=…</c>, verbatim
    /// and unescaped — the client layer does the URL escaping.
    /// </summary>
    [JsonPropertyName("path")]
    public string? Path { get; init; }
}
