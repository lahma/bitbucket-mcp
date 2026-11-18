using System.Globalization;

using Bitbucket.Mcp.Diffs;


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
    [Test]
    public void NullFileListIsRejected() =>
        Assert.Throws<ArgumentNullException>(() => DiffTruncator.Truncate(null!, 400, 4000));

    [Test]
    public void EmptyDiffRendersToNothingAtAll()
    {
        var result = DiffTruncator.Truncate([], 400, 4000);

        Assert.That(result.Text, Is.EqualTo(string.Empty));
        Assert.That(result.Truncated, Is.False);
        Assert.That(result.FilesShown, Is.EqualTo(0));
        Assert.That(result.FilesTotal, Is.EqualTo(0));
        Assert.That(result.LinesShown, Is.EqualTo(0));
        Assert.That(result.FilesOmittedNotice, Is.Null);
        Assert.That(result.Files, Is.Empty);
    }

    [Test]
    public void GenerousBudgetsReproduceTheDiffVerbatimWithNoMarkers()
    {
        var raw = DiffFixtures.Read(DiffFixtures.MultiFile);
        var result = DiffTruncator.Truncate(UnifiedDiffParser.Split(raw), 1000, 10000);

        Assert.That(result.Truncated, Is.False);
        Assert.That(result.FilesShown, Is.EqualTo(7));
        Assert.That(result.FilesTotal, Is.EqualTo(7));
        Assert.That(result.LinesShown, Is.EqualTo(32)); // see the table in the class remarks
        Assert.That(result.FilesOmittedNotice, Is.Null);
        Assert.That(CountOccurrences(result.Text, "[truncated:"), Is.EqualTo(0));

        // Nothing is dropped and nothing is reordered: the rendering is the fixture itself, minus
        // only its trailing newline (files are joined with, not terminated by, a newline).
        Assert.That(result.Text, Is.EqualTo(raw[..^1]));
    }

    /// <summary>
    /// The one place the per-file marker is spelled out character for character. 4201 body lines
    /// (one <c>@@</c> header + 4200 added lines), capped at 400; the suggested re-run limit is the
    /// smallest multiple of 100 that would fit the whole file, so ceil(4201/100)*100 = 4300.
    /// </summary>
    [Test]
    public void PerFileMarkerTextIsExact()
    {
        var files = DiffFixtures.Split(DiffFixtures.LargeFile);
        var result = DiffTruncator.Truncate(files, 400, 4000);

        var file = Expect.Single(result.Files);

        Assert.That(file.Truncated, Is.True);
        Assert.That(file.LinesShown, Is.EqualTo(400));
        Assert.That(file.LinesTotal, Is.EqualTo(4201));
        Assert.That(file.Path, Is.EqualTo("src/Generated/BigTable.cs"));
        Assert.That(file.Status, Is.EqualTo(DiffFileStatus.Added));

        Assert.That(LastLine(file.Text), Is.EqualTo("… [truncated: 400 of 4201 lines shown for src/Generated/BigTable.cs — re-run getPullRequestDiff with paths=[\"src/Generated/BigTable.cs\"] and maxLinesPerFile=4300]"));

        // Exactly one marker, for exactly the one file that was cut.
        Assert.That(CountOccurrences(file.Text, "[truncated:"), Is.EqualTo(1));
        Assert.That(CountOccurrences(result.Text, "[truncated:"), Is.EqualTo(1));

        Assert.That(result.Truncated, Is.True);
        Assert.That(result.LinesShown, Is.EqualTo(400));
        Assert.That(result.FilesShown, Is.EqualTo(1));
        Assert.That(result.FilesTotal, Is.EqualTo(1));
        Assert.That(result.FilesOmittedNotice, Is.Null); // every file was emitted; only its tail was cut
    }

    [Test]
    public void HeadersAreExemptFromTheBudgetAndFromTruncation()
    {
        var calculator = DiffFixtures.Split(DiffFixtures.MultiFile)[1];
        var result = DiffTruncator.Truncate([calculator], 0, 4000);

        var file = Expect.Single(result.Files);
        var lines = file.Text.Split('\n');

        // 4 header lines survive a zero-line budget, followed by the marker and nothing else.
        Assert.That(lines.Length, Is.EqualTo(5));
        Assert.That(lines[..4], Is.EqualTo(calculator.Header.ToArray()));
        Assert.That(lines[4], Is.EqualTo(ExpectedFileMarker("src/Calculator.cs", 0, 17, 100)));
        Assert.That(result.LinesShown, Is.EqualTo(0));
        Assert.That(file.LinesShown, Is.EqualTo(0));
        Assert.That(file.LinesTotal, Is.EqualTo(17));
    }

    [Test]
    public void GlobalBudgetAlsoCutsInsideAFile()
    {
        // Per-file budget 5000 would fit all 4201 lines; the 4000-line response budget does not.
        var result = DiffTruncator.Truncate(DiffFixtures.Split(DiffFixtures.LargeFile), 5000, 4000);

        var file = Expect.Single(result.Files);

        Assert.That(file.Truncated, Is.True);
        Assert.That(file.LinesShown, Is.EqualTo(4000));
        Assert.That(result.LinesShown, Is.EqualTo(4000));
        Assert.That(LastLine(file.Text), Is.EqualTo(ExpectedFileMarker("src/Generated/BigTable.cs", 4000, 4201, 4300)));
    }

    /// <summary>
    /// The one place the whole-response marker is spelled out character for character.
    /// </summary>
    /// <remarks>
    /// With a 20-line response budget over the multi-file fixture: files[0] spends 0 (no body),
    /// files[1] spends all 17 of its lines, files[2] gets the remaining 3 of its 5 and is cut, and
    /// the budget is gone before files[3] — so 3 of 7 files are shown and 20 lines emitted.
    /// </remarks>
    [Test]
    public void GlobalMarkerTextIsExactWhenFilesAreDroppedEntirely()
    {
        var result = DiffTruncator.Truncate(DiffFixtures.Split(DiffFixtures.MultiFile), 100, 20);

        Assert.That(result.Truncated, Is.True);
        Assert.That(result.FilesShown, Is.EqualTo(3));
        Assert.That(result.FilesTotal, Is.EqualTo(7));
        Assert.That(result.LinesShown, Is.EqualTo(20));
        Assert.That(result.Files.Count, Is.EqualTo(3));

        Assert.That(result.FilesOmittedNotice, Is.EqualTo("… [truncated: 3 of 7 files shown — re-run getPullRequestDiff with mode=\"diffstat\" to list all files, then request specific paths]"));
        Assert.That(LastLine(result.Text), Is.EqualTo(result.FilesOmittedNotice));
    }

    [Test]
    public void OnlyTheCutFileCarriesAMarkerAndItCarriesExactlyOne()
    {
        var result = DiffTruncator.Truncate(DiffFixtures.Split(DiffFixtures.MultiFile), 100, 20);

        Assert.That(result.Files[0].Truncated, Is.False); // preamble: no body to cut
        Assert.That(CountOccurrences(result.Files[0].Text, "[truncated:"), Is.EqualTo(0));

        Assert.That(result.Files[1].Truncated, Is.False); // all 17 lines fitted
        Assert.That(result.Files[1].LinesShown, Is.EqualTo(17));
        Assert.That(CountOccurrences(result.Files[1].Text, "[truncated:"), Is.EqualTo(0));

        Assert.That(result.Files[2].Truncated, Is.True);
        Assert.That(result.Files[2].LinesShown, Is.EqualTo(3));
        Assert.That(result.Files[2].LinesTotal, Is.EqualTo(5));
        Assert.That(CountOccurrences(result.Files[2].Text, "[truncated:"), Is.EqualTo(1));
        Assert.That(LastLine(result.Files[2].Text), Is.EqualTo(ExpectedFileMarker("docs/new notes.md", 3, 5, 100)));

        // One per-file marker plus the one whole-response notice, and no others.
        Assert.That(CountOccurrences(result.Text, "[truncated:"), Is.EqualTo(2));
    }

    [Test]
    public void TextIsTheFileTextsAndTheNoticeJoinedByNewlines()
    {
        var result = DiffTruncator.Truncate(DiffFixtures.Split(DiffFixtures.MultiFile), 100, 20);

        var parts = result.Files.Select(static file => file.Text).ToList();

        Assert.That(result.FilesOmittedNotice, Is.Not.Null);
        parts.Add(result.FilesOmittedNotice);

        Assert.That(result.Text, Is.EqualTo(string.Join('\n', parts)));
    }

    [Test]
    public void BinaryPatchBodyIsTruncatedLikeAnyOtherBody()
    {
        // The "GIT binary patch" line and its 304 followers are body, so the cap reaches them.
        var result = DiffTruncator.Truncate(DiffFixtures.Split(DiffFixtures.BinaryPatch), 50, 4000);

        var file = Expect.Single(result.Files);

        Assert.That(file.Truncated, Is.True);
        Assert.That(file.LinesShown, Is.EqualTo(50));
        Assert.That(file.LinesTotal, Is.EqualTo(305));
        Assert.That(LastLine(file.Text), Is.EqualTo(ExpectedFileMarker("assets/sprite.bin", 50, 305, 400)));
    }

    [Test]
    public void NegativeBudgetsAreTreatedAsZero()
    {
        var result = DiffTruncator.Truncate(DiffFixtures.Split(DiffFixtures.MultiFile), -5, -5);

        Assert.That(result.Truncated, Is.True);
        Assert.That(result.Files, Is.Empty);
        Assert.That(result.FilesShown, Is.EqualTo(0));
        Assert.That(result.FilesTotal, Is.EqualTo(7));
        Assert.That(result.LinesShown, Is.EqualTo(0));
        Assert.That(result.FilesOmittedNotice, Is.EqualTo("… [truncated: 0 of 7 files shown — re-run getPullRequestDiff with mode=\"diffstat\" to list all files, then request specific paths]"));
        Assert.That(result.Text, Is.EqualTo(result.FilesOmittedNotice));
    }

    /// <summary>
    /// The suggested <c>maxLinesPerFile</c> is always the smallest multiple of 100 that covers the
    /// whole file: 17 -&gt; 100, 305 -&gt; 400, 4201 -&gt; 4300.
    /// </summary>
    [TestCase(DiffFixtures.MultiFile, 1, "src/Calculator.cs", 17, 100)]
    [TestCase(DiffFixtures.BinaryPatch, 0, "assets/sprite.bin", 305, 400)]
    [TestCase(DiffFixtures.LargeFile, 0, "src/Generated/BigTable.cs", 4201, 4300)]
    public void SuggestedLimitRoundsUpToTheNextHundred(
        string fixture,
        int index,
        string path,
        int totalLines,
        int expectedSuggestion)
    {
        var file = DiffFixtures.Split(fixture)[index];
        var result = DiffTruncator.Truncate([file], 1, 4000);

        Assert.That(result.Files[0].Path, Is.EqualTo(path));
        Assert.That(result.Files[0].LinesTotal, Is.EqualTo(totalLines));
        Assert.That(LastLine(result.Files[0].Text), Is.EqualTo(ExpectedFileMarker(path, 1, totalLines, expectedSuggestion)));
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
