namespace Bitbucket.Mcp.Diffs;

/// <summary>
/// A diff cut down to fit a tool result, plus everything needed to say honestly what was left out.
/// </summary>
/// <remarks>
/// <para>
/// Every count here is measured in <em>body</em> lines — hunk headers, <c>+</c>/<c>-</c>/context
/// lines and binary notices. A file's <see cref="DiffFile.Header"/> is never truncated and never
/// counted: it is a handful of lines that identify the file, and dropping them would leave content
/// that cannot be attributed to anything.
/// </para>
/// <para>
/// <see cref="Text"/> is the complete rendering, including every truncation marker. The markers are
/// also reachable structurally (<see cref="TruncatedDiffFile.Truncated"/> and
/// <see cref="FilesOmittedNotice"/>) so a caller that renders the per-file entries itself cannot
/// accidentally present a truncated diff as a whole one.
/// </para>
/// </remarks>
internal sealed record TruncatedDiff
{
    /// <summary>The rendered diff: every emitted file, in order, with all truncation markers.</summary>
    internal required string Text { get; init; }

    /// <summary>Whether anything at all was left out — a file's tail, whole files, or both.</summary>
    internal bool Truncated { get; init; }

    /// <summary>How many files are present in <see cref="Files"/>.</summary>
    internal int FilesShown { get; init; }

    /// <summary>How many files the diff contained.</summary>
    internal int FilesTotal { get; init; }

    /// <summary>Body lines emitted across all files.</summary>
    internal int LinesShown { get; init; }

    /// <summary>
    /// The marker describing files that were dropped entirely, or <see langword="null"/> when every
    /// file was emitted. Already included at the end of <see cref="Text"/>.
    /// </summary>
    internal string? FilesOmittedNotice { get; init; }

    /// <summary>The files that were emitted, in diff order.</summary>
    internal IReadOnlyList<TruncatedDiffFile> Files { get; init; } = [];
}

/// <summary>One emitted file's share of a <see cref="TruncatedDiff"/>.</summary>
internal sealed record TruncatedDiffFile
{
    /// <summary>The file's path — the new path when there is one, otherwise the old one.</summary>
    internal required string Path { get; init; }

    /// <summary>What happened to the file.</summary>
    internal DiffFileStatus Status { get; init; }

    /// <summary>
    /// The file's header, the body lines that fit, and — when they did not all fit — the one marker
    /// line that says so.
    /// </summary>
    internal required string Text { get; init; }

    /// <summary>Whether this file's body was cut short.</summary>
    internal bool Truncated { get; init; }

    /// <summary>Body lines emitted for this file.</summary>
    internal int LinesShown { get; init; }

    /// <summary>Body lines this file has in the full diff.</summary>
    internal int LinesTotal { get; init; }
}
