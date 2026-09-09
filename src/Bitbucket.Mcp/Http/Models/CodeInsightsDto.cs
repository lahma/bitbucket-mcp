using System.Text.Json.Serialization;

namespace Bitbucket.Mcp.Http.Models;

/// <summary>
/// One Code Insights report attached to a commit — a linter, scanner, coverage or test tool's
/// verdict.
/// </summary>
/// <remarks>
/// The <c>data</c> array is deliberately not requested. Its <c>value</c> is polymorphic (number,
/// string, boolean, date or link), which would need a <c>JsonElement</c> in a model whose whole
/// convention is nullable primitives, and <see cref="Title"/> plus <see cref="Result"/> plus
/// <see cref="Details"/> already say what happened.
/// </remarks>
internal sealed record CodeInsightsReportDto
{
    /// <summary>The report's UUID, in braced form — how its annotations are addressed.</summary>
    [JsonPropertyName("uuid")]
    public string? Uuid { get; init; }

    /// <summary>The reporter's own id, unique per reporter. An alternative key to <see cref="Uuid"/>.</summary>
    [JsonPropertyName("external_id")]
    public string? ExternalId { get; init; }

    /// <summary>The report's title, for example <c>Security scan</c>.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>A sentence describing what the report covers.</summary>
    [JsonPropertyName("details")]
    public string? Details { get; init; }

    /// <summary>The tool or company that produced it.</summary>
    [JsonPropertyName("reporter")]
    public string? Reporter { get; init; }

    /// <summary><c>SECURITY</c>, <c>COVERAGE</c>, <c>TEST</c> or <c>BUG</c>.</summary>
    [JsonPropertyName("report_type")]
    public string? ReportType { get; init; }

    /// <summary><c>PASSED</c>, <c>FAILED</c> or <c>PENDING</c>.</summary>
    [JsonPropertyName("result")]
    public string? Result { get; init; }

    /// <summary>A URL to the full results in the external tool.</summary>
    [JsonPropertyName("link")]
    public string? Link { get; init; }

    /// <summary>When the report was created.</summary>
    [JsonPropertyName("created_on")]
    public DateTimeOffset? CreatedOn { get; init; }
}

/// <summary>
/// One finding inside a report, anchored to a file and a line.
/// </summary>
/// <remarks>
/// This is the richest "why did the build fail" signal Bitbucket carries, and the only one that
/// points at a place in the code rather than at a log. It also needs no scope the pull-request
/// tools do not already hold.
/// </remarks>
internal sealed record CodeInsightsAnnotationDto
{
    /// <summary>The annotation's UUID.</summary>
    [JsonPropertyName("uuid")]
    public string? Uuid { get; init; }

    /// <summary>Repository-relative path of the file the finding is on.</summary>
    [JsonPropertyName("path")]
    public string? Path { get; init; }

    /// <summary>The line the finding sits on; absent or 0 means the file as a whole.</summary>
    [JsonPropertyName("line")]
    public int? Line { get; init; }

    /// <summary>The message shown to a reader.</summary>
    [JsonPropertyName("summary")]
    public string? Summary { get; init; }

    /// <summary>The longer explanation.</summary>
    [JsonPropertyName("details")]
    public string? Details { get; init; }

    /// <summary><c>VULNERABILITY</c>, <c>CODE_SMELL</c> or <c>BUG</c>.</summary>
    [JsonPropertyName("annotation_type")]
    public string? AnnotationType { get; init; }

    /// <summary><c>PASSED</c>, <c>FAILED</c>, <c>SKIPPED</c> or <c>IGNORED</c>.</summary>
    [JsonPropertyName("result")]
    public string? Result { get; init; }

    /// <summary><c>CRITICAL</c>, <c>HIGH</c>, <c>MEDIUM</c> or <c>LOW</c>.</summary>
    [JsonPropertyName("severity")]
    public string? Severity { get; init; }

    /// <summary>A URL to the finding in the external tool.</summary>
    [JsonPropertyName("link")]
    public string? Link { get; init; }
}
