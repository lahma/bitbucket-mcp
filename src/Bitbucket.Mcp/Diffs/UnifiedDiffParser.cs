using System.Text;

namespace Bitbucket.Mcp.Diffs;

/// <summary>
/// Splits the raw text of a unified diff into one <see cref="DiffFile"/> per file.
/// </summary>
/// <remarks>
/// <para>
/// The input is whatever Bitbucket's <c>/diff</c> endpoint returned, so the parser is tolerant by
/// design: it never throws, it accepts LF and CRLF, and anything it cannot attribute to a
/// <c>diff --git</c> header — a leading commit message, output produced with <c>--no-prefix</c>, a
/// plain <c>diff -u</c> — still comes back as a <see cref="DiffFile"/> rather than disappearing.
/// Losing a hunk silently would mean an inline comment anchored to a line nobody can see.
/// </para>
/// <para>
/// File paths are read from the most reliable source available, in order: the <c>rename from</c> /
/// <c>rename to</c> headers, then the <c>---</c> / <c>+++</c> lines, and only then the
/// <c>diff --git</c> line. That order matters because the <c>diff --git</c> line is genuinely
/// ambiguous — git does not quote a path that merely contains spaces, so <c>a/x y b/z</c> could
/// split in more than one place — while the other three carry exactly one path per line.
/// </para>
/// </remarks>
internal static class UnifiedDiffParser
{
    private const string GitHeaderPrefix = "diff --git ";
    private const string DevNull = "/dev/null";

    /// <summary>Splits a raw unified diff into its files, in the order they appear.</summary>
    /// <param name="rawDiff">The diff text. <see langword="null"/>, empty or blank yields no files.</param>
    internal static IReadOnlyList<DiffFile> Split(string? rawDiff)
    {
        if (string.IsNullOrWhiteSpace(rawDiff))
        {
            return [];
        }

        var lines = SplitLines(rawDiff);
        var files = new List<DiffFile>();
        var start = 0;

        for (var i = 0; i < lines.Count; i++)
        {
            if (!Starts(lines[i], GitHeaderPrefix))
            {
                continue;
            }

            // The first header at index 0 opens the first file; anything before it is a preamble
            // that becomes an unnamed file of its own.
            if (i > start)
            {
                AddFile(files, lines, start, i);
            }

            start = i;
        }

        AddFile(files, lines, start, lines.Count);
        return files;
    }

    /// <summary>Splits on LF, dropping a preceding CR so CRLF input behaves like LF input.</summary>
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
            var end = text[^1] == '\r' ? text.Length - 1 : text.Length;
            lines.Add(text[start..end]);
        }

        return lines;
    }

    /// <summary>Adds a chunk as a file, skipping one that is empty or only whitespace.</summary>
    private static void AddFile(List<DiffFile> files, List<string> lines, int start, int end)
    {
        for (var i = start; i < end; i++)
        {
            if (!string.IsNullOrWhiteSpace(lines[i]))
            {
                files.Add(BuildFile(lines, start, end));
                return;
            }
        }
    }

    private static DiffFile BuildFile(List<string> lines, int start, int end)
    {
        var bodyStart = end;

        for (var i = start; i < end; i++)
        {
            if (IsContentStart(lines[i]))
            {
                bodyStart = i;
                break;
            }
        }

        string? gitOld = null;
        string? gitNew = null;
        string? namedOld = null;
        string? namedNew = null;
        string? minus = null;
        string? plus = null;
        var renamed = false;
        var copied = false;
        var added = false;
        var removed = false;

        for (var i = start; i < bodyStart; i++)
        {
            var line = lines[i];

            if (i == start && Starts(line, GitHeaderPrefix))
            {
                ParseGitHeaderPaths(line[GitHeaderPrefix.Length..], out gitOld, out gitNew);
            }
            else if (Starts(line, "new file mode "))
            {
                added = true;
            }
            else if (Starts(line, "deleted file mode "))
            {
                removed = true;
            }
            else if (Starts(line, "rename from "))
            {
                namedOld = Unquote(line["rename from ".Length..]);
                renamed = true;
            }
            else if (Starts(line, "rename to "))
            {
                namedNew = Unquote(line["rename to ".Length..]);
                renamed = true;
            }
            else if (Starts(line, "copy from "))
            {
                namedOld = Unquote(line["copy from ".Length..]);
                copied = true;
            }
            else if (Starts(line, "copy to "))
            {
                namedNew = Unquote(line["copy to ".Length..]);
                copied = true;
            }
            else if (Starts(line, "--- "))
            {
                minus = ParseSideMarker(line, 'a');
            }
            else if (Starts(line, "+++ "))
            {
                plus = ParseSideMarker(line, 'b');
            }
        }

        var binary = bodyStart < end
            && (Starts(lines[bodyStart], "Binary files ") || Starts(lines[bodyStart], "GIT binary patch"));

        if (string.Equals(minus, DevNull, StringComparison.Ordinal))
        {
            added = true;
        }

        if (string.Equals(plus, DevNull, StringComparison.Ordinal))
        {
            removed = true;
        }

        // A copy creates a file that did not exist before, so it reads as an addition; only a true
        // rename gets Renamed, because only then does the old path stop existing.
        var status = renamed ? DiffFileStatus.Renamed
            : added || copied ? DiffFileStatus.Added
            : removed ? DiffFileStatus.Removed
            : binary ? DiffFileStatus.Binary
            : DiffFileStatus.Modified;

        var oldPath = namedOld ?? RealPath(minus) ?? gitOld;
        var newPath = namedNew ?? RealPath(plus) ?? gitNew;

        if (status == DiffFileStatus.Added)
        {
            oldPath = null;
        }
        else if (status == DiffFileStatus.Removed)
        {
            newPath = null;
        }

        return new DiffFile
        {
            OldPath = oldPath,
            NewPath = newPath,
            Status = status,
            IsBinary = binary,
            Header = Slice(lines, start, bodyStart),
            Body = Slice(lines, bodyStart, end),
        };
    }

    /// <summary>
    /// Whether a line ends the header and starts the content. A binary notice counts, so that a
    /// literal binary patch lands in the body where the truncator can cap it.
    /// </summary>
    private static bool IsContentStart(string line) =>
        Starts(line, "@@") || Starts(line, "Binary files ") || Starts(line, "GIT binary patch");

    /// <summary>
    /// Reads both paths out of the text following <c>diff --git </c>.
    /// </summary>
    /// <remarks>
    /// Unquoted paths are ambiguous when they contain spaces, so the common case is resolved first:
    /// if splitting the text down the middle yields two identical paths, that is the split git
    /// meant. Only a rename or a copy — where the two paths differ — falls through to the
    /// best-effort <c> b/</c> search, and those carry unambiguous <c>rename from</c> /
    /// <c>rename to</c> headers that override whatever this produces.
    /// </remarks>
    private static void ParseGitHeaderPaths(string rest, out string? oldPath, out string? newPath)
    {
        oldPath = null;
        newPath = null;

        if (rest.Length == 0)
        {
            return;
        }

        if (rest[0] == '"' && TryReadQuoted(rest, 0, out var quotedOld, out var afterOld)
            && afterOld < rest.Length && rest[afterOld] == ' ')
        {
            oldPath = StripPrefix(quotedOld, 'a');
            newPath = StripPrefix(Unquote(rest[(afterOld + 1)..]), 'b');
            return;
        }

        var middle = (rest.Length - 1) / 2;

        if (middle > 0 && rest[middle] == ' ')
        {
            var left = StripPrefix(rest[..middle], 'a');
            var right = StripPrefix(rest[(middle + 1)..], 'b');

            if (string.Equals(left, right, StringComparison.Ordinal))
            {
                oldPath = left;
                newPath = right;
                return;
            }
        }

        var marker = rest.IndexOf(" b/", StringComparison.Ordinal);

        if (marker > 0 && Starts(rest, "a/"))
        {
            oldPath = Unquote(rest[2..marker]);
            newPath = Unquote(rest[(marker + 3)..]);
            return;
        }

        var space = rest.IndexOf(' ');

        if (space > 0 && space < rest.Length - 1)
        {
            oldPath = StripPrefix(Unquote(rest[..space]), 'a');
            newPath = StripPrefix(Unquote(rest[(space + 1)..]), 'b');
        }
    }

    /// <summary>
    /// Reads the path out of a <c>--- </c> or <c>+++ </c> line, keeping <c>/dev/null</c> intact so
    /// the caller can tell "the file did not exist" from "there was no such line".
    /// </summary>
    private static string ParseSideMarker(string line, char prefix)
    {
        var rest = line[4..];

        // POSIX diff appends a tab and a timestamp; git does not.
        var tab = rest.IndexOf('\t');

        if (tab >= 0)
        {
            rest = rest[..tab];
        }

        rest = Unquote(rest.TrimEnd());

        return string.Equals(rest, DevNull, StringComparison.Ordinal) ? DevNull : StripPrefix(rest, prefix);
    }

    /// <summary>Maps <c>/dev/null</c> and the absence of a marker alike to <see langword="null"/>.</summary>
    private static string? RealPath(string? marker) =>
        marker is null || string.Equals(marker, DevNull, StringComparison.Ordinal) || marker.Length == 0
            ? null
            : marker;

    /// <summary>Drops the <c>a/</c> or <c>b/</c> prefix; input produced with <c>--no-prefix</c> has none.</summary>
    private static string StripPrefix(string path, char prefix) =>
        path.Length > 2 && path[0] == prefix && path[1] == '/' ? path[2..] : path;

    /// <summary>
    /// Decodes git's C-style quoting — a quoted path whose control characters, quotes, backslashes
    /// and non-ASCII bytes are backslash- or octal-escaped. Text that is not quoted comes back
    /// unchanged.
    /// </summary>
    private static string Unquote(string value) =>
        value.Length > 1 && value[0] == '"' && TryReadQuoted(value, 0, out var decoded, out _) ? decoded : value;

    private static bool TryReadQuoted(string value, int start, out string decoded, out int next)
    {
        decoded = string.Empty;
        next = start;

        if (start >= value.Length || value[start] != '"')
        {
            return false;
        }

        var bytes = new List<byte>(value.Length - start);
        Span<char> chars = stackalloc char[2];
        Span<byte> utf8 = stackalloc byte[8];
        var i = start + 1;

        while (i < value.Length)
        {
            var c = value[i];

            if (c == '"')
            {
                decoded = Encoding.UTF8.GetString(bytes.ToArray());
                next = i + 1;
                return true;
            }

            if (c == '\\' && i + 1 < value.Length)
            {
                i++;
                var escape = value[i];

                if (escape is >= '0' and <= '7')
                {
                    var octal = 0;
                    var digits = 0;

                    while (digits < 3 && i < value.Length && value[i] is >= '0' and <= '7')
                    {
                        octal = (octal * 8) + (value[i] - '0');
                        i++;
                        digits++;
                    }

                    bytes.Add((byte) (octal & 0xFF));
                    continue;
                }

                bytes.Add(escape switch
                {
                    'a' => 0x07,
                    'b' => 0x08,
                    't' => 0x09,
                    'n' => 0x0A,
                    'v' => 0x0B,
                    'f' => 0x0C,
                    'r' => 0x0D,
                    _ => (byte) escape,
                });

                i++;
                continue;
            }

            if (c < 0x80)
            {
                bytes.Add((byte) c);
                i++;
                continue;
            }

            // git escapes non-ASCII bytes, so this only runs on hand-written input; encode the
            // character (with its low surrogate, if it has one) rather than mangling it.
            chars[0] = c;
            var length = 1;

            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                chars[1] = value[i + 1];
                length = 2;
            }

            var written = Encoding.UTF8.GetBytes(chars[..length], utf8);

            for (var k = 0; k < written; k++)
            {
                bytes.Add(utf8[k]);
            }

            i += length;
        }

        return false;
    }

    private static string[] Slice(List<string> lines, int start, int end)
    {
        if (end <= start)
        {
            return [];
        }

        var slice = new string[end - start];
        lines.CopyTo(start, slice, 0, slice.Length);
        return slice;
    }

    private static bool Starts(string line, string prefix) => line.StartsWith(prefix, StringComparison.Ordinal);
}
