using Bitbucket.Mcp.Http.Models;

namespace Bitbucket.Mcp.Diffs;

/// <summary>
/// A resolved inline-comment anchor: the wire payload, plus what it actually landed on.
/// </summary>
/// <remarks>
/// <see cref="Inline"/> is the only part Bitbucket sees. The rest exists so the tool result can
/// echo back the line the comment was attached to — the cheapest way for the caller to notice that
/// a snippet matched something other than what it meant.
/// </remarks>
internal sealed record InlineAnchor
{
    /// <summary>The <c>inline</c> object to send, with the canonical path from the diff.</summary>
    internal required InlineDto Inline { get; init; }

    /// <summary>What kind of diff line the anchor landed on.</summary>
    internal required DiffLineType LineType { get; init; }

    /// <summary>
    /// The anchored line number — in new-file numbering for an added or context line, old-file
    /// numbering for a removed one. Same value as <see cref="InlineDto.To"/> or
    /// <see cref="InlineDto.From"/>, whichever is set.
    /// </summary>
    internal required int Line { get; init; }

    /// <summary>The text of the anchored line, without its <c>+</c>/<c>-</c>/space marker.</summary>
    internal string? MatchedText { get; init; }
}
