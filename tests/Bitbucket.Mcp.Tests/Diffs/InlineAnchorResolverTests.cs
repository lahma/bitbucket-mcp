using System.Globalization;
using System.Text;

using Bitbucket.Mcp.Diffs;


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

    [Test]
    public void UniqueSnippetOnAnAddedLineAnchorsToTheNewSide()
    {
        var anchor = InlineAnchorResolver.Resolve(
            Anchors,
            ReportPath,
            codeSnippet: "foreach (var item in Items)");

        Assert.That(anchor.LineType, Is.EqualTo(DiffLineType.Added));
        Assert.That(anchor.Line, Is.EqualTo(13)); // fixture line 10
        Assert.That(anchor.Inline.Path, Is.EqualTo(ReportPath));
        Assert.That(anchor.Inline.To, Is.EqualTo(13));
        Assert.That(anchor.Inline.From, Is.Null);
        Assert.That(anchor.Inline.StartTo, Is.Null);
        Assert.That(anchor.Inline.StartFrom, Is.Null);

        // The matched text keeps the original indentation and loses only the "+" marker.
        Assert.That(anchor.MatchedText, Is.EqualTo("        foreach (var item in Items)"));
    }

    [Test]
    public void ExactMatchWinsOverContainmentSoTheRemovedLineIsChosen()
    {
        // "return total;" is exactly the removed line (fixture 14, old 17) and a prefix of the
        // added one (fixture 15, new 17). Whole-line equality is tried first, so there is one
        // match and it is on the old side.
        var anchor = InlineAnchorResolver.Resolve(Anchors, ReportPath, codeSnippet: "return total;");

        Assert.That(anchor.LineType, Is.EqualTo(DiffLineType.Removed));
        Assert.That(anchor.Line, Is.EqualTo(17));
        Assert.That(anchor.Inline.From, Is.EqualTo(17));
        Assert.That(anchor.Inline.To, Is.Null);
        Assert.That(anchor.MatchedText, Is.EqualTo("        return total;"));
    }

    [Test]
    public void WithoutAnExactMatchTheSameTwoLinesAreAmbiguous()
    {
        // The counterpart of the test above: "return total" (no semicolon) is equal to neither
        // line, so the containment tier runs and finds both.
        var exception = Expect.Throws<InlineAnchorException>(() => InlineAnchorResolver.Resolve(Anchors, ReportPath, codeSnippet: "return total"));

        Assert.That(exception.Message, Does.Contain("matches 2 places"));
        Assert.That(exception.Message, Does.Contain("line 17 before the change (lineType=REMOVED): return total;"));
        Assert.That(exception.Message, Does.Contain("line 17 after the change (lineType=ADDED): return total; // fixed"));
    }

    [Test]
    public void ContainmentTierMatchesASubstringOfAContextLine()
    {
        var anchor = InlineAnchorResolver.Resolve(Anchors, ReportPath, codeSnippet: "item.Value");

        Assert.That(anchor.LineType, Is.EqualTo(DiffLineType.Context));
        Assert.That(anchor.Line, Is.EqualTo(15)); // fixture line 12, context, new 15
        Assert.That(anchor.Inline.To, Is.EqualTo(15));
        Assert.That(anchor.Inline.From, Is.Null);
    }

    [Test]
    public void MultiLineSnippetMatchesAConsecutiveRunAndSpansIt()
    {
        var anchor = InlineAnchorResolver.Resolve(
            Anchors,
            ReportPath,
            codeSnippet: "Console.WriteLine(\"done\");\nConsole.WriteLine(\"extra\");");

        // Fixture lines 21-22: both added, new 43 and new 44. The run's last line is the anchor,
        // the first is the start of the range.
        Assert.That(anchor.LineType, Is.EqualTo(DiffLineType.Added));
        Assert.That(anchor.Line, Is.EqualTo(44));
        Assert.That(anchor.Inline.To, Is.EqualTo(44));
        Assert.That(anchor.Inline.StartTo, Is.EqualTo(43));
        Assert.That(anchor.Inline.From, Is.Null);
        Assert.That(anchor.Inline.StartFrom, Is.Null);
        Assert.That(anchor.MatchedText, Is.EqualTo("        Console.WriteLine(\"done\");\n        Console.WriteLine(\"extra\");"));
    }

    [Test]
    public void SnippetCopiedWithItsDiffMarkerStillMatches()
    {
        var anchor = InlineAnchorResolver.Resolve(
            Anchors,
            ReportPath,
            codeSnippet: "+        Console.WriteLine(\"extra\");");

        Assert.That(anchor.LineType, Is.EqualTo(DiffLineType.Added));
        Assert.That(anchor.Line, Is.EqualTo(44));
        Assert.That(anchor.Inline.To, Is.EqualTo(44));
    }

    [Test]
    public void AmbiguousSnippetListsEveryCandidateWithItsLineNumber()
    {
        // Fixture line 20 (context, new 42) and line 21 (added, new 43) are the same text.
        var exception = Expect.Throws<InlineAnchorException>(() => InlineAnchorResolver.Resolve(Anchors, ReportPath, codeSnippet: "Console.WriteLine(\"done\");"));

        Assert.That(exception.Message, Does.Contain("matches 2 places"));
        Assert.That(exception.Message, Does.Contain("line 42 after the change (lineType=CONTEXT)"));
        Assert.That(exception.Message, Does.Contain("line 43 after the change (lineType=ADDED)"));
        Assert.That(exception.Message, Does.Contain("Re-send the same codeSnippet together with line"));
    }

    /// <summary>
    /// The tie-breaker the ambiguity error asks for: the same snippet plus one of the offered line
    /// numbers. 42 is the context line (fixture 20), 43 the added one (fixture 21).
    /// </summary>
    [TestCase(42, "CONTEXT")]
    [TestCase(43, "ADDED")]
    public void LineBreaksTheTieBetweenSeveralSnippetMatches(int line, string expectedType)
    {
        var anchor = InlineAnchorResolver.Resolve(
            Anchors,
            ReportPath,
            codeSnippet: "Console.WriteLine(\"done\");",
            line: line);

        Assert.That(anchor.LineType, Is.EqualTo(ParseLineType(expectedType)));
        Assert.That(anchor.Line, Is.EqualTo(line));
        Assert.That(anchor.Inline.To, Is.EqualTo(line));
    }

    [Test]
    public void SnippetPlusStartLineProducesAMultiLineAnchor()
    {
        var anchor = InlineAnchorResolver.Resolve(
            Anchors,
            ReportPath,
            codeSnippet: "Console.WriteLine(\"extra\");",
            startLine: 42);

        Assert.That(anchor.Inline.To, Is.EqualTo(44));
        Assert.That(anchor.Inline.StartTo, Is.EqualTo(42));
    }

    /// <summary>
    /// The snippet picks the side, so <c>startLine</c> is read on that side too: "return total;"
    /// resolves to the removed line (old 17), and old 13 is a line the diff shows before the change.
    /// </summary>
    [Test]
    public void SnippetOnTheOldSideTakesItsStartLineFromTheOldSide()
    {
        var anchor = InlineAnchorResolver.Resolve(
            Anchors,
            ReportPath,
            codeSnippet: "return total;",
            startLine: 13);

        Assert.That(anchor.Inline.From, Is.EqualTo(17));
        Assert.That(anchor.Inline.StartFrom, Is.EqualTo(13));
        Assert.That(anchor.Inline.To, Is.Null);
        Assert.That(anchor.Inline.StartTo, Is.Null);
    }

    // -----------------------------------------------------------------------------------------
    // Explicit line + lineType
    // -----------------------------------------------------------------------------------------

    [Test]
    public void AddedLineTypeAnchorsToTheNewSide()
    {
        var anchor = InlineAnchorResolver.Resolve(Anchors, ReportPath, line: 13, lineType: DiffLineType.Added);

        Assert.That(anchor.Inline.To, Is.EqualTo(13));
        Assert.That(anchor.Inline.From, Is.Null);
        Assert.That(anchor.LineType, Is.EqualTo(DiffLineType.Added));
        Assert.That(anchor.MatchedText, Is.EqualTo("        foreach (var item in Items)"));
    }

    [Test]
    public void RemovedLineTypeAnchorsToTheOldSide()
    {
        // Old line 13 and new line 13 are different lines of the diff (fixture 9 and 10); the side
        // is what tells them apart.
        var anchor = InlineAnchorResolver.Resolve(Anchors, ReportPath, line: 13, lineType: DiffLineType.Removed);

        Assert.That(anchor.Inline.From, Is.EqualTo(13));
        Assert.That(anchor.Inline.To, Is.Null);
        Assert.That(anchor.LineType, Is.EqualTo(DiffLineType.Removed));
        Assert.That(anchor.MatchedText, Is.EqualTo("        foreach (var item in items)"));
    }

    [Test]
    public void ContextLineTypeAnchorsToTheNewSide()
    {
        var anchor = InlineAnchorResolver.Resolve(Anchors, ReportPath, line: 42, lineType: DiffLineType.Context);

        Assert.That(anchor.Inline.To, Is.EqualTo(42));
        Assert.That(anchor.Inline.From, Is.Null);
        Assert.That(anchor.LineType, Is.EqualTo(DiffLineType.Context));
    }

    [Test]
    public void LineTypeDefaultsToAdded()
    {
        var anchor = InlineAnchorResolver.Resolve(Anchors, ReportPath, line: 13);

        Assert.That(anchor.Inline.To, Is.EqualTo(13));
        Assert.That(anchor.LineType, Is.EqualTo(DiffLineType.Added));
    }

    [Test]
    public void ExplicitStartLineSpansTheRangeOnTheRequestedSide()
    {
        var newSide = InlineAnchorResolver.Resolve(
            Anchors,
            ReportPath,
            line: 45,
            lineType: DiffLineType.Context,
            startLine: 42);

        Assert.That(newSide.Inline.To, Is.EqualTo(45));
        Assert.That(newSide.Inline.StartTo, Is.EqualTo(42));
        Assert.That(newSide.Inline.StartFrom, Is.Null);

        var oldSide = InlineAnchorResolver.Resolve(
            Anchors,
            ReportPath,
            line: 17,
            lineType: DiffLineType.Removed,
            startLine: 13);

        Assert.That(oldSide.Inline.From, Is.EqualTo(17));
        Assert.That(oldSide.Inline.StartFrom, Is.EqualTo(13));
        Assert.That(oldSide.Inline.StartTo, Is.Null);
    }

    // -----------------------------------------------------------------------------------------
    // Paths
    // -----------------------------------------------------------------------------------------

    [TestCase("./src/Report.cs")]
    [TestCase("src\\Report.cs")]
    [TestCase("/src/Report.cs")]
    [TestCase("SRC/report.CS")]
    [TestCase("  src/Report.cs  ")]
    public void CallerReshapedPathsResolveToTheCanonicalDiffPath(string path)
    {
        var anchor = InlineAnchorResolver.Resolve(Anchors, path, codeSnippet: "item.Value");

        Assert.That(anchor.Inline.Path, Is.EqualTo(ReportPath));
    }

    [Test]
    public void UnknownPathListsThePathsTheDiffDoesContain()
    {
        var exception = Expect.Throws<InlineAnchorException>(() => InlineAnchorResolver.Resolve(DiffFixtures.Split(DiffFixtures.MultiFile), "src/Nope.cs", codeSnippet: "anything"));

        Assert.That(exception.Message, Does.Contain("This diff contains no file named src/Nope.cs."));
        Assert.That(exception.Message, Does.Contain("src/Calculator.cs"));
        Assert.That(exception.Message, Does.Contain("docs/new notes.md"));
        Assert.That(exception.Message, Does.Contain("assets/icon.ico"));
        Assert.That(exception.Message, Does.Contain("Use a path exactly as the diff spells it."));
    }

    [Test]
    public void EmptyDiffSaysToFetchOneFirst()
    {
        var exception = Expect.Throws<InlineAnchorException>(() => InlineAnchorResolver.Resolve([], "src/Report.cs", codeSnippet: "anything"));

        Assert.That(exception.Message, Does.Contain("The diff is empty"));
        Assert.That(exception.Message, Does.Contain("getPullRequestDiff"));
    }

    [TestCase("")]
    [TestCase("   ")]
    public void BlankPathIsRejected(string path)
    {
        var exception = Expect.Throws<InlineAnchorException>(() => InlineAnchorResolver.Resolve(Anchors, path, codeSnippet: "item.Value"));

        Assert.That(exception.Message, Does.Contain("needs the path of the file"));
    }

    // -----------------------------------------------------------------------------------------
    // Error paths
    // -----------------------------------------------------------------------------------------

    [Test]
    public void BinaryFileHasNoLinesToAnchorTo()
    {
        var exception = Expect.Throws<InlineAnchorException>(() => InlineAnchorResolver.Resolve(DiffFixtures.Split(DiffFixtures.MultiFile), "assets/logo.png", codeSnippet: "anything"));

        Assert.That(exception.Message, Does.Contain("assets/logo.png is a binary file"));
        Assert.That(exception.Message, Does.Contain("comment on the pull request itself"));
    }

    [Test]
    public void FileWhoseHunksWereNotIncludedSaysToFetchThem()
    {
        var files = UnifiedDiffParser.Split(
            "diff --git a/src/Empty.cs b/src/Empty.cs\n"
            + "index 1111111..2222222 100644\n"
            + "--- a/src/Empty.cs\n"
            + "+++ b/src/Empty.cs\n");

        var exception = Expect.Throws<InlineAnchorException>(() => InlineAnchorResolver.Resolve(files, "src/Empty.cs", codeSnippet: "anything"));

        Assert.That(exception.Message, Does.Contain("contains no changed lines"));
        Assert.That(exception.Message, Does.Contain("paths=[\"src/Empty.cs\"]"));
    }

    /// <summary>
    /// A snippet that matches nothing gets the five lines with the most token overlap. Tokens of
    /// <c>total = item.Value + total;</c> are {total, item, Value}; six diff lines share at least
    /// one, and they are ranked by the summed length of the shared tokens:
    /// <c>total += item.Value;</c> (14) beats <c>var total = 0;</c>, <c>return total;</c> and
    /// <c>return total; // fixed</c> (5 each), which beat the two <c>foreach</c> lines (4 each) —
    /// so the last <c>foreach</c> line falls off the end of the list.
    /// </summary>
    [Test]
    public void NoMatchListsTheFiveNearestLines()
    {
        var exception = Expect.Throws<InlineAnchorException>(() => InlineAnchorResolver.Resolve(Anchors, ReportPath, codeSnippet: "total = item.Value + total;"));

        Assert.That(exception.Message, Does.Contain("No line in the diff of src/Report.cs matches that codeSnippet"));
        Assert.That(exception.Message, Does.Contain("The closest lines it does show are:"));
        Assert.That(CountOccurrences(exception.Message, "\n  line "), Is.EqualTo(5));

        Assert.That(exception.Message, Does.Contain("line 15 after the change (lineType=CONTEXT): total += item.Value;"));
        Assert.That(exception.Message, Does.Contain("line 12 after the change (lineType=CONTEXT): var total = 0;"));
        Assert.That(exception.Message, Does.Contain("line 17 before the change (lineType=REMOVED): return total;"));
        Assert.That(exception.Message, Does.Contain("line 17 after the change (lineType=ADDED): return total; // fixed"));
        Assert.That(exception.Message, Does.Contain("line 13 before the change (lineType=REMOVED): foreach (var item in items)"));
    }

    [Test]
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

        var exception = Expect.Throws<InlineAnchorException>(() => InlineAnchorResolver.Resolve(files, "src/Repeat.cs", codeSnippet: "Log(\"x\");"));

        Assert.That(exception.Message, Does.Contain("matches 25 places"));
        Assert.That(CountOccurrences(exception.Message, "\n  line "), Is.EqualTo(20));
        Assert.That(exception.Message, Does.Contain("… and 5 more."));
    }

    [TestCase(19, "ADDED", "the file after the change")]
    [TestCase(39, "CONTEXT", "the file after the change")]
    [TestCase(45, "REMOVED", "the file before the change")]
    public void LineOutsideTheDiffListsWhatBothSidesDoShow(int line, string lineType, string side)
    {
        var exception = Expect.Throws<InlineAnchorException>(() => InlineAnchorResolver.Resolve(Anchors, ReportPath, line: line, lineType: ParseLineType(lineType)));

        Assert.That(exception.Message, Does.Contain(string.Create(CultureInfo.InvariantCulture, $"line={line} is not a line the diff of src/Report.cs shows for {side}")));
        Assert.That(exception.Message, Does.Contain("Lines available after the change (lineType=ADDED or CONTEXT): 10-18, 40-46"));
        Assert.That(exception.Message, Does.Contain("Lines available before the change (lineType=REMOVED): 10-18, 40-44"));
    }

    [Test]
    public void StartLineMustBeSmallerThanTheCommentedLine()
    {
        var exception = Expect.Throws<InlineAnchorException>(() => InlineAnchorResolver.Resolve(Anchors, ReportPath, line: 45, lineType: DiffLineType.Context, startLine: 45));

        Assert.That(exception.Message, Does.Contain("startLine (45) must be smaller than the commented line (45)"));
    }

    [Test]
    public void StartLineMustAlsoBeVisibleInTheDiff()
    {
        var exception = Expect.Throws<InlineAnchorException>(() => InlineAnchorResolver.Resolve(Anchors, ReportPath, line: 45, lineType: DiffLineType.Context, startLine: 19));

        Assert.That(exception.Message, Does.Contain("startLine=19 is not a line the diff of src/Report.cs shows"));
    }

    /// <summary>
    /// The same rule in snippet mode, which used to trust <c>startLine</c> outright: the snippet
    /// resolves to new line 44, but 19 is in neither hunk, so a comment starting there would be
    /// anchored to a line nothing in this response shows.
    /// </summary>
    [Test]
    public void StartLineIsCheckedInSnippetModeToo()
    {
        var exception = Expect.Throws<InlineAnchorException>(() => InlineAnchorResolver.Resolve(
            Anchors,
            ReportPath,
            codeSnippet: "Console.WriteLine(\"extra\");",
            startLine: 19));

        Assert.That(exception.Message, Does.Contain("startLine=19 is not a line the diff of src/Report.cs shows for the file after the change"));

        // The range listing is what turns the refusal into a usable retry.
        Assert.That(exception.Message, Does.Contain("Lines available after the change (lineType=ADDED or CONTEXT): 10-18, 40-46"));
        Assert.That(exception.Message, Does.Contain("Lines available before the change (lineType=REMOVED): 10-18, 40-44"));
    }

    /// <summary>
    /// And on the old side: "return total;" is the removed line (old 17), so <c>startLine</c> is
    /// read against the file before the change, where line 9 is one line above the first hunk.
    /// </summary>
    [Test]
    public void SnippetStartLineIsCheckedOnTheSideTheSnippetResolvedTo()
    {
        var exception = Expect.Throws<InlineAnchorException>(() => InlineAnchorResolver.Resolve(
            Anchors,
            ReportPath,
            codeSnippet: "return total;",
            startLine: 9));

        Assert.That(exception.Message, Does.Contain("startLine=9 is not a line the diff of src/Report.cs shows for the file before the change"));
    }

    [Test]
    public void SnippetSpanningBothSidesIsRejected()
    {
        // Fixture lines 9-10: one removed line immediately followed by its replacement. The run
        // matches, but it cannot be one anchor.
        var exception = Expect.Throws<InlineAnchorException>(() => InlineAnchorResolver.Resolve(
                Anchors,
                ReportPath,
                codeSnippet: "foreach (var item in items)\nforeach (var item in Items)"));

        Assert.That(exception.Message, Does.Contain("spans both removed and added lines"));
        Assert.That(exception.Message, Does.Contain("lineType=ADDED"));
        Assert.That(exception.Message, Does.Contain("lineType=REMOVED"));
    }

    [Test]
    public void NeitherSnippetNorLineIsAnError()
    {
        var exception = Expect.Throws<InlineAnchorException>(() => InlineAnchorResolver.Resolve(Anchors, ReportPath));

        Assert.That(exception.Message, Does.Contain("without knowing where"));
        Assert.That(exception.Message, Does.Contain("codeSnippet"));
    }

    [TestCase(0)]
    [TestCase(-1)]
    public void LineMustBePositive(int line)
    {
        var exception = Expect.Throws<InlineAnchorException>(() => InlineAnchorResolver.Resolve(Anchors, ReportPath, line: line));

        Assert.That(exception.Message, Does.Contain("line must be 1 or greater"));
    }

    [Test]
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
