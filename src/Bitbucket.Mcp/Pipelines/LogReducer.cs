using System.Globalization;
using System.Text;

namespace Bitbucket.Mcp.Pipelines;

/// <summary>
/// Cuts a pipeline step's log down to something a model can read, without ever holding the whole
/// log in memory.
/// </summary>
/// <remarks>
/// <para>
/// A Pipelines step runs under <c>set -e</c>: the last thing printed is the thing that broke, which
/// is why <see cref="TailAsync"/> is the default and why the tail is what the byte budget is spent
/// on. <see cref="SearchAsync"/> is the escape hatch for a failure that is not at the end.
/// </para>
/// <para>
/// <b>Memory is bounded by the budget, not by the log.</b> Bitbucket's own documentation calls
/// these files "potentially very large", and the tail path is reached with a whole log on the wire
/// whenever storage declines the <c>Range</c> header. Every method here therefore streams and keeps
/// at most a fixed ring; nothing reads to the end into a string.
/// </para>
/// <para>
/// The one rule, shared with <see cref="Diffs.DiffTruncator"/>: <b>truncation is always visible</b>.
/// Every cut leaves a marker in the text saying what was dropped and which call would fetch it.
/// </para>
/// </remarks>
internal static class LogReducer
{
    /// <summary>The escape character that introduces every ANSI sequence.</summary>
    private const char Escape = '\u001b';

    /// <summary>Bytes read from the network at a time. Unrelated to the budget.</summary>
    private const int ReadChunk = 16 * 1024;

    /// <summary>Reads the end of <paramref name="stream"/>, keeping at most the given budgets.</summary>
    /// <param name="stream">The log body. Consumed to the end — that is what makes the tail the tail.</param>
    /// <param name="maxLines">How many trailing lines to keep.</param>
    /// <param name="byteBudget">The ring size; the most that is ever held in memory.</param>
    /// <param name="rangeStart">
    /// The offset the body starts at, from <c>Content-Range</c>. Non-zero means the first line
    /// arrived cut in half and is dropped.
    /// </param>
    /// <param name="totalBytes">The log's full size when the server reported one.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    internal static async Task<LogExcerpt> TailAsync(
        Stream stream,
        int maxLines,
        long byteBudget,
        long rangeStart,
        long? totalBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var capacity = (int) Math.Clamp(byteBudget, 1, int.MaxValue);
        var ring = new byte[capacity];
        var written = 0L;
        var next = 0;

        var buffer = new byte[ReadChunk];
        int read;

        while ((read = await stream.ReadAsync(buffer.AsMemory(0, ReadChunk), cancellationToken).ConfigureAwait(false)) > 0)
        {
            // Only the last `capacity` bytes of a chunk can survive, so a chunk larger than the ring
            // is trimmed before it is copied rather than wrapping around several times.
            var offset = Math.Max(0, read - capacity);
            var count = read - offset;

            for (var i = 0; i < count; i++)
            {
                ring[next] = buffer[offset + i];
                next = next + 1 == capacity ? 0 : next + 1;
            }

            written += read;
        }

        var overflowed = written > capacity;
        var kept = (int) Math.Min(written, capacity);
        var ordered = new byte[kept];

        if (overflowed)
        {
            var start = next;

            for (var i = 0; i < kept; i++)
            {
                ordered[i] = ring[start];
                start = start + 1 == capacity ? 0 : start + 1;
            }
        }
        else
        {
            Array.Copy(ring, 0, ordered, 0, kept);
        }

        var lines = SplitLines(StripAnsi(Encoding.UTF8.GetString(ordered)));

        // The body began mid-line if the range started past zero, or if the ring dropped a prefix.
        var partialFirstLine = (rangeStart > 0 || overflowed) && lines.Count > 0;

        if (partialFirstLine)
        {
            lines.RemoveAt(0);
        }

        var droppedForLineCap = Math.Max(0, lines.Count - maxLines);

        if (droppedForLineCap > 0)
        {
            lines.RemoveRange(0, droppedForLineCap);
        }

        var truncated = partialFirstLine || droppedForLineCap > 0;
        var total = totalBytes ?? (rangeStart == 0 ? written : null);

        var builder = new StringBuilder();

        if (truncated)
        {
            builder.Append("... [showing the last ").Append(Count(lines.Count, "line", "lines")).Append(" of this step's log");

            if (total is { } size)
            {
                builder.Append(" — ").Append(Bytes(ordered.LongLength)).Append(" of ").Append(Bytes(size));
            }

            builder.Append(']').Append('\n');
        }

        for (var i = 0; i < lines.Count; i++)
        {
            builder.Append(lines[i]).Append('\n');
        }

        return new LogExcerpt
        {
            Text = builder.ToString(),
            Truncated = truncated,
            LinesShown = lines.Count,
            BytesShown = ordered.LongLength,
            TotalBytes = total,
            Hint = truncated
                ? "Earlier output was left out. Raise maxLines for more of the end, or pass pattern=\"...\" to "
                  + "search the whole log for the failing text."
                : null,
        };
    }

    /// <summary>Reads the beginning of <paramref name="stream"/> — for a step that died on startup.</summary>
    internal static async Task<LogExcerpt> HeadAsync(
        Stream stream,
        int maxLines,
        long byteBudget,
        long? totalBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var capacity = (int) Math.Clamp(byteBudget, 1, int.MaxValue);
        var collected = new byte[capacity];
        var filled = 0;

        var buffer = new byte[ReadChunk];
        int read;

        while (filled < capacity
               && (read = await stream.ReadAsync(buffer.AsMemory(0, ReadChunk), cancellationToken).ConfigureAwait(false)) > 0)
        {
            var count = Math.Min(read, capacity - filled);
            Array.Copy(buffer, 0, collected, filled, count);
            filled += count;
        }

        var lines = SplitLines(StripAnsi(Encoding.UTF8.GetString(collected, 0, filled)));
        var more = lines.Count > maxLines || filled == capacity;

        if (lines.Count > maxLines)
        {
            lines.RemoveRange(maxLines, lines.Count - maxLines);
        }

        var builder = new StringBuilder();

        for (var i = 0; i < lines.Count; i++)
        {
            builder.Append(lines[i]).Append('\n');
        }

        if (more)
        {
            builder.Append("... [truncated: showing the first ")
                .Append(Count(lines.Count, "line", "lines"))
                .Append(" of this step's log]")
                .Append('\n');
        }

        return new LogExcerpt
        {
            Text = builder.ToString(),
            Truncated = more,
            LinesShown = lines.Count,
            BytesShown = filled,
            TotalBytes = totalBytes,
            Hint = more
                ? "Only the start of the log is shown. Raise maxLines, or omit mode to read the end, where a "
                  + "failing step usually reports why."
                : null,
        };
    }

    /// <summary>
    /// Streams the whole log and keeps only the lines containing <paramref name="pattern"/>, with
    /// surrounding context.
    /// </summary>
    /// <remarks>
    /// <paramref name="pattern"/> is matched as a literal, case-insensitive substring and never as a
    /// regular expression: the argument arrives from a model, and an unbounded backtracking pattern
    /// over a multi-megabyte log is a denial of service against the server that fetched it.
    /// </remarks>
    internal static async Task<LogExcerpt> SearchAsync(
        Stream stream,
        string pattern,
        int contextLines,
        int maxLines,
        long? totalBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);

        var before = contextLines > 0 ? new string[contextLines] : [];
        var buffered = 0;
        var beforeNext = 0;

        var builder = new StringBuilder();
        var emitted = 0;
        var matches = 0;
        var after = 0;
        var lineNumber = 0;
        var lastEmittedLine = 0;
        var bytes = 0L;
        var capped = false;

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } raw)
        {
            lineNumber++;
            bytes += raw.Length + 1;

            var line = StripAnsi(raw);
            var isMatch = line.Contains(pattern, StringComparison.OrdinalIgnoreCase);

            if (isMatch)
            {
                matches++;
            }

            // Counting continues past the cap so the marker can say how many matches there really
            // were — "50 of 312" is actionable in a way that "50, and possibly more" is not.
            if (capped)
            {
                continue;
            }

            if (isMatch)
            {
                var contextStart = lineNumber - buffered;

                if (lastEmittedLine != 0 && contextStart > lastEmittedLine + 1)
                {
                    builder.Append("... [")
                        .Append(Count(contextStart - lastEmittedLine - 1, "line", "lines"))
                        .Append(" skipped]")
                        .Append('\n');
                }

                for (var i = 0; i < buffered; i++)
                {
                    var index = (beforeNext - buffered + i + before.Length) % before.Length;
                    builder.Append(before[index]).Append('\n');
                    emitted++;
                }

                buffered = 0;

                builder.Append(line).Append('\n');
                emitted++;
                lastEmittedLine = lineNumber;
                after = contextLines;
            }
            else if (after > 0)
            {
                builder.Append(line).Append('\n');
                emitted++;
                lastEmittedLine = lineNumber;
                after--;
            }
            else if (before.Length > 0)
            {
                before[beforeNext] = line;
                beforeNext = beforeNext + 1 == before.Length ? 0 : beforeNext + 1;
                buffered = Math.Min(buffered + 1, before.Length);
            }

            if (emitted >= maxLines)
            {
                capped = true;
            }
        }

        if (capped)
        {
            builder.Append("... [truncated: ")
                .Append(Count(emitted, "line", "lines"))
                .Append(" shown of ")
                .Append(Count(matches, "match", "matches"))
                .Append("; narrow pattern or raise maxLines]")
                .Append('\n');
        }
        else if (matches == 0)
        {
            builder.Append("[no line of this step's log contains \"").Append(pattern).Append("\"]").Append('\n');
        }

        return new LogExcerpt
        {
            Text = builder.ToString(),
            Truncated = capped,
            LinesShown = emitted,
            BytesShown = bytes,
            TotalBytes = totalBytes ?? bytes,
            MatchCount = matches,
            Hint = capped
                ? "More lines matched than were shown. Narrow pattern, or raise maxLines."
                : matches == 0
                    ? "Nothing matched. Omit pattern to read the end of the log, which is where a failing step "
                      + "usually reports why."
                    : null,
        };
    }

    /// <summary>Removes ANSI escape sequences.</summary>
    /// <remarks>
    /// Build tools colour their output, and a log full of <c>ESC[1;31m</c> spends the model's
    /// context on bytes it cannot act on. Hand-scanned rather than matched with a regular
    /// expression: it runs over every line of every log, and it keeps
    /// <c>System.Text.RegularExpressions</c> out of an AOT binary that has no other use for it.
    /// </remarks>
    internal static string StripAnsi(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.IndexOf(Escape) < 0)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);

        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != Escape || i + 1 >= value.Length)
            {
                builder.Append(value[i]);
                continue;
            }

            var next = value[i + 1];

            if (next == '[')
            {
                // CSI: ESC [ parameter bytes (0x30-0x3F) intermediates (0x20-0x2F) final (0x40-0x7E).
                var j = i + 2;

                while (j < value.Length && value[j] is >= ' ' and <= '?')
                {
                    j++;
                }

                if (j < value.Length && value[j] is >= '@' and <= '~')
                {
                    i = j;
                    continue;
                }

                // Unterminated: not an escape sequence after all, so keep the character as it is.
                builder.Append(value[i]);
                continue;
            }

            if (next == ']')
            {
                // OSC: ESC ] … terminated by BEL or by ESC \.
                var j = i + 2;

                while (j < value.Length)
                {
                    if (value[j] == '\a')
                    {
                        break;
                    }

                    if (value[j] == Escape && j + 1 < value.Length && value[j + 1] == '\\')
                    {
                        j++;
                        break;
                    }

                    j++;
                }

                i = Math.Min(j, value.Length - 1);
                continue;
            }

            // A two-character escape (ESC c, ESC =, …). Drop both.
            i++;
        }

        return builder.ToString();
    }

    /// <summary>Splits on LF, tolerating CRLF, and drops the empty tail a trailing newline leaves.</summary>
    private static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        var start = 0;

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n')
            {
                continue;
            }

            var end = i > start && text[i - 1] == '\r' ? i - 1 : i;
            lines.Add(text[start..end]);
            start = i + 1;
        }

        if (start < text.Length)
        {
            lines.Add(text[start..]);
        }

        return lines;
    }

    /// <summary>
    /// "1 line" / "1,234 lines". The plural is spelled out rather than derived, because the nouns
    /// this is called with do not all take a bare "s" — "matchs" is how a marker stops reading like
    /// a sentence someone wrote.
    /// </summary>
    private static string Count(int value, string singular, string plural) =>
        value == 1
            ? string.Create(CultureInfo.InvariantCulture, $"1 {singular}")
            : string.Create(CultureInfo.InvariantCulture, $"{value:N0} {plural}");

    private static string Bytes(long value) =>
        string.Create(CultureInfo.InvariantCulture, $"{value:N0} bytes");
}
