namespace Bitbucket.Mcp.Pipelines;

/// <summary>Which part of a step's log to read.</summary>
/// <remarks>
/// <see cref="Tail"/> is the default because a Pipelines step runs under <c>set -e</c>: whatever
/// broke is the last thing printed. The other two exist for the cases the tail cannot serve — a
/// container that never started, and a failure buried in the middle of a long run.
/// </remarks>
internal enum LogReadMode
{
    /// <summary>The end of the log.</summary>
    Tail,

    /// <summary>The beginning — for a step that died before it produced any output of its own.</summary>
    Head,

    /// <summary>Only the lines matching a literal substring, with surrounding context.</summary>
    Search,
}
