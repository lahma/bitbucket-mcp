using System.Globalization;
using System.Text;

using Bitbucket.Mcp.Diffs;

using Xunit;

namespace Bitbucket.Mcp.Tests.Diffs;

/// <summary>
/// Anchor resolution against <c>Fixtures/diff-anchors.diff</c>.
/// </summary>
/// <remarks>
/// Every expected line number below was derived by hand from the two <c>@@</c> headers, walking the
/// counters the way the resolver does (a <c>+</c> line advances only the new-file counter, a
/// <c>-</c> line only the old-file counter, a context line both). Fixture lines are 1-based:
/// <code>
///  5  @@ -10,9 +10,9 @@                                     hunk 0 starts at old 10 / new 10
///  6   public int Total()                    context  old 10  new 10
///  7   {                                     context  old 11  new 11
///  8   var total = 0;                        context  old 12  new 12
///  9  -foreach (var item in items)           removed  old 13    —
/// 10  +foreach (var item in Items)           added      —     new 13
/// 11   {                                     context  old 14  new 14
/// 12   total += item.Value;                  context  old 15  new 15
/// 13   }                                     context  old 16  new 16
/// 14  -return total;                         removed  old 17    —
/// 15  +return total; // fixed                added      —     new 17
/// 16   }                                     context  old 18  new 18
/// 17  @@ -40,5 +40,7 @@                                      hunk 1 starts at old 40 / new 40
/// 18   public void Dump()                    context  old 40  new 40
/// 19   {                                     context  old 41  new 41
/// 20   Console.WriteLine("done");            context  old 42  new 42
/// 21  +Console.WriteLine("done");            added      —     new 43
/// 22  +Console.WriteLine("extra");           added      —     new 44
/// 23   }                                     context  old 43  new 45
/// 24  }                                      context  old 44  new 46
/// </code>
/// So the diff shows new-file lines <c>10-18, 40-46</c> and old-file lines <c>10-18, 40-44</c>,
/// and nothing else.
/// </remarks>
public sealed class InlineAnchorResolverTests
{
    private const string ReportPath = "src/Report.cs";

    private static IReadOnlyList<DiffFile> Anchors => DiffFixtures.Split(DiffFixtures.Anchors);

    // -----------------------------------------------------------------------------------------
    // codeSnippet — happy paths
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void UniqueSnippetOnAnAddedLineAnchorsToTheNewSide()
    {
        var anchor = InlineAnchorResolver.Resolve(
            Anchors,
            ReportPath,
            codeSnippet: "foreach (var item in Items)");

        Assert.Equal(DiffLineType.Added, anchor.LineType);
        Assert.Equal(13, anchor.Line); // fixture line 10
        Assert.Equal(ReportPath, anchor.Inline.Path);
        Assert.Equal(13, anchor.Inline.To);
        Assert.Null(anchor.Inline.From);
        Assert.Null(anchor.Inline.StartTo);
        Assert.Null(anchor.Inline.StartFrom);

        // The matched text keeps the original indentation and loses only the "+" marker.
        Assert.Equal("        foreach (var item in Items)", anchor.MatchedText);
    }

    [Fact]
    public void ExactMatchWinsOverContainmentSoTheRemovedLineIsChosen()
    {
        // "return total;" is exactly the removed line (fixture 14, old 17) and a prefix of the
        // added one (fixture 15, new 17). Whole-line equality is tried first, so there is one
        // match and it is on the old side.
        var anchor = InlineAnchorResolver.Resolve(Anchors, ReportPath, codeSnippet: "return total;");

        Assert.Equal(DiffLineType.Removed, anchor.LineType);
        Assert.Equal(17, anchor.Line);
        Assert.Equal(17, anchor.Inline.From);
        Assert.Null(anchor.Inline.To);
        Assert.Equal("        return total;", anchor.MatchedText);
    }

    [Fact]
    public void WithoutAnExactMatchTheSameTwoLinesAreAmbiguous()
    {
        // The counterpart of the test above: "return total" (no semicolon) is equal to neither
        // line, so the containment tier runs and finds both.
        var exception = Assert.Throws<InlineAnchorException>(
            () => InlineAnchorResolver.Resolve(Anchors, ReportPath, codeSnippet: "return total"));

        Assert.Contains("matches 2 places", exception.Message, StringComparison.Ordinal);
        Assert.Contains("line 17 before the change (lineType=REMOVED): return total;", exception.Message, StringComparison.Ordinal);
        Assert.Contains("line 17 after the change (lineType=ADDED): return total; // fixed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ContainmentTierMatchesASubstringOfAContextLine()
    {
        var anchor = InlineAnchorResolver.Resolve(Anchors, ReportPath, codeSnippet: "item.Value");

        Assert.Equal(DiffLineType.Context, anchor.LineType);
        Assert.Equal(15, anchor.Line); // fixture line 12, context, new 15
        Assert.Equal(15, anchor.Inline.To);
        Assert.Null(anchor.Inline.From);
    }

    [Fact]
    public void MultiLineSnippetMatchesAConsecutiveRunAndSpansIt()
    {
        var anchor = InlineAnchorResolver.Resolve(
            Anchors,
            ReportPath,
            codeSnippet: "Console.WriteLine(\"done\");\nConsole.WriteLine(\"extra\");");

        // Fixture lines 21-22: both added, new 43 and new 44. The run's last line is the anchor,
        // the first is the start of the range.
        Assert.Equal(DiffLineType.Added, anchor.LineType);
        Assert.Equal(44, anchor.Line);
        Assert.Equal(44, anchor.Inline.To);
        Assert.Equal(43, anchor.Inline.StartTo);
        Assert.Null(anchor.Inline.From);
        Assert.Null(anchor.Inline.StartFrom);
        Assert.Equal(
            "        Console.WriteLine(\"done\");\n        Console.WriteLine(\"extra\");",
            anchor.MatchedText);
    }

    [Fact]
    public void SnippetCopiedWithItsDiffMarkerStillMatches()
    {
        var anchor = InlineAnchorResolver.Resolve(
            Anchors,
            ReportPath,
            codeSnippet: "+        Console.WriteLine(\"extra\");");

        Assert.Equal(DiffLineType.Added, anchor.LineType);
        Assert.Equal(44, anchor.Line);
        Assert.Equal(44, anchor.Inline.To);
    }

    [Fact]
    public void AmbiguousSnippetListsEveryCandidateWithItsLineNumber()
    {
        // Fixture line 20 (context, new 42) and line 21 (added, new 43) are the same text.
        var exception = Assert.Throws<InlineAnchorException>(
            () => InlineAnchorResolver.Resolve(Anchors, ReportPath, codeSnippet: "Console.WriteLine(\"done\");"));

        Assert.Contains("matches 2 places", exception.Message, StringComparison.Ordinal);
        Assert.Contains("line 42 after the change (lineType=CONTEXT)", exception.Message, StringComparison.Ordinal);
        Assert.Contains("line 43 after the change (lineType=ADDED)", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Re-send the same codeSnippet together with line", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The tie-breaker the ambiguity error asks for: the same snippet plus one of the offered line
    /// numbers. 42 is the context line (fixture 20), 43 the added one (fixture 21).
    /// </summary>
    [Theory]
    [InlineData(42, "CONTEXT")]
    [InlineData(43, "ADDED")]
    public void LineBreaksTheTieBetweenSeveralSnippetMatches(int line, string expectedType)
    {
        var anchor = InlineAnchorResolver.Resolve(
            Anchors,
            ReportPath,
            codeSnippet: "Console.WriteLine(\"done\");",
            line: line);

        Assert.Equal(ParseLineType(expectedType), anchor.LineType);
        Assert.Equal(line, anchor.Line);
        Assert.Equal(line, anchor.Inline.To);
    }

    [Fact]
    public void SnippetPlusStartLineProducesAMultiLineAnchor()
    {
        var anchor = InlineAnchorResolver.Resolve(
            Anchors,
            ReportPath,
            codeSnippet: "Console.WriteLine(\"extra\");",
            startLine: 42);

        Assert.Equal(44, anchor.Inline.To);
        Assert.Equal(42, anchor.Inline.StartTo);
    }

    /// <summary>
    /// The snippet picks the side, so <c>startLine</c> is read on that side too: "return total;"
    /// resolves to the removed line (old 17), and old 13 is a line the diff shows before the change.
    /// </summary>
    [Fact]
    public void SnippetOnTheOldSideTakesItsStartLineFromTheOldSide()
    {
        var anchor = InlineAnchorResolver.Resolve(
            Anchors,
            ReportPath,
            codeSnippet: "return total;",
            startLine: 13);

        Assert.Equal(17, anchor.Inline.From);
        Assert.Equal(13, anchor.Inline.StartFrom);
        Assert.Null(anchor.Inline.To);
        Assert.Null(anchor.Inline.StartTo);
    }

    // -----------------------------------------------------------------------------------------
    // Explicit line + lineType
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void AddedLineTypeAnchorsToTheNewSide()
    {
        var anchor = InlineAnchorResolver.Resolve(Anchors, ReportPath, line: 13, lineType: DiffLineType.Added);

        Assert.Equal(13, anchor.Inline.To);
        Assert.Null(anchor.Inline.From);
        Assert.Equal(DiffLineType.Added, anchor.LineType);
        Assert.Equal("        foreach (var item in Items)", anchor.MatchedText);
    }

    [Fact]
    public void RemovedLineTypeAnchorsToTheOldSide()
    {
        // Old line 13 and new line 13 are different lines of the diff (fixture 9 and 10); the side
        // is what tells them apart.
        var anchor = InlineAnchorResolver.Resolve(Anchors, ReportPath, line: 13, lineType: DiffLineType.Removed);

        Assert.Equal(13, anchor.Inline.From);
        Assert.Null(anchor.Inline.To);
        Assert.Equal(DiffLineType.Removed, anchor.LineType);
        Assert.Equal("        foreach (var item in items)", anchor.MatchedText);
    }

    [Fact]
    public void ContextLineTypeAnchorsToTheNewSide()
    {
        var anchor = InlineAnchorResolver.Resolve(Anchors, ReportPath, line: 42, lineType: DiffLineType.Context);

        Assert.Equal(42, anchor.Inline.To);
        Assert.Null(anchor.Inline.From);
        Assert.Equal(DiffLineType.Context, anchor.LineType);
    }

    [Fact]
    public void LineTypeDefaultsToAdded()
    {
        var anchor = InlineAnchorResolver.Resolve(Anchors, ReportPath, line: 13);

        Assert.Equal(13, anchor.Inline.To);
        Assert.Equal(DiffLineType.Added, anchor.LineType);
    }

    [Fact]
    public void ExplicitStartLineSpansTheRangeOnTheRequestedSide()
    {
        var newSide = InlineAnchorResolver.Resolve(
            Anchors,
            ReportPath,
            line: 45,
            lineType: DiffLineType.Context,
            startLine: 42);

        Assert.Equal(45, newSide.Inline.To);
        Assert.Equal(42, newSide.Inline.StartTo);
        Assert.Null(newSide.Inline.StartFrom);

        var oldSide = InlineAnchorResolver.Resolve(
            Anchors,
            ReportPath,
            line: 17,
            lineType: DiffLineType.Removed,
            startLine: 13);

        Assert.Equal(17, oldSide.Inline.From);
        Assert.Equal(13, oldSide.Inline.StartFrom);
        Assert.Null(oldSide.Inline.StartTo);
    }

    // -----------------------------------------------------------------------------------------
    // Paths
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("./src/Report.cs")]
    [InlineData("src\\Report.cs")]
    [InlineData("/src/Report.cs")]
    [InlineData("SRC/report.CS")]
    [InlineData("  src/Report.cs  ")]
    public void CallerReshapedPathsResolveToTheCanonicalDiffPath(string path)
    {
        var anchor = InlineAnchorResolver.Resolve(Anchors, path, codeSnippet: "item.Value");

        Assert.Equal(ReportPath, anchor.Inline.Path);
    }

    [Fact]
    public void UnknownPathListsThePathsTheDiffDoesContain()
    {
        var exception = Assert.Throws<InlineAnchorException>(
            () => InlineAnchorResolver.Resolve(DiffFixtures.Split(DiffFixtures.MultiFile), "src/Nope.cs", codeSnippet: "anything"));

        Assert.Contains("This diff contains no file named src/Nope.cs.", exception.Message, StringComparison.Ordinal);
        Assert.Contains("src/Calculator.cs", exception.Message, StringComparison.Ordinal);
        Assert.Contains("docs/new notes.md", exception.Message, StringComparison.Ordinal);
        Assert.Contains("assets/icon.ico", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Use a path exactly as the diff spells it.", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyDiffSaysToFetchOneFirst()
    {
        var exception = Assert.Throws<InlineAnchorException>(
            () => InlineAnchorResolver.Resolve([], "src/Report.cs", codeSnippet: "anything"));

        Assert.Contains("The diff is empty", exception.Message, StringComparison.Ordinal);
        Assert.Contains("getPullRequestDiff", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankPathIsRejected(string path)
    {
        var exception = Assert.Throws<InlineAnchorException>(
            () => InlineAnchorResolver.Resolve(Anchors, path, codeSnippet: "item.Value"));

        Assert.Contains("needs the path of the file", exception.Message, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------------------------
    // Error paths
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void BinaryFileHasNoLinesToAnchorTo()
    {
        var exception = Assert.Throws<InlineAnchorException>(
            () => InlineAnchorResolver.Resolve(DiffFixtures.Split(DiffFixtures.MultiFile), "assets/logo.png", codeSnippet: "anything"));

        Assert.Contains("assets/logo.png is a binary file", exception.Message, StringComparison.Ordinal);
        Assert.Contains("comment on the pull request itself", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FileWhoseHunksWereNotIncludedSaysToFetchThem()
    {
        var files = UnifiedDiffParser.Split(
            "diff --git a/src/Empty.cs b/src/Empty.cs\n"
            + "index 1111111..2222222 100644\n"
            + "--- a/src/Empty.cs\n"
            + "+++ b/src/Empty.cs\n");

        var exception = Assert.Throws<InlineAnchorException>(
            () => InlineAnchorResolver.Resolve(files, "src/Empty.cs", codeSnippet: "anything"));

        Assert.Contains("contains no changed lines", exception.Message, StringComparison.Ordinal);
        Assert.Contains("paths=[\"src/Empty.cs\"]", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A snippet that matches nothing gets the five lines with the most token overlap. Tokens of
    /// <c>total = item.Value + total;</c> are {total, item, Value}; six diff lines share at least
    /// one, and they are ranked by the summed length of the shared tokens:
    /// <c>total += item.Value;</c> (14) beats <c>var total = 0;</c>, <c>return total;</c> and
    /// <c>return total; // fixed</c> (5 each), which beat the two <c>foreach</c> lines (4 each) —
    /// so the last <c>foreach</c> line falls off the end of the list.
    /// </summary>
    [Fact]
    public void NoMatchListsTheFiveNearestLines()
    {
        var exception = Assert.Throws<InlineAnchorException>(
            () => InlineAnchorResolver.Resolve(Anchors, ReportPath, codeSnippet: "total = item.Value + total;"));

        Assert.Contains("No line in the diff of src/Report.cs matches that codeSnippet", exception.Message, StringComparison.Ordinal);
        Assert.Contains("The closest lines it does show are:", exception.Message, StringComparison.Ordinal);
        Assert.Equal(5, CountOccurrences(exception.Message, "\n  line "));

        Assert.Contains("line 15 after the change (lineType=CONTEXT): total += item.Value;", exception.Message, StringComparison.Ordinal);
        Assert.Contains("line 12 after the change (lineType=CONTEXT): var total = 0;", exception.Message, StringComparison.Ordinal);
        Assert.Contains("line 17 before the change (lineType=REMOVED): return total;", exception.Message, StringComparison.Ordinal);
        Assert.Contains("line 17 after the change (lineType=ADDED): return total; // fixed", exception.Message, StringComparison.Ordinal);
        Assert.Contains("line 13 before the change (lineType=REMOVED): foreach (var item in items)", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AmbiguityListIsCappedAtTwentyCandidates()
    {
        var builder = new StringBuilder();
        builder.Append("diff --git a/src/Repeat.cs b/src/Repeat.cs\n");
        builder.Append("index 1111111..2222222 100644\n");
        builder.Append("--- a/src/Repeat.cs\n");
        builder.Append("+++ b/src/Repeat.cs\n");
        builder.Append("@@ -0,0 +1,25 @@\n");

        for (var i = 0; i < 25; i++)
        {
            builder.Append("+        Log(\"x\");\n");
        }

        var files = UnifiedDiffParser.Split(builder.ToString());

        var exception = Assert.Throws<InlineAnchorException>(
            () => InlineAnchorResolver.Resolve(files, "src/Repeat.cs", codeSnippet: "Log(\"x\");"));

        Assert.Contains("matches 25 places", exception.Message, StringComparison.Ordinal);
        Assert.Equal(20, CountOccurrences(exception.Message, "\n  line "));
        Assert.Contains("… and 5 more.", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(19, "ADDED", "the file after the change")]
    [InlineData(39, "CONTEXT", "the file after the change")]
    [InlineData(45, "REMOVED", "the file before the change")]
    public void LineOutsideTheDiffListsWhatBothSidesDoShow(int line, string lineType, string side)
    {
        var exception = Assert.Throws<InlineAnchorException>(
            () => InlineAnchorResolver.Resolve(Anchors, ReportPath, line: line, lineType: ParseLineType(lineType)));

        Assert.Contains(
            string.Create(CultureInfo.InvariantCulture, $"line={line} is not a line the diff of src/Report.cs shows for {side}"),
            exception.Message,
            StringComparison.Ordinal);
        Assert.Contains("Lines available after the change (lineType=ADDED or CONTEXT): 10-18, 40-46", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Lines available before the change (lineType=REMOVED): 10-18, 40-44", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StartLineMustBeSmallerThanTheCommentedLine()
    {
        var exception = Assert.Throws<InlineAnchorException>(
            () => InlineAnchorResolver.Resolve(Anchors, ReportPath, line: 45, lineType: DiffLineType.Context, startLine: 45));

        Assert.Contains("startLine (45) must be smaller than the commented line (45)", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StartLineMustAlsoBeVisibleInTheDiff()
    {
        var exception = Assert.Throws<InlineAnchorException>(
            () => InlineAnchorResolver.Resolve(Anchors, ReportPath, line: 45, lineType: DiffLineType.Context, startLine: 19));

        Assert.Contains("startLine=19 is not a line the diff of src/Report.cs shows", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same rule in snippet mode, which used to trust <c>startLine</c> outright: the snippet
    /// resolves to new line 44, but 19 is in neither hunk, so a comment starting there would be
    /// anchored to a line nothing in this response shows.
    /// </summary>
    [Fact]
    public void StartLineIsCheckedInSnippetModeToo()
    {
        var exception = Assert.Throws<InlineAnchorException>(() => InlineAnchorResolver.Resolve(
            Anchors,
            ReportPath,
            codeSnippet: "Console.WriteLine(\"extra\");",
            startLine: 19));

        Assert.Contains(
            "startLine=19 is not a line the diff of src/Report.cs shows for the file after the change",
            exception.Message,
            StringComparison.Ordinal);

        // The range listing is what turns the refusal into a usable retry.
        Assert.Contains("Lines available after the change (lineType=ADDED or CONTEXT): 10-18, 40-46", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Lines available before the change (lineType=REMOVED): 10-18, 40-44", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// And on the old side: "return total;" is the removed line (old 17), so <c>startLine</c> is
    /// read against the file before the change, where line 9 is one line above the first hunk.
    /// </summary>
    [Fact]
    public void SnippetStartLineIsCheckedOnTheSideTheSnippetResolvedTo()
    {
        var exception = Assert.Throws<InlineAnchorException>(() => InlineAnchorResolver.Resolve(
            Anchors,
            ReportPath,
            codeSnippet: "return total;",
            startLine: 9));

        Assert.Contains(
            "startLine=9 is not a line the diff of src/Report.cs shows for the file before the change",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SnippetSpanningBothSidesIsRejected()
    {
        // Fixture lines 9-10: one removed line immediately followed by its replacement. The run
        // matches, but it cannot be one anchor.
        var exception = Assert.Throws<InlineAnchorException>(
            () => InlineAnchorResolver.Resolve(
                Anchors,
                ReportPath,
                codeSnippet: "foreach (var item in items)\nforeach (var item in Items)"));

        Assert.Contains("spans both removed and added lines", exception.Message, StringComparison.Ordinal);
        Assert.Contains("lineType=ADDED", exception.Message, StringComparison.Ordinal);
        Assert.Contains("lineType=REMOVED", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NeitherSnippetNorLineIsAnError()
    {
        var exception = Assert.Throws<InlineAnchorException>(
            () => InlineAnchorResolver.Resolve(Anchors, ReportPath));

        Assert.Contains("without knowing where", exception.Message, StringComparison.Ordinal);
        Assert.Contains("codeSnippet", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void LineMustBePositive(int line)
    {
        var exception = Assert.Throws<InlineAnchorException>(
            () => InlineAnchorResolver.Resolve(Anchors, ReportPath, line: line));

        Assert.Contains("line must be 1 or greater", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NullFileListIsRejected() =>
        Assert.Throws<ArgumentNullException>(() => InlineAnchorResolver.Resolve(null!, ReportPath, codeSnippet: "x"));

    /// <summary>Maps the tool-facing vocabulary to the enum, so theory data can stay public.</summary>
    private static DiffLineType ParseLineType(string value) => value switch
    {
        "ADDED" => DiffLineType.Added,
        "REMOVED" => DiffLineType.Removed,
        "CONTEXT" => DiffLineType.Context,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown lineType."),
    };

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
