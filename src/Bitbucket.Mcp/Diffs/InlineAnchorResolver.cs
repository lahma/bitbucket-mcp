using System.Globalization;
using System.Text;

using Bitbucket.Mcp.Http.Models;

namespace Bitbucket.Mcp.Diffs;

/// <summary>Which side of a diff a line belongs to.</summary>
internal enum DiffLineType
{
    /// <summary>A <c>+</c> line: present only after the change, numbered in the new file.</summary>
    Added,

    /// <summary>A <c>-</c> line: present only before the change, numbered in the old file.</summary>
    Removed,

    /// <summary>An unchanged line shown for context, numbered in both files.</summary>
    Context,
}

/// <summary>
/// Turns "comment on this piece of code" into the line anchor Bitbucket's API demands.
/// </summary>
/// <remarks>
/// <para>
/// Bitbucket anchors an inline comment with <c>to</c> (a line number in the file after the change)
/// or <c>from</c> (before it). A model cannot reliably count lines out of a diff, but it can quote
/// one, so <c>codeSnippet</c> is the preferred input: the snippet is matched against the file's
/// hunk lines and the number is derived by walking the <c>@@ -a,b +c,d @@</c> counters. Added and
/// context lines resolve to <c>to</c>, removed lines to <c>from</c>.
/// </para>
/// <para>
/// Every failure throws <see cref="InlineAnchorException"/> with a message meant for the caller to
/// act on: a snippet that matches nothing lists the nearest lines, a snippet that matches several
/// lists all of them with their numbers, and re-calling with both the snippet and one of those
/// numbers resolves it. Guessing an anchor would be worse than failing — the comment would land on
/// unrelated code.
/// </para>
/// <para>
/// Matching is whitespace-insensitive (both sides are trimmed) and tries, in order: whole-line
/// equality, substring containment, and then the same two again with a copied-in <c>+</c>/<c>-</c>
/// diff marker removed. A multi-line snippet must match consecutive lines of one hunk on one side,
/// and becomes a multi-line comment spanning them.
/// </para>
/// </remarks>
internal static class InlineAnchorResolver
{
    /// <summary>How many "did you mean" lines a no-match error shows.</summary>
    private const int MaxNearestCandidates = 5;

    /// <summary>Cap on the candidates an ambiguity error lists, so the message stays readable.</summary>
    private const int MaxAmbiguousCandidates = 20;

    /// <summary>Cap on the paths an unknown-path error lists.</summary>
    private const int MaxListedPaths = 20;

    /// <summary>Cap on the line ranges an out-of-diff error lists.</summary>
    private const int MaxListedRanges = 10;

    /// <summary>Longest quoted line of code in a message.</summary>
    private const int ExcerptLength = 120;

    /// <summary>Resolves an inline-comment anchor against a parsed diff.</summary>
    /// <param name="files">The parsed diff the comment is being placed in.</param>
    /// <param name="path">The file to comment on, as the diff or the diffstat spells it.</param>
    /// <param name="codeSnippet">
    /// The text of the line(s) to comment on. When present it decides the anchor; an accompanying
    /// <paramref name="line"/> only breaks a tie between several matches.
    /// </param>
    /// <param name="line">
    /// An explicit line number. Used on its own when <paramref name="codeSnippet"/> is absent, and
    /// as the tie-breaker when a snippet matches more than one line.
    /// </param>
    /// <param name="lineType">How to read <paramref name="line"/>. Defaults to <see cref="DiffLineType.Added"/>.</param>
    /// <param name="startLine">First line of a multi-line comment; must be smaller than the anchored line.</param>
    /// <exception cref="InlineAnchorException">The anchor could not be resolved to exactly one line.</exception>
    internal static InlineAnchor Resolve(
        IReadOnlyList<DiffFile> files,
        string path,
        string? codeSnippet = null,
        int? line = null,
        DiffLineType? lineType = null,
        int? startLine = null)
    {
        ArgumentNullException.ThrowIfNull(files);

        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InlineAnchorException(
                "An inline comment needs the path of the file to comment on, spelled exactly as the diff spells it.");
        }

        var file = FindFile(files, path) ?? throw new InlineAnchorException(UnknownPathMessage(files, path));
        var lines = ReadHunkLines(file);

        if (lines.Count == 0)
        {
            throw new InlineAnchorException(NoLinesMessage(file));
        }

        if (!string.IsNullOrWhiteSpace(codeSnippet))
        {
            return ResolveBySnippet(file, lines, codeSnippet, line, startLine);
        }

        if (line is null)
        {
            throw new InlineAnchorException(string.Create(
                CultureInfo.InvariantCulture,
                $"Cannot place an inline comment on {file.Path} without knowing where. Pass codeSnippet with the text of the line to comment on, copied from the diff, or pass line together with lineType (ADDED, REMOVED or CONTEXT)."));
        }

        return ResolveByLine(file, lines, line.Value, lineType ?? DiffLineType.Added, startLine);
    }

    private static InlineAnchor ResolveBySnippet(
        DiffFile file,
        List<HunkLine> lines,
        string codeSnippet,
        int? line,
        int? startLine)
    {
        // Non-empty by construction: Resolve only takes this path for a snippet that is not
        // whitespace, and NormalizeSnippet keeps every trimmed line that has any text left.
        var needle = NormalizeSnippet(codeSnippet, stripDiffMarkers: false);

        var matches = FindRuns(lines, needle, exact: true);

        if (matches.Count == 0)
        {
            matches = FindRuns(lines, needle, exact: false);
        }

        // A snippet copied straight out of the diff still carries its + or - marker, which nothing
        // in the file's content will ever match. Retry without it rather than report "no match".
        if (matches.Count == 0)
        {
            var unmarked = NormalizeSnippet(codeSnippet, stripDiffMarkers: true);

            if (unmarked.Count > 0)
            {
                matches = FindRuns(lines, unmarked, exact: true);

                if (matches.Count == 0)
                {
                    matches = FindRuns(lines, unmarked, exact: false);
                }

                if (matches.Count > 0)
                {
                    needle = unmarked;
                }
            }
        }

        if (matches.Count == 0)
        {
            throw new InlineAnchorException(NoMatchMessage(file, lines, needle));
        }

        // The ambiguity error tells the caller to re-send the snippet with a line number; this is
        // where that second call lands.
        if (matches.Count > 1 && line is not null)
        {
            var narrowed = new List<Run>();

            for (var i = 0; i < matches.Count; i++)
            {
                if (TryResolveSide(lines, matches[i], out var fromSide)
                    && AnchorLine(lines, matches[i], fromSide) == line.Value)
                {
                    narrowed.Add(matches[i]);
                }
            }

            if (narrowed.Count == 1)
            {
                matches = narrowed;
            }
        }

        if (matches.Count > 1)
        {
            throw new InlineAnchorException(AmbiguousMessage(file, lines, matches));
        }

        var run = matches[0];

        if (!TryResolveSide(lines, run, out var side))
        {
            throw new InlineAnchorException(string.Create(
                CultureInfo.InvariantCulture,
                $"That codeSnippet spans both removed and added lines of {file.Path}, so it cannot be one comment anchor. Comment on one side at a time: pass line with lineType=ADDED for a line the change adds, or lineType=REMOVED for one it deletes."));
        }

        var anchor = AnchorLine(lines, run, side);
        int? start = run.End > run.Start ? SideLine(lines[run.Start], side) : null;

        if (startLine is not null)
        {
            RequireStartBeforeAnchor(startLine.Value, anchor);

            // The snippet decided the side, so startLine has to be visible on that same side — the
            // same check ResolveByLine makes. Without it a range could start above the first line
            // the diff shows, and Bitbucket would anchor the comment somewhere nobody named.
            if (FindLine(lines, startLine.Value, side) < 0)
            {
                throw new InlineAnchorException(LineNotShownMessage(file, lines, startLine.Value, side, "startLine"));
            }

            start = startLine.Value;
        }

        return Build(file, lines, run, side, anchor, start);
    }

    private static InlineAnchor ResolveByLine(
        DiffFile file,
        List<HunkLine> lines,
        int line,
        DiffLineType lineType,
        int? startLine)
    {
        if (line <= 0)
        {
            throw new InlineAnchorException(string.Create(
                CultureInfo.InvariantCulture,
                $"line must be 1 or greater; got {line}. Line numbers in a diff start at 1."));
        }

        var fromSide = lineType == DiffLineType.Removed;
        var index = FindLine(lines, line, fromSide);

        if (index < 0)
        {
            throw new InlineAnchorException(LineNotShownMessage(file, lines, line, fromSide, "line"));
        }

        int? start = null;

        if (startLine is not null)
        {
            RequireStartBeforeAnchor(startLine.Value, line);

            if (FindLine(lines, startLine.Value, fromSide) < 0)
            {
                throw new InlineAnchorException(LineNotShownMessage(file, lines, startLine.Value, fromSide, "startLine"));
            }

            start = startLine.Value;
        }

        var run = new Run(index, index);
        return Build(file, lines, run, fromSide, line, start);
    }

    private static InlineAnchor Build(DiffFile file, List<HunkLine> lines, Run run, bool fromSide, int anchor, int? start)
    {
        var inline = fromSide
            ? new InlineDto { Path = file.Path, From = anchor, StartFrom = start }
            : new InlineDto { Path = file.Path, To = anchor, StartTo = start };

        return new InlineAnchor
        {
            Inline = inline,
            LineType = lines[run.End].Type,
            Line = anchor,
            MatchedText = MatchedText(lines, run),
        };
    }

    private static void RequireStartBeforeAnchor(int startLine, int anchor)
    {
        if (startLine >= anchor)
        {
            throw new InlineAnchorException(string.Create(
                CultureInfo.InvariantCulture,
                $"startLine ({startLine}) must be smaller than the commented line ({anchor}): startLine is the first line of the range and line is the last. Drop startLine for a comment on a single line."));
        }
    }

    // ---------------------------------------------------------------------------------------
    // Hunk walking
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Expands a file's body into one entry per real diff line, numbered by walking the
    /// <c>@@ -a,b +c,d @@</c> counters: a <c>+</c> line advances the new-file counter, a <c>-</c>
    /// line the old-file counter, a context line both.
    /// </summary>
    private static List<HunkLine> ReadHunkLines(DiffFile file)
    {
        var result = new List<HunkLine>(file.Body.Count);
        var oldLine = 0;
        var newLine = 0;
        var hunk = -1;

        for (var i = 0; i < file.Body.Count; i++)
        {
            var line = file.Body[i];

            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                // An unparsable header invalidates the counters; skip its lines rather than
                // number them wrongly and anchor a comment to the wrong place.
                hunk = TryParseHunkHeader(line, out oldLine, out newLine) ? hunk + 1 : -1;
                continue;
            }

            // Before the first hunk header there is nothing to number (a binary notice, say), and
            // "\ No newline at end of file" is a note about the line above, not a line of its own.
            if (hunk < 0 || line.StartsWith('\\'))
            {
                continue;
            }

            // git writes a bare space for an unchanged empty line, but anything that trims trailing
            // whitespace turns that into an empty line. It is still context, and still counts.
            if (line.Length == 0)
            {
                result.Add(new HunkLine(DiffLineType.Context, oldLine, newLine, string.Empty, string.Empty, hunk));
                oldLine++;
                newLine++;
                continue;
            }

            switch (line[0])
            {
                case '+':
                    result.Add(new HunkLine(DiffLineType.Added, 0, newLine, line[1..], Trim(line, 1), hunk));
                    newLine++;
                    break;

                case '-':
                    result.Add(new HunkLine(DiffLineType.Removed, oldLine, 0, line[1..], Trim(line, 1), hunk));
                    oldLine++;
                    break;

                default:
                    var offset = line[0] == ' ' ? 1 : 0;
                    result.Add(new HunkLine(DiffLineType.Context, oldLine, newLine, line[offset..], Trim(line, offset), hunk));
                    oldLine++;
                    newLine++;
                    break;
            }
        }

        return result;
    }

    private static string Trim(string line, int offset) => line.AsSpan(offset).Trim().ToString();

    /// <summary>Reads the two start line numbers out of <c>@@ -a,b +c,d @@</c>.</summary>
    private static bool TryParseHunkHeader(string line, out int oldStart, out int newStart)
    {
        oldStart = 0;
        newStart = 0;

        var minus = line.IndexOf('-');
        var plus = line.IndexOf('+');

        return minus >= 0
            && plus > minus
            && TryReadInt(line, minus + 1, out oldStart)
            && TryReadInt(line, plus + 1, out newStart);
    }

    private static bool TryReadInt(string text, int start, out int value)
    {
        value = 0;
        var digits = 0;

        for (var i = start; i < text.Length && char.IsAsciiDigit(text[i]); i++)
        {
            value = (value * 10) + (text[i] - '0');
            digits++;

            if (digits > 9)
            {
                return false;
            }
        }

        return digits > 0;
    }

    // ---------------------------------------------------------------------------------------
    // Matching
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Reduces a snippet to the trimmed, non-empty lines to look for, optionally dropping a
    /// leading <c>+</c> or <c>-</c> — but only when every line has one, so that a line of code
    /// genuinely starting with an operator is left alone.
    /// </summary>
    private static List<string> NormalizeSnippet(string codeSnippet, bool stripDiffMarkers)
    {
        var raw = codeSnippet.Split('\n');
        var trimmed = new List<string>(raw.Length);
        var allMarked = true;

        for (var i = 0; i < raw.Length; i++)
        {
            var value = raw[i].Trim();

            if (value.Length == 0)
            {
                continue;
            }

            if (value[0] is not ('+' or '-'))
            {
                allMarked = false;
            }

            trimmed.Add(value);
        }

        if (!stripDiffMarkers)
        {
            return trimmed;
        }

        if (!allMarked || trimmed.Count == 0)
        {
            // Nothing to strip: the retry would repeat the attempt that already failed.
            return [];
        }

        var unmarked = new List<string>(trimmed.Count);

        for (var i = 0; i < trimmed.Count; i++)
        {
            var value = trimmed[i][1..].Trim();

            if (value.Length > 0)
            {
                unmarked.Add(value);
            }
        }

        return unmarked;
    }

    /// <summary>
    /// Finds every place the snippet's lines match consecutive diff lines of a single hunk.
    /// </summary>
    private static List<Run> FindRuns(List<HunkLine> lines, List<string> needle, bool exact)
    {
        var runs = new List<Run>();
        var last = lines.Count - needle.Count;

        for (var i = 0; i <= last; i++)
        {
            var hunk = lines[i].Hunk;
            var matched = true;

            for (var j = 0; j < needle.Count; j++)
            {
                var candidate = lines[i + j];

                if (candidate.Hunk != hunk || !Matches(candidate.Trimmed, needle[j], exact))
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                runs.Add(new Run(i, i + needle.Count - 1));
            }
        }

        return runs;
    }

    private static bool Matches(string content, string needle, bool exact) =>
        exact
            ? string.Equals(content, needle, StringComparison.Ordinal)
            : content.Contains(needle, StringComparison.Ordinal);

    /// <summary>
    /// Decides which side a matched run anchors to. Removed lines force the old side, anything else
    /// the new side; a run containing both cannot be a single anchor.
    /// </summary>
    private static bool TryResolveSide(List<HunkLine> lines, Run run, out bool fromSide)
    {
        var added = false;
        var removed = false;

        for (var i = run.Start; i <= run.End; i++)
        {
            if (lines[i].Type == DiffLineType.Added)
            {
                added = true;
            }
            else if (lines[i].Type == DiffLineType.Removed)
            {
                removed = true;
            }
        }

        fromSide = removed;
        return !(added && removed);
    }

    private static int AnchorLine(List<HunkLine> lines, Run run, bool fromSide) => SideLine(lines[run.End], fromSide);

    private static int SideLine(in HunkLine line, bool fromSide) => fromSide ? line.OldLine : line.NewLine;

    private static int FindLine(List<HunkLine> lines, int number, bool fromSide)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (SideLine(lines[i], fromSide) == number)
            {
                return i;
            }
        }

        return -1;
    }

    private static string MatchedText(List<HunkLine> lines, Run run)
    {
        if (run.End == run.Start)
        {
            return lines[run.Start].Content;
        }

        var builder = new StringBuilder();

        for (var i = run.Start; i <= run.End; i++)
        {
            if (i > run.Start)
            {
                builder.Append('\n');
            }

            builder.Append(lines[i].Content);
        }

        return builder.ToString();
    }

    private static DiffFile? FindFile(IReadOnlyList<DiffFile> files, string path)
    {
        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];

            if (string.Equals(file.NewPath, path, StringComparison.Ordinal)
                || string.Equals(file.OldPath, path, StringComparison.Ordinal))
            {
                return file;
            }
        }

        // Second chance for a path the caller reshaped on the way in: a leading "./", a Windows
        // separator, or the wrong case on a case-insensitive checkout.
        var wanted = NormalizePath(path);

        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];

            if (SamePath(file.NewPath, wanted) || SamePath(file.OldPath, wanted))
            {
                return file;
            }
        }

        return null;
    }

    private static bool SamePath(string? candidate, string wanted) =>
        candidate is not null && string.Equals(NormalizePath(candidate), wanted, StringComparison.OrdinalIgnoreCase);

    private static string NormalizePath(string path)
    {
        var value = path.Trim().Replace('\\', '/');

        if (value.StartsWith("./", StringComparison.Ordinal))
        {
            value = value[2..];
        }

        return value.TrimStart('/');
    }

    // ---------------------------------------------------------------------------------------
    // Messages. These go straight back to the caller, so they name the parameter to change.
    // ---------------------------------------------------------------------------------------

    private static string UnknownPathMessage(IReadOnlyList<DiffFile> files, string path)
    {
        var message = new StringBuilder();
        message.Append(CultureInfo.InvariantCulture, $"This diff contains no file named {path}.");

        if (files.Count == 0)
        {
            message.Append(" The diff is empty — fetch it with getPullRequestDiff before commenting on a line.");
            return message.ToString();
        }

        message.Append(" It contains:");
        var listed = Math.Min(files.Count, MaxListedPaths);

        for (var i = 0; i < listed; i++)
        {
            message.Append("\n  ").Append(files[i].Path);
        }

        if (files.Count > listed)
        {
            message.Append(CultureInfo.InvariantCulture, $"\n  … and {files.Count - listed} more.");
        }

        message.Append("\nUse a path exactly as the diff spells it.");
        return message.ToString();
    }

    private static string NoLinesMessage(DiffFile file)
    {
        if (file.IsBinary)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{file.Path} is a binary file, so the diff has no lines to attach a comment to. Leave path, line and codeSnippet unset to comment on the pull request itself.");
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"The diff shown for {file.Path} contains no changed lines — its hunks were not included in this response. Re-run getPullRequestDiff with paths=[\"{file.Path}\"] to fetch them, then comment.");
    }

    private static string NoMatchMessage(DiffFile file, List<HunkLine> lines, List<string> needle)
    {
        var message = new StringBuilder();
        message.Append(CultureInfo.InvariantCulture, $"No line in the diff of {file.Path} matches that codeSnippet ({Excerpt(needle[0])}).");

        var nearest = FindNearest(lines, needle);

        if (nearest.Count > 0)
        {
            message.Append(" The closest lines it does show are:");

            for (var i = 0; i < nearest.Count; i++)
            {
                AppendCandidate(message, lines[nearest[i]]);
            }
        }
        else
        {
            message.Append(" Its diff starts with:");
            var listed = Math.Min(lines.Count, MaxNearestCandidates);

            for (var i = 0; i < listed; i++)
            {
                AppendCandidate(message, lines[i]);
            }
        }

        message.Append("\nCopy the line to comment on out of the diff verbatim, or pass line with the number above and the matching lineType.");
        return message.ToString();
    }

    private static string AmbiguousMessage(DiffFile file, List<HunkLine> lines, List<Run> matches)
    {
        var message = new StringBuilder();
        message.Append(CultureInfo.InvariantCulture, $"That codeSnippet matches {matches.Count} places in the diff of {file.Path}, so it does not say which one to comment on. Re-send the same codeSnippet together with line set to one of these numbers:");

        var listed = Math.Min(matches.Count, MaxAmbiguousCandidates);

        for (var i = 0; i < listed; i++)
        {
            AppendCandidate(message, lines[matches[i].End]);
        }

        if (matches.Count > listed)
        {
            message.Append(CultureInfo.InvariantCulture, $"\n  … and {matches.Count - listed} more.");
        }

        return message.ToString();
    }

    private static string LineNotShownMessage(DiffFile file, List<HunkLine> lines, int line, bool fromSide, string parameterName)
    {
        var which = fromSide ? "the file before the change" : "the file after the change";

        var message = new StringBuilder();
        message.Append(CultureInfo.InvariantCulture, $"{parameterName}={line} is not a line the diff of {file.Path} shows for {which}. A comment can only be anchored to a line the diff actually contains.");
        message.Append(CultureInfo.InvariantCulture, $"\n  Lines available after the change (lineType=ADDED or CONTEXT): {DescribeRanges(lines, fromSide: false)}");
        message.Append(CultureInfo.InvariantCulture, $"\n  Lines available before the change (lineType=REMOVED): {DescribeRanges(lines, fromSide: true)}");
        message.Append("\nEasier: pass codeSnippet with the text of the line instead of counting lines.");
        return message.ToString();
    }

    private static void AppendCandidate(StringBuilder message, in HunkLine line)
    {
        message.Append("\n  ");

        switch (line.Type)
        {
            case DiffLineType.Added:
                message.Append(CultureInfo.InvariantCulture, $"line {line.NewLine} after the change (lineType=ADDED)");
                break;

            case DiffLineType.Removed:
                message.Append(CultureInfo.InvariantCulture, $"line {line.OldLine} before the change (lineType=REMOVED)");
                break;

            default:
                message.Append(CultureInfo.InvariantCulture, $"line {line.NewLine} after the change (lineType=CONTEXT)");
                break;
        }

        message.Append(": ").Append(Excerpt(line.Content));
    }

    /// <summary>
    /// Ranks the file's lines by how much of the snippet's vocabulary they share, so a near miss —
    /// a stale copy, a reformatted line — points at the line the caller meant.
    /// </summary>
    private static List<int> FindNearest(List<HunkLine> lines, List<string> needle)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < needle.Count; i++)
        {
            CollectTokens(needle[i], tokens);
        }

        var best = new List<int>(MaxNearestCandidates);
        var scores = new List<int>(MaxNearestCandidates);
        var candidate = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < lines.Count; i++)
        {
            candidate.Clear();
            CollectTokens(lines[i].Trimmed, candidate);

            var score = 0;

            foreach (var token in candidate)
            {
                if (tokens.Contains(token))
                {
                    score += token.Length;
                }
            }

            if (score == 0)
            {
                continue;
            }

            var position = best.Count;

            while (position > 0 && scores[position - 1] < score)
            {
                position--;
            }

            if (position >= MaxNearestCandidates)
            {
                continue;
            }

            best.Insert(position, i);
            scores.Insert(position, score);

            if (best.Count > MaxNearestCandidates)
            {
                best.RemoveAt(best.Count - 1);
                scores.RemoveAt(scores.Count - 1);
            }
        }

        return best;
    }

    private static void CollectTokens(string text, HashSet<string> tokens)
    {
        var start = -1;

        for (var i = 0; i <= text.Length; i++)
        {
            var isToken = i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_');

            if (isToken)
            {
                if (start < 0)
                {
                    start = i;
                }

                continue;
            }

            if (start >= 0)
            {
                tokens.Add(text[start..i]);
                start = -1;
            }
        }
    }

    /// <summary>Summarises the line numbers one side of the diff covers, as <c>1-20, 44-70</c>.</summary>
    private static string DescribeRanges(List<HunkLine> lines, bool fromSide)
    {
        var ranges = new StringBuilder();
        var listed = 0;
        var start = -1;
        var previous = -1;

        for (var i = 0; i < lines.Count; i++)
        {
            var number = SideLine(lines[i], fromSide);

            if (number <= 0)
            {
                continue;
            }

            if (start < 0)
            {
                start = number;
            }
            else if (number != previous + 1)
            {
                AppendRange(ranges, start, previous, ref listed);
                start = number;
            }

            previous = number;
        }

        if (start >= 0)
        {
            AppendRange(ranges, start, previous, ref listed);
        }

        return ranges.Length == 0 ? "none" : ranges.ToString();
    }

    private static void AppendRange(StringBuilder ranges, int start, int end, ref int listed)
    {
        if (listed >= MaxListedRanges)
        {
            if (listed == MaxListedRanges)
            {
                ranges.Append(", …");
                listed++;
            }

            return;
        }

        if (ranges.Length > 0)
        {
            ranges.Append(", ");
        }

        if (start == end)
        {
            ranges.Append(CultureInfo.InvariantCulture, $"{start}");
        }
        else
        {
            ranges.Append(CultureInfo.InvariantCulture, $"{start}-{end}");
        }

        listed++;
    }

    private static string Excerpt(string content)
    {
        var trimmed = content.Trim();

        return trimmed.Length <= ExcerptLength
            ? trimmed
            : string.Concat(trimmed.AsSpan(0, ExcerptLength), "…");
    }

    /// <summary>One diff line, numbered on the side(s) it exists on. Zero means "not on this side".</summary>
    private readonly record struct HunkLine(
        DiffLineType Type,
        int OldLine,
        int NewLine,
        string Content,
        string Trimmed,
        int Hunk);

    /// <summary>An inclusive range of matched <see cref="HunkLine"/> indexes.</summary>
    private readonly record struct Run(int Start, int End);
}
