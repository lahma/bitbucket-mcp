using System.Globalization;

using Bitbucket.Mcp.Diffs;

using Xunit;

namespace Bitbucket.Mcp.Tests.Diffs;

/// <summary>
/// Budget arithmetic and marker text of <see cref="DiffTruncator"/>.
/// </summary>
/// <remarks>
/// Body-line counts of <c>Fixtures/diff-multi-file.diff</c>, counted by hand from the fixture (a
/// file's body starts at its first <c>@@</c> or binary notice; headers are neither counted nor
/// truncated):
/// <code>
/// files[0] "(unknown)"          preamble, no body                   0
/// files[1] src/Calculator.cs    2 hunk headers + 15 content lines   17
/// files[2] docs/new notes.md    1 hunk header  +  4 content lines    5
/// files[3] scripts/legacy.sh    1 hunk header  +  3 removed lines    4
/// files[4] src/Version.txt      1 hunk header  +  2 added + "\ No"   4
/// files[5] assets/logo.png      1 binary notice                      1
/// files[6] assets/icon.ico      1 binary notice                      1
///                                                          total    32
/// </code>
/// </remarks>
public sealed class DiffTruncatorTests
{
    [Fact]
    public void NullFileListIsRejected() =>
        Assert.Throws<ArgumentNullException>(() => DiffTruncator.Truncate(null!, 400, 4000));

    [Fact]
    public void EmptyDiffRendersToNothingAtAll()
    {
        var result = DiffTruncator.Truncate([], 400, 4000);

        Assert.Equal(string.Empty, result.Text);
        Assert.False(result.Truncated);
        Assert.Equal(0, result.FilesShown);
        Assert.Equal(0, result.FilesTotal);
        Assert.Equal(0, result.LinesShown);
        Assert.Null(result.FilesOmittedNotice);
        Assert.Empty(result.Files);
    }

    [Fact]
    public void GenerousBudgetsReproduceTheDiffVerbatimWithNoMarkers()
    {
        var raw = DiffFixtures.Read(DiffFixtures.MultiFile);
        var result = DiffTruncator.Truncate(UnifiedDiffParser.Split(raw), 1000, 10000);

        Assert.False(result.Truncated);
        Assert.Equal(7, result.FilesShown);
        Assert.Equal(7, result.FilesTotal);
        Assert.Equal(32, result.LinesShown); // see the table in the class remarks
        Assert.Null(result.FilesOmittedNotice);
        Assert.Equal(0, CountOccurrences(result.Text, "[truncated:"));

        // Nothing is dropped and nothing is reordered: the rendering is the fixture itself, minus
        // only its trailing newline (files are joined with, not terminated by, a newline).
        Assert.Equal(raw[..^1], result.Text);
    }

    /// <summary>
    /// The one place the per-file marker is spelled out character for character. 4201 body lines
    /// (one <c>@@</c> header + 4200 added lines), capped at 400; the suggested re-run limit is the
    /// smallest multiple of 100 that would fit the whole file, so ceil(4201/100)*100 = 4300.
    /// </summary>
    [Fact]
    public void PerFileMarkerTextIsExact()
    {
        var files = DiffFixtures.Split(DiffFixtures.LargeFile);
        var result = DiffTruncator.Truncate(files, 400, 4000);

        var file = Assert.Single(result.Files);

        Assert.True(file.Truncated);
        Assert.Equal(400, file.LinesShown);
        Assert.Equal(4201, file.LinesTotal);
        Assert.Equal("src/Generated/BigTable.cs", file.Path);
        Assert.Equal(DiffFileStatus.Added, file.Status);

        Assert.Equal(
            "… [truncated: 400 of 4201 lines shown for src/Generated/BigTable.cs — re-run getPullRequestDiff with paths=[\"src/Generated/BigTable.cs\"] and maxLinesPerFile=4300]",
            LastLine(file.Text));

        // Exactly one marker, for exactly the one file that was cut.
        Assert.Equal(1, CountOccurrences(file.Text, "[truncated:"));
        Assert.Equal(1, CountOccurrences(result.Text, "[truncated:"));

        Assert.True(result.Truncated);
        Assert.Equal(400, result.LinesShown);
        Assert.Equal(1, result.FilesShown);
        Assert.Equal(1, result.FilesTotal);
        Assert.Null(result.FilesOmittedNotice); // every file was emitted; only its tail was cut
    }

    [Fact]
    public void HeadersAreExemptFromTheBudgetAndFromTruncation()
    {
        var calculator = DiffFixtures.Split(DiffFixtures.MultiFile)[1];
        var result = DiffTruncator.Truncate([calculator], 0, 4000);

        var file = Assert.Single(result.Files);
        var lines = file.Text.Split('\n');

        // 4 header lines survive a zero-line budget, followed by the marker and nothing else.
        Assert.Equal(5, lines.Length);
        Assert.Equal(calculator.Header.ToArray(), lines[..4]);
        Assert.Equal(ExpectedFileMarker("src/Calculator.cs", 0, 17, 100), lines[4]);
        Assert.Equal(0, result.LinesShown);
        Assert.Equal(0, file.LinesShown);
        Assert.Equal(17, file.LinesTotal);
    }

    [Fact]
    public void GlobalBudgetAlsoCutsInsideAFile()
    {
        // Per-file budget 5000 would fit all 4201 lines; the 4000-line response budget does not.
        var result = DiffTruncator.Truncate(DiffFixtures.Split(DiffFixtures.LargeFile), 5000, 4000);

        var file = Assert.Single(result.Files);

        Assert.True(file.Truncated);
        Assert.Equal(4000, file.LinesShown);
        Assert.Equal(4000, result.LinesShown);
        Assert.Equal(
            ExpectedFileMarker("src/Generated/BigTable.cs", 4000, 4201, 4300),
            LastLine(file.Text));
    }

    /// <summary>
    /// The one place the whole-response marker is spelled out character for character.
    /// </summary>
    /// <remarks>
    /// With a 20-line response budget over the multi-file fixture: files[0] spends 0 (no body),
    /// files[1] spends all 17 of its lines, files[2] gets the remaining 3 of its 5 and is cut, and
    /// the budget is gone before files[3] — so 3 of 7 files are shown and 20 lines emitted.
    /// </remarks>
    [Fact]
    public void GlobalMarkerTextIsExactWhenFilesAreDroppedEntirely()
    {
        var result = DiffTruncator.Truncate(DiffFixtures.Split(DiffFixtures.MultiFile), 100, 20);

        Assert.True(result.Truncated);
        Assert.Equal(3, result.FilesShown);
        Assert.Equal(7, result.FilesTotal);
        Assert.Equal(20, result.LinesShown);
        Assert.Equal(3, result.Files.Count);

        Assert.Equal(
            "… [truncated: 3 of 7 files shown — re-run getPullRequestDiff with mode=\"diffstat\" to list all files, then request specific paths]",
            result.FilesOmittedNotice);
        Assert.Equal(result.FilesOmittedNotice, LastLine(result.Text));
    }

    [Fact]
    public void OnlyTheCutFileCarriesAMarkerAndItCarriesExactlyOne()
    {
        var result = DiffTruncator.Truncate(DiffFixtures.Split(DiffFixtures.MultiFile), 100, 20);

        Assert.False(result.Files[0].Truncated); // preamble: no body to cut
        Assert.Equal(0, CountOccurrences(result.Files[0].Text, "[truncated:"));

        Assert.False(result.Files[1].Truncated); // all 17 lines fitted
        Assert.Equal(17, result.Files[1].LinesShown);
        Assert.Equal(0, CountOccurrences(result.Files[1].Text, "[truncated:"));

        Assert.True(result.Files[2].Truncated);
        Assert.Equal(3, result.Files[2].LinesShown);
        Assert.Equal(5, result.Files[2].LinesTotal);
        Assert.Equal(1, CountOccurrences(result.Files[2].Text, "[truncated:"));
        Assert.Equal(
            ExpectedFileMarker("docs/new notes.md", 3, 5, 100),
            LastLine(result.Files[2].Text));

        // One per-file marker plus the one whole-response notice, and no others.
        Assert.Equal(2, CountOccurrences(result.Text, "[truncated:"));
    }

    [Fact]
    public void TextIsTheFileTextsAndTheNoticeJoinedByNewlines()
    {
        var result = DiffTruncator.Truncate(DiffFixtures.Split(DiffFixtures.MultiFile), 100, 20);

        var parts = result.Files.Select(static file => file.Text).ToList();

        Assert.NotNull(result.FilesOmittedNotice);
        parts.Add(result.FilesOmittedNotice);

        Assert.Equal(string.Join('\n', parts), result.Text);
    }

    [Fact]
    public void BinaryPatchBodyIsTruncatedLikeAnyOtherBody()
    {
        // The "GIT binary patch" line and its 304 followers are body, so the cap reaches them.
        var result = DiffTruncator.Truncate(DiffFixtures.Split(DiffFixtures.BinaryPatch), 50, 4000);

        var file = Assert.Single(result.Files);

        Assert.True(file.Truncated);
        Assert.Equal(50, file.LinesShown);
        Assert.Equal(305, file.LinesTotal);
        Assert.Equal(ExpectedFileMarker("assets/sprite.bin", 50, 305, 400), LastLine(file.Text));
    }

    [Fact]
    public void NegativeBudgetsAreTreatedAsZero()
    {
        var result = DiffTruncator.Truncate(DiffFixtures.Split(DiffFixtures.MultiFile), -5, -5);

        Assert.True(result.Truncated);
        Assert.Empty(result.Files);
        Assert.Equal(0, result.FilesShown);
        Assert.Equal(7, result.FilesTotal);
        Assert.Equal(0, result.LinesShown);
        Assert.Equal(
            "… [truncated: 0 of 7 files shown — re-run getPullRequestDiff with mode=\"diffstat\" to list all files, then request specific paths]",
            result.FilesOmittedNotice);
        Assert.Equal(result.FilesOmittedNotice, result.Text);
    }

    /// <summary>
    /// The suggested <c>maxLinesPerFile</c> is always the smallest multiple of 100 that covers the
    /// whole file: 17 -&gt; 100, 305 -&gt; 400, 4201 -&gt; 4300.
    /// </summary>
    [Theory]
    [InlineData(DiffFixtures.MultiFile, 1, "src/Calculator.cs", 17, 100)]
    [InlineData(DiffFixtures.BinaryPatch, 0, "assets/sprite.bin", 305, 400)]
    [InlineData(DiffFixtures.LargeFile, 0, "src/Generated/BigTable.cs", 4201, 4300)]
    public void SuggestedLimitRoundsUpToTheNextHundred(
        string fixture,
        int index,
        string path,
        int totalLines,
        int expectedSuggestion)
    {
        var file = DiffFixtures.Split(fixture)[index];
        var result = DiffTruncator.Truncate([file], 1, 4000);

        Assert.Equal(path, result.Files[0].Path);
        Assert.Equal(totalLines, result.Files[0].LinesTotal);
        Assert.Equal(
            ExpectedFileMarker(path, 1, totalLines, expectedSuggestion),
            LastLine(result.Files[0].Text));
    }

    private static string ExpectedFileMarker(string path, int shown, int total, int suggestion) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"… [truncated: {shown} of {total} lines shown for {path} — re-run getPullRequestDiff with paths=[\"{path}\"] and maxLinesPerFile={suggestion}]");

    private static string LastLine(string text) => text.Split('\n')[^1];

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = text.IndexOf(value, StringComparison.Ordinal);

        while (index >= 0)
        {
            count++;
            index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal);
        }

        return count;
    }
}
