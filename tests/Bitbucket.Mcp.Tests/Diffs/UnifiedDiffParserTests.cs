using System.Globalization;

using Bitbucket.Mcp.Diffs;

using Xunit;

namespace Bitbucket.Mcp.Tests.Diffs;

/// <summary>
/// Splitting behaviour of <see cref="UnifiedDiffParser"/>.
/// </summary>
/// <remarks>
/// Every count below was read off <c>Fixtures/diff-multi-file.diff</c> by hand. Its layout, by
/// 1-based line number:
/// <code>
///  1- 6  git-log preamble, no diff --git line      -> files[0] header 6, body 0
///  7-10  diff --git / index / --- / +++            -> files[1] header 4
/// 11-27  two hunks                                 -> files[1] body 17
/// 28-34  diff --git / similarity / rename from /
///        rename to / index / --- / +++             -> files[2] header 7
/// 35-39  one hunk                                  -> files[2] body 5
/// 40-44  diff --git / deleted file mode / index /
///        --- / +++ /dev/null                       -> files[3] header 5
/// 45-48  one hunk                                  -> files[3] body 4
/// 49-53  diff --git / new file mode / index /
///        --- /dev/null / +++                       -> files[4] header 5
/// 54-57  one hunk plus "\ No newline at end of
///        file"                                     -> files[4] body 4
/// 58-59  diff --git / index                        -> files[5] header 2
/// 60     Binary files ... differ                   -> files[5] body 1
/// 61-63  diff --git / new file mode / index        -> files[6] header 3
/// 64     Binary files /dev/null and ... differ     -> files[6] body 1
/// </code>
/// </remarks>
public sealed class UnifiedDiffParserTests
{
    /// <summary>Fixture lines 7-10, everything before the first <c>@@</c>.</summary>
    private static readonly string[] ExpectedCalculatorHeader =
    [
        "diff --git a/src/Calculator.cs b/src/Calculator.cs",
        "index 1a2b3c4..5d6e7f8 100644",
        "--- a/src/Calculator.cs",
        "+++ b/src/Calculator.cs",
    ];

    /// <summary>Only the last two files are binary — and one of those is also an addition.</summary>
    private static readonly bool[] ExpectedBinaryFlags = [false, false, false, false, false, true, true];

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\n\t\n")]
    public void SplitYieldsNothingForEmptyInput(string? rawDiff) =>
        Assert.Empty(UnifiedDiffParser.Split(rawDiff));

    [Fact]
    public void SplitsTheMultiFileFixtureIntoItsPreambleAndSixFiles()
    {
        var files = DiffFixtures.Split(DiffFixtures.MultiFile);

        Assert.Equal(7, files.Count);
        Assert.Equal(
            new[]
            {
                DiffFile.UnknownPath,
                "src/Calculator.cs",
                "docs/new notes.md",
                "scripts/legacy.sh",
                "src/Version.txt",
                "assets/logo.png",
                "assets/icon.ico",
            },
            files.Select(static file => file.Path).ToArray());
    }

    [Fact]
    public void EveryStatusAndBinaryFlagCombinationIsReported()
    {
        var files = DiffFixtures.Split(DiffFixtures.MultiFile);

        Assert.Equal(
            new[]
            {
                DiffFileStatus.Modified, // the preamble: nothing says otherwise, so it falls back
                DiffFileStatus.Modified,
                DiffFileStatus.Renamed,
                DiffFileStatus.Removed,
                DiffFileStatus.Added,
                DiffFileStatus.Binary,
                DiffFileStatus.Added, // a binary *addition* keeps Added and sets IsBinary as well
            },
            files.Select(static file => file.Status).ToArray());

        Assert.Equal(ExpectedBinaryFlags, files.Select(static file => file.IsBinary).ToArray());
    }

    [Fact]
    public void PreambleBecomesAnUnnamedFileWithNoBody()
    {
        var preamble = DiffFixtures.Split(DiffFixtures.MultiFile)[0];

        Assert.Null(preamble.OldPath);
        Assert.Null(preamble.NewPath);
        Assert.Equal("(unknown)", preamble.Path);
        Assert.Equal(6, preamble.Header.Count); // fixture lines 1-6
        Assert.Empty(preamble.Body);
        Assert.StartsWith("commit ", preamble.Header[0], StringComparison.Ordinal);
    }

    [Fact]
    public void HeaderEndsAtTheFirstHunkHeader()
    {
        var calculator = DiffFixtures.Split(DiffFixtures.MultiFile)[1];

        Assert.Equal(ExpectedCalculatorHeader, calculator.Header.ToArray());

        Assert.Equal(17, calculator.Body.Count); // fixture lines 11-27
        Assert.Equal("@@ -10,5 +10,8 @@ public static class Calculator", calculator.Body[0]);

        // Hunk 1 is the header plus 9 content lines, so hunk 2's header is body index 10.
        Assert.Equal("@@ -30,5 +33,5 @@ public static class Calculator", calculator.Body[10]);
        Assert.Equal("src/Calculator.cs", calculator.OldPath);
        Assert.Equal("src/Calculator.cs", calculator.NewPath);
    }

    [Fact]
    public void RenameKeepsBothPathsEvenWithSpacesInThem()
    {
        var renamed = DiffFixtures.Split(DiffFixtures.MultiFile)[2];

        Assert.Equal("docs/old notes.md", renamed.OldPath);
        Assert.Equal("docs/new notes.md", renamed.NewPath);
        Assert.Equal("docs/new notes.md", renamed.Path);
        Assert.Equal(7, renamed.Header.Count); // fixture lines 28-34
        Assert.Equal(5, renamed.Body.Count); // fixture lines 35-39
    }

    [Fact]
    public void DeletedFileHasNoNewPath()
    {
        var deleted = DiffFixtures.Split(DiffFixtures.MultiFile)[3];

        Assert.Equal("scripts/legacy.sh", deleted.OldPath);
        Assert.Null(deleted.NewPath);
        Assert.Equal("scripts/legacy.sh", deleted.Path);
        Assert.Equal("+++ /dev/null", deleted.Header[^1]);
        Assert.Equal(4, deleted.Body.Count); // fixture lines 45-48
    }

    [Fact]
    public void AddedFileHasNoOldPathAndKeepsTheNoNewlineMarkerInItsBody()
    {
        var added = DiffFixtures.Split(DiffFixtures.MultiFile)[4];

        Assert.Null(added.OldPath);
        Assert.Equal("src/Version.txt", added.NewPath);
        Assert.Equal("--- /dev/null", added.Header[^2]);

        // Body = @@ header + two "+" lines + the "\ No newline" note (fixture lines 54-57).
        Assert.Equal(4, added.Body.Count);
        Assert.Equal("\\ No newline at end of file", added.Body[^1]);
    }

    [Fact]
    public void BinaryNoticeIsBodySoTheTruncatorCanCapIt()
    {
        var files = DiffFixtures.Split(DiffFixtures.MultiFile);
        var modifiedBinary = files[5];
        var addedBinary = files[6];

        Assert.Equal(2, modifiedBinary.Header.Count);
        Assert.Equal("Binary files a/assets/logo.png and b/assets/logo.png differ", Assert.Single(modifiedBinary.Body));

        Assert.Equal(3, addedBinary.Header.Count);
        Assert.Equal("Binary files /dev/null and b/assets/icon.ico differ", Assert.Single(addedBinary.Body));
        Assert.Null(addedBinary.OldPath);
        Assert.Equal("assets/icon.ico", addedBinary.NewPath);
    }

    [Fact]
    public void GitBinaryPatchLandsInTheBodyWholeSoItCanBeTruncated()
    {
        var file = Assert.Single(DiffFixtures.Split(DiffFixtures.BinaryPatch));

        Assert.Equal(DiffFileStatus.Binary, file.Status);
        Assert.True(file.IsBinary);
        Assert.Equal("assets/sprite.bin", file.Path);
        Assert.Equal(2, file.Header.Count);

        // 305 body lines: "GIT binary patch", "literal 12288", 300 payload lines, a blank line,
        // "literal 0" and "HcmV?d00001" (fixture lines 3-307).
        Assert.Equal(305, file.Body.Count);
        Assert.Equal("GIT binary patch", file.Body[0]);
    }

    [Fact]
    public void OctalEscapedPathsDecodeToUtf8()
    {
        var file = Assert.Single(DiffFixtures.Split(DiffFixtures.OctalPath));

        // "a/src/caf\303\251.cs" -> 0xC3 0xA9 is U+00E9, so src/café.cs.
        Assert.Equal("src/café.cs", file.OldPath);
        Assert.Equal("src/café.cs", file.NewPath);
        Assert.Equal(DiffFileStatus.Modified, file.Status);
        Assert.Equal(4, file.Header.Count);
        Assert.Equal(5, file.Body.Count);
    }

    [Fact]
    public void NoPrefixOutputKeepsItsPaths()
    {
        var file = Assert.Single(DiffFixtures.Split(DiffFixtures.NoPrefix));

        Assert.Equal("src/NoPrefix.cs", file.OldPath);
        Assert.Equal("src/NoPrefix.cs", file.NewPath);
        Assert.Equal("@@ -5,3 +5,3 @@", file.Body[0]);
    }

    [Fact]
    public void HeaderlessDiffStillYieldsAFileWithItsPaths()
    {
        var file = Assert.Single(DiffFixtures.Split(DiffFixtures.Headerless));

        Assert.Equal("src/Headerless.cs", file.OldPath);
        Assert.Equal("src/Headerless.cs", file.NewPath);
        Assert.Equal(2, file.Header.Count); // only --- and +++
        Assert.Equal(5, file.Body.Count);
    }

    [Fact]
    public void ContentWithNoFileHeadersAtAllStillSurvivesAsAnUnnamedFile()
    {
        var file = Assert.Single(UnifiedDiffParser.Split("@@ -1,2 +1,2 @@\n-alpha\n+beta\n"));

        Assert.Null(file.OldPath);
        Assert.Null(file.NewPath);
        Assert.Equal(DiffFile.UnknownPath, file.Path);
        Assert.Empty(file.Header);
        Assert.Equal(3, file.Body.Count);
    }

    [Fact]
    public void LargeFixtureParsesAsOneAddedFile()
    {
        var file = Assert.Single(DiffFixtures.Split(DiffFixtures.LargeFile));

        Assert.Equal(DiffFileStatus.Added, file.Status);
        Assert.False(file.IsBinary);
        Assert.Equal("src/Generated/BigTable.cs", file.Path);
        Assert.Equal(5, file.Header.Count);

        // One "@@ -0,0 +1,4200 @@" header plus 4200 "+" lines.
        Assert.Equal(4201, file.Body.Count);
    }

    [Theory]
    [InlineData(DiffFixtures.MultiFile)]
    [InlineData(DiffFixtures.Anchors)]
    [InlineData(DiffFixtures.OctalPath)]
    [InlineData(DiffFixtures.NoPrefix)]
    [InlineData(DiffFixtures.Headerless)]
    [InlineData(DiffFixtures.BinaryPatch)]
    [InlineData(DiffFixtures.LargeFile)]
    public void CrlfInputProducesExactlyTheSameFilesAsLfInput(string fixture)
    {
        var lf = DiffFixtures.Read(fixture);
        var expected = UnifiedDiffParser.Split(lf);
        var actual = UnifiedDiffParser.Split(DiffFixtures.ToCrlf(lf));

        Assert.Equal(expected.Count, actual.Count);

        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].OldPath, actual[i].OldPath);
            Assert.Equal(expected[i].NewPath, actual[i].NewPath);
            Assert.Equal(expected[i].Status, actual[i].Status);
            Assert.Equal(expected[i].IsBinary, actual[i].IsBinary);
            Assert.Equal(expected[i].Header, actual[i].Header);
            Assert.Equal(expected[i].Body, actual[i].Body);
        }
    }

    /// <summary>
    /// Every <c>@@ -a,b +c,d @@</c> in the text fixtures must describe the lines that follow it:
    /// <c>b</c> = context + removed, <c>d</c> = context + added. The anchor resolver derives every
    /// line number by walking exactly these counters, so a fixture that misstated them would make
    /// the arithmetic expectations in <see cref="InlineAnchorResolverTests"/> meaningless.
    /// </summary>
    [Theory]
    [InlineData(DiffFixtures.MultiFile)]
    [InlineData(DiffFixtures.Anchors)]
    [InlineData(DiffFixtures.OctalPath)]
    [InlineData(DiffFixtures.NoPrefix)]
    [InlineData(DiffFixtures.Headerless)]
    [InlineData(DiffFixtures.LargeFile)]
    public void HunkHeadersAgreeWithTheLinesTheyDescribe(string fixture)
    {
        var hunks = 0;

        foreach (var file in DiffFixtures.Split(fixture))
        {
            var declaredOld = 0;
            var declaredNew = 0;
            var actualOld = 0;
            var actualNew = 0;
            var inHunk = false;

            foreach (var line in file.Body)
            {
                if (line.StartsWith("@@", StringComparison.Ordinal))
                {
                    if (inHunk)
                    {
                        Assert.Equal(declaredOld, actualOld);
                        Assert.Equal(declaredNew, actualNew);
                    }

                    (declaredOld, declaredNew) = ParseHunkCounts(line);
                    actualOld = 0;
                    actualNew = 0;
                    inHunk = true;
                    hunks++;
                    continue;
                }

                if (!inHunk || line.StartsWith('\\'))
                {
                    continue;
                }

                switch (line.Length == 0 ? ' ' : line[0])
                {
                    case '+':
                        actualNew++;
                        break;

                    case '-':
                        actualOld++;
                        break;

                    default:
                        actualOld++;
                        actualNew++;
                        break;
                }
            }

            if (inHunk)
            {
                Assert.Equal(declaredOld, actualOld);
                Assert.Equal(declaredNew, actualNew);
            }
        }

        Assert.True(hunks > 0, "the fixture should contain at least one hunk");
    }

    /// <summary>Reads <c>b</c> and <c>d</c> out of <c>@@ -a,b +c,d @@</c> (both default to 1).</summary>
    private static (int Old, int New) ParseHunkCounts(string header)
    {
        var parts = header.Split(' ');
        return (ParseCount(parts[1]), ParseCount(parts[2]));

        static int ParseCount(string range)
        {
            var comma = range.IndexOf(',');
            return comma < 0 ? 1 : int.Parse(range[(comma + 1)..], CultureInfo.InvariantCulture);
        }
    }
}
