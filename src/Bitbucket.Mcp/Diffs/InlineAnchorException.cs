namespace Bitbucket.Mcp.Diffs;

/// <summary>
/// Thrown when a comment cannot be anchored to a specific line of a diff.
/// </summary>
/// <remarks>
/// The message is written for the model that made the call, not for a log: it says what went wrong,
/// shows the candidate lines with their numbers, and names the parameter to change on the next
/// attempt. The tool layer passes it through to the caller unchanged, so it is the only chance to
/// turn a failed guess into a successful retry.
/// </remarks>
internal sealed class InlineAnchorException : Exception
{
    internal InlineAnchorException(string message)
        : base(message)
    {
    }

    internal InlineAnchorException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
