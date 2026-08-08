namespace Bitbucket.Mcp.Http;

/// <summary>
/// A pagination cursor handed back to a tool did not decode to a URL this client is willing to
/// request.
/// </summary>
/// <remarks>
/// <para>
/// Distinct from <see cref="ArgumentException"/> on purpose: the tool-layer error funnel has to
/// tell these apart from ordinary bad arguments, because the advice differs — "pass the
/// <c>nextCursor</c> from the previous result back verbatim, do not edit or invent one" — and
/// because a rejected cursor may be the visible end of an injection attempt rather than a typo
/// (see <see cref="BitbucketCursor"/> for the threat model).
/// </para>
/// <para>
/// The offending cursor is carried as a property and deliberately kept out of
/// <see cref="Exception.Message"/>: the message is shown to the user and echoing
/// attacker-influenced text into it buys nothing.
/// </para>
/// </remarks>
internal sealed class InvalidCursorException : Exception
{
    /// <param name="cursor">The cursor as received, for logs and diagnostics.</param>
    internal InvalidCursorException(string? cursor)
        : base("The pagination cursor is not valid. Pass the nextCursor value from the previous page back unchanged.")
    {
        Cursor = cursor;
    }

    /// <summary>The rejected cursor, exactly as it arrived.</summary>
    internal string? Cursor { get; }
}
