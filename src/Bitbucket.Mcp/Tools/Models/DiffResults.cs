namespace Bitbucket.Mcp.Tools.Models;

/// <summary>One changed file as diffstat describes it: what happened, and how much.</summary>
internal sealed record DiffStatFile
{
    /// <summary>
    /// Repository-relative path — the new path when there is one, otherwise the old one. Feed this
    /// back verbatim as an entry of <c>paths</c>.
    /// </summary>
    public string? Path { get; init; }

    /// <summary><c>added</c>, <c>removed</c>, <c>modified</c>, <c>renamed</c> or a merge-conflict marker.</summary>
    public string? Status { get; init; }

    /// <summary>Lines added. Zero for a binary file.</summary>
    public int? LinesAdded { get; init; }

    /// <summary>Lines removed. Zero for a binary file.</summary>
    public int? LinesRemoved { get; init; }
}

/// <summary>
/// The file list of a pull request's diff. This is the first call of any diff workflow: it is
/// paginated, cheap, and still works on pull requests whose full diff Bitbucket refuses to build.
/// </summary>
internal sealed record DiffStatResult
{
    /// <summary>The changed files on this page.</summary>
    public IReadOnlyList<DiffStatFile> Files { get; init; } = [];

    /// <summary>Cursor for the next page of files, or absent when there are no more.</summary>
    public string? NextCursor { get; init; }

    /// <summary>Total changed files, when Bitbucket reported a count.</summary>
    public int? TotalFiles { get; init; }
}

/// <summary>One file's unified diff, and an honest account of how much of it is here.</summary>
internal sealed record DiffFileDiff
{
    /// <summary>Repository-relative path — the new path when there is one, otherwise the old one.</summary>
    public string? Path { get; init; }

    /// <summary><c>modified</c>, <c>added</c>, <c>removed</c>, <c>renamed</c> or <c>binary</c>.</summary>
    public string? Status { get; init; }

    /// <summary>
    /// The unified diff for this file, including any truncation marker. Copy a line out of here
    /// verbatim to use as <c>codeSnippet</c> when commenting.
    /// </summary>
    public string? Diff { get; init; }

    /// <summary>Whether this file's diff was cut short.</summary>
    public bool Truncated { get; init; }

    /// <summary>Diff lines included for this file.</summary>
    public int LinesShown { get; init; }

    /// <summary>Diff lines this file has in total.</summary>
    public int LinesTotal { get; init; }
}

/// <summary>The requested files' diffs, truncated to the configured budgets.</summary>
/// <remarks>
/// Truncation is never silent: every cut leaves a marker line inside
/// <see cref="DiffFileDiff.Diff"/>, <see cref="Truncated"/> says that something was cut, and
/// <see cref="Hint"/> says what to call to see the rest.
/// </remarks>
internal sealed record DiffResult
{
    /// <summary>The files included in this response, in diff order.</summary>
    public IReadOnlyList<DiffFileDiff> Files { get; init; } = [];

    /// <summary>Whether anything was left out — a file's tail, whole files, or both.</summary>
    public bool Truncated { get; init; }

    /// <summary>What to call to see what was left out. Absent when nothing was.</summary>
    public string? Hint { get; init; }
}

/// <summary>
/// The result of <c>getPullRequestDiff</c> in either of its modes.
/// </summary>
/// <remarks>
/// One tool, two shapes: <see cref="Mode"/> says which one arrived, and exactly one of
/// <see cref="Diffstat"/> and <see cref="Diff"/> is present (nulls are omitted from the JSON). A
/// single result type is what lets the tool advertise one output schema while still keeping the
/// file list and the file contents as the two distinct things they are.
/// </remarks>
internal sealed record PullRequestDiffResult
{
    /// <summary><c>diffstat</c> or <c>diff</c> — which of the two payloads below is present.</summary>
    public string? Mode { get; init; }

    /// <summary>The changed-file list. Present when <see cref="Mode"/> is <c>diffstat</c>.</summary>
    public DiffStatResult? Diffstat { get; init; }

    /// <summary>The file diffs. Present when <see cref="Mode"/> is <c>diff</c>.</summary>
    public DiffResult? Diff { get; init; }
}
