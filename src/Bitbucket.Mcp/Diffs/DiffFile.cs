namespace Bitbucket.Mcp.Diffs;

/// <summary>What happened to a file, as far as the diff's own headers reveal.</summary>
/// <remarks>
/// The values line up with the vocabulary Bitbucket's diffstat uses, so a tool result can report
/// the same word whether it came from <c>/diffstat</c> or from parsing <c>/diff</c>.
/// <see cref="Modified"/> is first so that it is also the fallback for a chunk whose headers say
/// nothing.
/// </remarks>
internal enum DiffFileStatus
{
    /// <summary>Content changed in place. Also the fallback when the headers are uninformative.</summary>
    Modified,

    /// <summary>The file did not exist before (<c>new file mode</c>, or <c>--- /dev/null</c>).</summary>
    Added,

    /// <summary>The file does not exist after (<c>deleted file mode</c>, or <c>+++ /dev/null</c>).</summary>
    Removed,

    /// <summary>The file moved (<c>rename from</c> / <c>rename to</c>).</summary>
    Renamed,

    /// <summary>A binary file whose only "change" the diff can describe is that it differs.</summary>
    Binary,
}

/// <summary>
/// One file's section of a unified diff, split into the part that describes the file and the part
/// that describes the change.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Header"/> runs from the <c>diff --git</c> line up to — but not including — the first
/// line of actual content, which is the first <c>@@</c> hunk header, a <c>Binary files … differ</c>
/// notice, or a <c>GIT binary patch</c> block. Binary notices count as content deliberately: a
/// literal binary patch can run to thousands of lines, and everything in <see cref="Header"/> is
/// exempt from truncation.
/// </para>
/// <para>
/// <see cref="OldPath"/> is <see langword="null"/> exactly when the file was added and
/// <see cref="NewPath"/> exactly when it was removed, mirroring how diffstat reports the same file.
/// Neither is a URL-escaped value — they are repository-relative paths, ready to be handed back to
/// <c>diff?path=</c> verbatim.
/// </para>
/// </remarks>
internal sealed record DiffFile
{
    /// <summary>Stand-in path for a diff chunk that carried no file name at all.</summary>
    internal const string UnknownPath = "(unknown)";

    /// <summary>The path before the change; <see langword="null"/> when the file was added.</summary>
    internal string? OldPath { get; init; }

    /// <summary>The path after the change; <see langword="null"/> when the file was removed.</summary>
    internal string? NewPath { get; init; }

    /// <summary>What happened to the file.</summary>
    internal DiffFileStatus Status { get; init; }

    /// <summary>
    /// Whether the diff refused to show content because the file is binary. Independent of
    /// <see cref="Status"/>, which stays <see cref="DiffFileStatus.Added"/> for a newly added
    /// binary file rather than losing that fact.
    /// </summary>
    internal bool IsBinary { get; init; }

    /// <summary>The <c>diff --git</c> line and everything up to the first content line.</summary>
    internal IReadOnlyList<string> Header { get; init; } = [];

    /// <summary>The content lines: <c>@@</c> hunk headers, their <c>+</c>/<c>-</c>/context lines,
    /// <c>\ No newline at end of file</c> markers, and binary notices.</summary>
    internal IReadOnlyList<string> Body { get; init; } = [];

    /// <summary>
    /// The path to show a user or feed back to the API: the new path when there is one, otherwise
    /// the old one. This is also the path an inline comment on this file should carry.
    /// </summary>
    internal string Path => NewPath ?? OldPath ?? UnknownPath;
}
