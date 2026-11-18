using System.Globalization;

using Bitbucket.Mcp.Diffs;


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

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("\n\n\t\n")]
    public void SplitYieldsNothingForEmptyInput(string? rawDiff) =>
        Assert.That(UnifiedDiffParser.Split(rawDiff), Is.Empty);

    [Test]
    public void SplitsTheMultiFileFixtureIntoItsPreambleAndSixFiles()
    {
        var files = DiffFixtures.Split(DiffFixtures.MultiFile);

        Assert.That(files.Count, Is.EqualTo(7));
        Assert.That(files.Select(static file => file.Path).ToArray(), Is.EqualTo(new[]
            {
                DiffFile.UnknownPath,
                "src/Calculator.cs",
                "docs/new notes.md",
                "scripts/legacy.sh",
                "src/Version.txt",
                "assets/logo.png",
                "assets/icon.ico",
            }));
    }

    [Test]
    public void EveryStatusAndBinaryFlagCombinationIsReported()
    {
        var files = DiffFixtures.Split(DiffFixtures.MultiFile);

        Assert.That(files.Select(static file => file.Status).ToArray(), Is.EqualTo(new[]
            {
                DiffFileStatus.Modified, // the preamble: nothing says otherwise, so it falls back
                DiffFileStatus.Modified,
                DiffFileStatus.Renamed,
                DiffFileStatus.Removed,
                DiffFileStatus.Added,
                DiffFileStatus.Binary,
                DiffFileStatus.Added, // a binary *addition* keeps Added and sets IsBinary as well
            }));

        Assert.That(files.Select(static file => file.IsBinary).ToArray(), Is.EqualTo(ExpectedBinaryFlags));
    }

    [Test]
    public void PreambleBecomesAnUnnamedFileWithNoBody()
    {
        var preamble = DiffFixtures.Split(DiffFixtures.MultiFile)[0];

        Assert.That(preamble.OldPath, Is.Null);
        Assert.That(preamble.NewPath, Is.Null);
        Assert.That(preamble.Path, Is.EqualTo("(unknown)"));
        Assert.That(preamble.Header.Count, Is.EqualTo(6)); // fixture lines 1-6
        Assert.That(preamble.Body, Is.Empty);
        Assert.That(preamble.Header[0], Does.StartWith("commit "));
    }

    [Test]
    public void HeaderEndsAtTheFirstHunkHeader()
    {
        var calculator = DiffFixtures.Split(DiffFixtures.MultiFile)[1];

        Assert.That(calculator.Header.ToArray(), Is.EqualTo(ExpectedCalculatorHeader));

        Assert.That(calculator.Body.Count, Is.EqualTo(17)); // fixture lines 11-27
        Assert.That(calculator.Body[0], Is.EqualTo("@@ -10,5 +10,8 @@ public static class Calculator"));

        // Hunk 1 is the header plus 9 content lines, so hunk 2's header is body index 10.
        Assert.That(calculator.Body[10], Is.EqualTo("@@ -30,5 +33,5 @@ public static class Calculator"));
        Assert.That(calculator.OldPath, Is.EqualTo("src/Calculator.cs"));
        Assert.That(calculator.NewPath, Is.EqualTo("src/Calculator.cs"));
    }

    [Test]
    public void RenameKeepsBothPathsEvenWithSpacesInThem()
    {
        var renamed = DiffFixtures.Split(DiffFixtures.MultiFile)[2];

        Assert.That(renamed.OldPath, Is.EqualTo("docs/old notes.md"));
        Assert.That(renamed.NewPath, Is.EqualTo("docs/new notes.md"));
        Assert.That(renamed.Path, Is.EqualTo("docs/new notes.md"));
        Assert.That(renamed.Header.Count, Is.EqualTo(7)); // fixture lines 28-34
        Assert.That(renamed.Body.Count, Is.EqualTo(5)); // fixture lines 35-39
    }

    [Test]
    public void DeletedFileHasNoNewPath()
    {
        var deleted = DiffFixtures.Split(DiffFixtures.MultiFile)[3];

        Assert.That(deleted.OldPath, Is.EqualTo("scripts/legacy.sh"));
        Assert.That(deleted.NewPath, Is.Null);
        Assert.That(deleted.Path, Is.EqualTo("scripts/legacy.sh"));
        Assert.That(deleted.Header[^1], Is.EqualTo("+++ /dev/null"));
        Assert.That(deleted.Body.Count, Is.EqualTo(4)); // fixture lines 45-48
    }

    [Test]
    public void AddedFileHasNoOldPathAndKeepsTheNoNewlineMarkerInItsBody()
    {
        var added = DiffFixtures.Split(DiffFixtures.MultiFile)[4];

        Assert.That(added.OldPath, Is.Null);
        Assert.That(added.NewPath, Is.EqualTo("src/Version.txt"));
        Assert.That(added.Header[^2], Is.EqualTo("--- /dev/null"));

        // Body = @@ header + two "+" lines + the "\ No newline" note (fixture lines 54-57).
        Assert.That(added.Body.Count, Is.EqualTo(4));
        Assert.That(added.Body[^1], Is.EqualTo("\\ No newline at end of file"));
    }

    [Test]
    public void BinaryNoticeIsBodySoTheTruncatorCanCapIt()
    {
        var files = DiffFixtures.Split(DiffFixtures.MultiFile);
        var modifiedBinary = files[5];
        var addedBinary = files[6];

        Assert.That(modifiedBinary.Header.Count, Is.EqualTo(2));
        Assert.That(Expect.Single(modifiedBinary.Body), Is.EqualTo("Binary files a/assets/logo.png and b/assets/logo.png differ"));

        Assert.That(addedBinary.Header.Count, Is.EqualTo(3));
        Assert.That(Expect.Single(addedBinary.Body), Is.EqualTo("Binary files /dev/null and b/assets/icon.ico differ"));
        Assert.That(addedBinary.OldPath, Is.Null);
        Assert.That(addedBinary.NewPath, Is.EqualTo("assets/icon.ico"));
    }

    [Test]
    public void GitBinaryPatchLandsInTheBodyWholeSoItCanBeTruncated()
    {
        var file = Expect.Single(DiffFixtures.Split(DiffFixtures.BinaryPatch));

        Assert.That(file.Status, Is.EqualTo(DiffFileStatus.Binary));
        Assert.That(file.IsBinary, Is.True);
        Assert.That(file.Path, Is.EqualTo("assets/sprite.bin"));
        Assert.That(file.Header.Count, Is.EqualTo(2));

        // 305 body lines: "GIT binary patch", "literal 12288", 300 payload lines, a blank line,
        // "literal 0" and "HcmV?d00001" (fixture lines 3-307).
        Assert.That(file.Body.Count, Is.EqualTo(305));
        Assert.That(file.Body[0], Is.EqualTo("GIT binary patch"));
    }

    [Test]
    public void OctalEscapedPathsDecodeToUtf8()
    {
        var file = Expect.Single(DiffFixtures.Split(DiffFixtures.OctalPath));

        // "a/src/caf\303\251.cs" -> 0xC3 0xA9 is U+00E9, so src/café.cs.
        Assert.That(file.OldPath, Is.EqualTo("src/café.cs"));
        Assert.That(file.NewPath, Is.EqualTo("src/café.cs"));
        Assert.That(file.Status, Is.EqualTo(DiffFileStatus.Modified));
        Assert.That(file.Header.Count, Is.EqualTo(4));
        Assert.That(file.Body.Count, Is.EqualTo(5));
    }

    [Test]
    public void NoPrefixOutputKeepsItsPaths()
    {
        var file = Expect.Single(DiffFixtures.Split(DiffFixtures.NoPrefix));

        Assert.That(file.OldPath, Is.EqualTo("src/NoPrefix.cs"));
        Assert.That(file.NewPath, Is.EqualTo("src/NoPrefix.cs"));
        Assert.That(file.Body[0], Is.EqualTo("@@ -5,3 +5,3 @@"));
    }

    [Test]
    public void HeaderlessDiffStillYieldsAFileWithItsPaths()
    {
        var file = Expect.Single(DiffFixtures.Split(DiffFixtures.Headerless));

        Assert.That(file.OldPath, Is.EqualTo("src/Headerless.cs"));
        Assert.That(file.NewPath, Is.EqualTo("src/Headerless.cs"));
        Assert.That(file.Header.Count, Is.EqualTo(2)); // only --- and +++
        Assert.That(file.Body.Count, Is.EqualTo(5));
    }

    [Test]
    public void ContentWithNoFileHeadersAtAllStillSurvivesAsAnUnnamedFile()
    {
        var file = Expect.Single(UnifiedDiffParser.Split("@@ -1,2 +1,2 @@\n-alpha\n+beta\n"));

        Assert.That(file.OldPath, Is.Null);
        Assert.That(file.NewPath, Is.Null);
        Assert.That(file.Path, Is.EqualTo(DiffFile.UnknownPath));
        Assert.That(file.Header, Is.Empty);
        Assert.That(file.Body.Count, Is.EqualTo(3));
    }

    [Test]
    public void LargeFixtureParsesAsOneAddedFile()
    {
        var file = Expect.Single(DiffFixtures.Split(DiffFixtures.LargeFile));

        Assert.That(file.Status, Is.EqualTo(DiffFileStatus.Added));
        Assert.That(file.IsBinary, Is.False);
        Assert.That(file.Path, Is.EqualTo("src/Generated/BigTable.cs"));
        Assert.That(file.Header.Count, Is.EqualTo(5));

        // One "@@ -0,0 +1,4200 @@" header plus 4200 "+" lines.
        Assert.That(file.Body.Count, Is.EqualTo(4201));
    }

    [TestCase(DiffFixtures.MultiFile)]
    [TestCase(DiffFixtures.Anchors)]
    [TestCase(DiffFixtures.OctalPath)]
    [TestCase(DiffFixtures.NoPrefix)]
    [TestCase(DiffFixtures.Headerless)]
    [TestCase(DiffFixtures.BinaryPatch)]
    [TestCase(DiffFixtures.LargeFile)]
    public void CrlfInputProducesExactlyTheSameFilesAsLfInput(string fixture)
    {
        var lf = DiffFixtures.Read(fixture);
        var expected = UnifiedDiffParser.Split(lf);
        var actual = UnifiedDiffParser.Split(DiffFixtures.ToCrlf(lf));

        Assert.That(actual.Count, Is.EqualTo(expected.Count));

        for (var i = 0; i < expected.Count; i++)
        {
            Assert.That(actual[i].OldPath, Is.EqualTo(expected[i].OldPath));
            Assert.That(actual[i].NewPath, Is.EqualTo(expected[i].NewPath));
            Assert.That(actual[i].Status, Is.EqualTo(expected[i].Status));
            Assert.That(actual[i].IsBinary, Is.EqualTo(expected[i].IsBinary));
            Assert.That(actual[i].Header, Is.EqualTo(expected[i].Header));
            Assert.That(actual[i].Body, Is.EqualTo(expected[i].Body));
        }
    }

    /// <summary>
    /// Every <c>@@ -a,b +c,d @@</c> in the text fixtures must describe the lines that follow it:
    /// <c>b</c> = context + removed, <c>d</c> = context + added. The anchor resolver derives every
    /// line number by walking exactly these counters, so a fixture that misstated them would make
    /// the arithmetic expectations in <see cref="InlineAnchorResolverTests"/> meaningless.
    /// </summary>
    [TestCase(DiffFixtures.MultiFile)]
    [TestCase(DiffFixtures.Anchors)]
    [TestCase(DiffFixtures.OctalPath)]
    [TestCase(DiffFixtures.NoPrefix)]
    [TestCase(DiffFixtures.Headerless)]
    [TestCase(DiffFixtures.LargeFile)]
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
                        Assert.That(actualOld, Is.EqualTo(declaredOld));
                        Assert.That(actualNew, Is.EqualTo(declaredNew));
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
                Assert.That(actualOld, Is.EqualTo(declaredOld));
                Assert.That(actualNew, Is.EqualTo(declaredNew));
            }
        }

        Assert.That(hunks > 0, Is.True, "the fixture should contain at least one hunk");
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
