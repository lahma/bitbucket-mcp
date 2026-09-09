namespace Bitbucket.Mcp.Tools.Models;

/// <summary>One finding from a linter, scanner or coverage tool, anchored to a file and a line.</summary>
internal sealed record CodeInsightAnnotation
{
    /// <summary>Repository-relative path of the file the finding is on.</summary>
    public string? Path { get; init; }

    /// <summary>The line the finding sits on. Absent or 0 means the file as a whole.</summary>
    public int? Line { get; init; }

    /// <summary>The message.</summary>
    public string? Summary { get; init; }

    /// <summary>The longer explanation, when the reporting tool supplied one.</summary>
    public string? Details { get; init; }

    /// <summary><c>VULNERABILITY</c>, <c>CODE_SMELL</c> or <c>BUG</c>.</summary>
    public string? AnnotationType { get; init; }

    /// <summary><c>PASSED</c>, <c>FAILED</c>, <c>SKIPPED</c> or <c>IGNORED</c>.</summary>
    public string? Result { get; init; }

    /// <summary><c>CRITICAL</c>, <c>HIGH</c>, <c>MEDIUM</c> or <c>LOW</c>.</summary>
    public string? Severity { get; init; }

    /// <summary>The finding in the external tool, when it linked one.</summary>
    public string? Url { get; init; }

    /// <summary>
    /// The report this finding came from, present only when several reports are being listed at
    /// once — so a caller can tell a coverage gap from a security finding.
    /// </summary>
    public string? ReportTitle { get; init; }
}

/// <summary>One Code Insights report attached to a commit.</summary>
internal sealed record CodeInsightReport
{
    /// <summary>The report's identifier. Pass it back as <c>reportId</c> to page through its annotations.</summary>
    public string? ReportId { get; init; }

    /// <summary>The report's title.</summary>
    public string? Title { get; init; }

    /// <summary>What the report covers.</summary>
    public string? Details { get; init; }

    /// <summary>The tool that produced it.</summary>
    public string? Reporter { get; init; }

    /// <summary><c>SECURITY</c>, <c>COVERAGE</c>, <c>TEST</c> or <c>BUG</c>.</summary>
    public string? ReportType { get; init; }

    /// <summary><c>PASSED</c>, <c>FAILED</c> or <c>PENDING</c>.</summary>
    public string? Result { get; init; }

    /// <summary>The full results in the external tool.</summary>
    public string? Url { get; init; }

    /// <summary>When the report was created.</summary>
    public DateTimeOffset? CreatedOn { get; init; }

    /// <summary>
    /// How many annotations were pulled in for this report. Only reports whose <see cref="Result"/>
    /// is <c>FAILED</c> are expanded, and only up to the call's cap.
    /// </summary>
    public int? AnnotationsShown { get; init; }
}

/// <summary>
/// A commit's Code Insights: the reports on it, and the file-and-line findings behind the failing
/// ones.
/// </summary>
internal sealed record CodeInsightsResult
{
    /// <summary>The reports on this commit. Empty when no tool has published one.</summary>
    public IReadOnlyList<CodeInsightReport> Reports { get; init; } = [];

    /// <summary>
    /// The findings. In the default mode these come from the failing reports only; with
    /// <c>reportId</c> they are one page of that report's annotations.
    /// </summary>
    public IReadOnlyList<CodeInsightAnnotation> Annotations { get; init; } = [];

    /// <summary>Whether the annotation cap cut the list short.</summary>
    public bool Truncated { get; init; }

    /// <summary>Cursor for the next page of annotations. Only ever set when <c>reportId</c> was given.</summary>
    public string? NextCursor { get; init; }

    /// <summary>What to do next, when there is an obvious next call.</summary>
    public string? Hint { get; init; }
}
