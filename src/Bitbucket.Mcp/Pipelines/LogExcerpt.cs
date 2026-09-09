namespace Bitbucket.Mcp.Pipelines;

/// <summary>
/// The part of a pipeline step's log that was actually read, plus everything needed to say
/// truthfully how much of it that was.
/// </summary>
/// <remarks>
/// The <see cref="Diffs.TruncatedDiff"/> analogue, and it exists for the same reason: a model that
/// silently receives the middle of a log will diagnose the build from whatever it can see. Every
/// field here is in service of the one rule — <b>truncation is always visible</b> — so
/// <see cref="Text"/> already carries the markers and the numbers beside it only quantify them.
/// </remarks>
internal sealed record LogExcerpt
{
    /// <summary>The log text that was kept, with any truncation markers already in place.</summary>
    internal required string Text { get; init; }

    /// <summary>Whether anything was left out — the head of the log, gaps between matches, or both.</summary>
    internal bool Truncated { get; init; }

    /// <summary>Lines present in <see cref="Text"/>, markers excluded.</summary>
    internal int LinesShown { get; init; }

    /// <summary>Bytes read off the wire.</summary>
    internal long BytesShown { get; init; }

    /// <summary>
    /// The log's full size when Bitbucket reported one — from <c>Content-Range</c> on a ranged
    /// read, or the body length when the whole log was streamed. <see langword="null"/> when
    /// nothing said.
    /// </summary>
    internal long? TotalBytes { get; init; }

    /// <summary>
    /// In <c>search</c> mode, how many lines matched — which can exceed <see cref="LinesShown"/>
    /// only when the cap cut the matches short. <see langword="null"/> in the other modes.
    /// </summary>
    internal int? MatchCount { get; init; }

    /// <summary>
    /// What to call to see more, or <see langword="null"/> when nothing was left out. Already
    /// included at the end of <see cref="Text"/>.
    /// </summary>
    internal string? Hint { get; init; }
}
