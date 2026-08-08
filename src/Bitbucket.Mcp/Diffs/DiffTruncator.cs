using System.Globalization;
using System.Text;

namespace Bitbucket.Mcp.Diffs;

/// <summary>
/// Cuts a parsed diff down to a per-file and a whole-response line budget.
/// </summary>
/// <remarks>
/// <para>
/// The one rule this type exists to enforce: <b>truncation is always visible</b>. A model that
/// silently receives half a file will review the half it can see and report the file as fine, so
/// every cut leaves exactly one marker line saying what was dropped and which call would fetch it.
/// </para>
/// <para>
/// The budget is spent on body lines only (see <see cref="TruncatedDiff"/>). Files are emitted in
/// diff order until the whole-response budget runs out; the rest are named only by count, because
/// listing them is what <c>mode="diffstat"</c> is for.
/// </para>
/// </remarks>
internal static class DiffTruncator
{
    /// <summary>
    /// Granularity of the suggested <c>maxLinesPerFile</c> in a marker. A round number reads as a
    /// knob to turn rather than as a magic constant.
    /// </summary>
    private const int SuggestionStep = 100;

    /// <summary>Renders <paramref name="files"/> within both budgets.</summary>
    /// <param name="files">The parsed diff, in order.</param>
    /// <param name="maxLinesPerFile">Body lines allowed per file. Negative is treated as zero.</param>
    /// <param name="maxTotalLines">Body lines allowed across the whole response. Negative is treated as zero.</param>
    internal static TruncatedDiff Truncate(IReadOnlyList<DiffFile> files, int maxLinesPerFile, int maxTotalLines)
    {
        ArgumentNullException.ThrowIfNull(files);

        var perFile = Math.Max(0, maxLinesPerFile);
        var remaining = Math.Max(0, maxTotalLines);

        var entries = new List<TruncatedDiffFile>(files.Count);
        var text = new StringBuilder();
        var linesShown = 0;
        var truncated = false;

        for (var f = 0; f < files.Count; f++)
        {
            if (remaining <= 0)
            {
                break;
            }

            var file = files[f];
            var total = file.Body.Count;
            var shown = Math.Min(Math.Min(perFile, remaining), total);

            var rendered = new StringBuilder();

            for (var i = 0; i < file.Header.Count; i++)
            {
                AppendLine(rendered, file.Header[i]);
            }

            for (var i = 0; i < shown; i++)
            {
                AppendLine(rendered, file.Body[i]);
            }

            var fileTruncated = shown < total;

            if (fileTruncated)
            {
                AppendLine(rendered, FileMarker(file.Path, shown, total));
                truncated = true;
            }

            var fileText = rendered.ToString();

            entries.Add(new TruncatedDiffFile
            {
                Path = file.Path,
                Status = file.Status,
                Text = fileText,
                Truncated = fileTruncated,
                LinesShown = shown,
                LinesTotal = total,
            });

            AppendLine(text, fileText);
            linesShown += shown;
            remaining -= shown;
        }

        string? notice = null;

        if (entries.Count < files.Count)
        {
            notice = FilesMarker(entries.Count, files.Count);
            AppendLine(text, notice);
            truncated = true;
        }

        return new TruncatedDiff
        {
            Text = text.ToString(),
            Truncated = truncated,
            FilesShown = entries.Count,
            FilesTotal = files.Count,
            LinesShown = linesShown,
            FilesOmittedNotice = notice,
            Files = entries,
        };
    }

    private static void AppendLine(StringBuilder builder, string line)
    {
        if (builder.Length > 0)
        {
            builder.Append('\n');
        }

        builder.Append(line);
    }

    private static string FileMarker(string path, int shown, int total) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"… [truncated: {shown} of {total} lines shown for {path} — re-run getPullRequestDiff with paths=[\"{path}\"] and maxLinesPerFile={SuggestLimit(total)}]");

    private static string FilesMarker(int filesShown, int filesTotal) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"… [truncated: {filesShown} of {filesTotal} files shown — re-run getPullRequestDiff with mode=\"diffstat\" to list all files, then request specific paths]");

    /// <summary>The smallest multiple of <see cref="SuggestionStep"/> that would fit the whole file.</summary>
    private static int SuggestLimit(int totalLines)
    {
        var steps = (totalLines + SuggestionStep - 1) / SuggestionStep;
        return Math.Max(1, steps) * SuggestionStep;
    }
}
