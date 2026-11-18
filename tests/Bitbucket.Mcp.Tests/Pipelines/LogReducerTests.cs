using System.Text;

using Bitbucket.Mcp.Pipelines;


namespace Bitbucket.Mcp.Tests.Pipelines;

/// <summary>
/// The log reducers, driven straight off a <see cref="MemoryStream"/>. Nothing here touches HTTP:
/// the byte ring, the partial-line rule and every marker string are decidable without a transport,
/// and they are the only genuinely novel logic in the pipeline surface.
/// </summary>
public sealed class LogReducerTests
{
    private const string Esc = "\u001b";

    [Test]
    public async Task TailKeepsTheLastLinesWhenTheWholeLogFitsTheRing()
    {
        using var stream = Stream("alpha\nbravo\ncharlie\ndelta\n");

        var excerpt = await LogReducer.TailAsync(
            stream,
            maxLines: 2,
            byteBudget: 4096,
            rangeStart: 0,
            totalBytes: null,
            TestContext.CurrentContext.CancellationToken);

        Assert.That(excerpt.LinesShown, Is.EqualTo(2));
        Assert.That(excerpt.Text, Does.Contain("charlie"));
        Assert.That(excerpt.Text, Does.Contain("delta"));
        Assert.That(excerpt.Text, Does.Not.Contain("alpha"));
        Assert.That(excerpt.Truncated, Is.True);
        Assert.That(excerpt.Hint, Is.Not.Null);
    }

    [Test]
    public async Task TailOfAShortLogIsNotReportedAsTruncated()
    {
        using var stream = Stream("only\ntwo\n");

        var excerpt = await LogReducer.TailAsync(
            stream, maxLines: 50, byteBudget: 4096, rangeStart: 0, totalBytes: null,
            TestContext.CurrentContext.CancellationToken);

        Assert.That(excerpt.Truncated, Is.False);
        Assert.That(excerpt.Hint, Is.Null);
        Assert.That(excerpt.LinesShown, Is.EqualTo(2));
        Assert.That(excerpt.TotalBytes, Is.EqualTo(9));
    }

    /// <summary>
    /// The 200 path: storage ignored <c>Range</c> and sent the whole log. The ring must still bound
    /// memory and still yield the tail — this is the branch a very large log actually takes.
    /// </summary>
    [Test]
    public async Task TailStreamsALogFarLargerThanTheRingAndStillEndsAtTheEnd()
    {
        var builder = new StringBuilder();

        for (var i = 0; i < 20_000; i++)
        {
            builder.Append("line ").Append(i).Append('\n');
        }

        using var stream = Stream(builder.ToString());

        var excerpt = await LogReducer.TailAsync(
            stream, maxLines: 3, byteBudget: 512, rangeStart: 0, totalBytes: null,
            TestContext.CurrentContext.CancellationToken);

        Assert.That(excerpt.LinesShown, Is.EqualTo(3));
        Assert.That(excerpt.Text, Does.Contain("line 19999"));
        Assert.That(excerpt.Text, Does.Not.Contain("line 0\n"));
        Assert.That(excerpt.Truncated, Is.True);

        // The ring is the memory bound, and the reported total is the whole stream.
        Assert.That(excerpt.BytesShown, Is.EqualTo(512));
        Assert.That(excerpt.TotalBytes, Is.EqualTo(builder.Length));
    }

    /// <summary>
    /// A ranged read starts mid-line, so the first line is half a line. Reporting it whole is how a
    /// model ends up diagnosing a build from a fragment.
    /// </summary>
    [Test]
    public async Task TailDropsThePartialFirstLineOfARangedRead()
    {
        using var stream = Stream("dle of a cut line\nsecond\nthird\n");

        var excerpt = await LogReducer.TailAsync(
            stream, maxLines: 50, byteBudget: 4096, rangeStart: 900, totalBytes: 1000,
            TestContext.CurrentContext.CancellationToken);

        Assert.That(excerpt.Text, Does.Not.Contain("dle of a cut line"));
        Assert.That(excerpt.Text, Does.Contain("second"));
        Assert.That(excerpt.LinesShown, Is.EqualTo(2));
        Assert.That(excerpt.Truncated, Is.True);
        Assert.That(excerpt.TotalBytes, Is.EqualTo(1000));
    }

    [Test]
    public async Task TailMarkerQuantifiesWhatWasLeftOut()
    {
        using var stream = Stream("one\ntwo\nthree\n");

        var excerpt = await LogReducer.TailAsync(
            stream, maxLines: 1, byteBudget: 4096, rangeStart: 0, totalBytes: 14,
            TestContext.CurrentContext.CancellationToken);

        Assert.That(excerpt.Text, Does.Contain("showing the last 1 line"));
        Assert.That(excerpt.Text, Does.Contain("14 bytes"));
    }

    [Test]
    public async Task HeadKeepsTheStartAndSaysSo()
    {
        using var stream = Stream("first\nsecond\nthird\nfourth\n");

        var excerpt = await LogReducer.HeadAsync(
            stream, maxLines: 2, byteBudget: 4096, totalBytes: null, TestContext.CurrentContext.CancellationToken);

        Assert.That(excerpt.LinesShown, Is.EqualTo(2));
        Assert.That(excerpt.Text, Does.Contain("first"));
        Assert.That(excerpt.Text, Does.Not.Contain("third"));
        Assert.That(excerpt.Text, Does.Contain("showing the first 2 lines"));
        Assert.That(excerpt.Truncated, Is.True);
    }

    [Test]
    public async Task SearchEmitsMatchesWithContextOnEitherSide()
    {
        using var stream = Stream("a\nb\nBOOM here\nd\ne\n");

        var excerpt = await LogReducer.SearchAsync(
            stream, "boom", contextLines: 1, maxLines: 100, totalBytes: null,
            TestContext.CurrentContext.CancellationToken);

        Assert.That(excerpt.MatchCount, Is.EqualTo(1));
        Assert.That(excerpt.Text, Does.Contain("b\n"));
        Assert.That(excerpt.Text, Does.Contain("BOOM here"));
        Assert.That(excerpt.Text, Does.Contain("d\n"));
        Assert.That(excerpt.Text, Does.Not.Contain("\ne\n"));
        Assert.That(excerpt.Truncated, Is.False);
    }

    [Test]
    public async Task SearchSeparatesNonAdjacentBlocksWithASkippedMarker()
    {
        var builder = new StringBuilder("ERROR one\n");

        for (var i = 0; i < 50; i++)
        {
            builder.Append("filler ").Append(i).Append('\n');
        }

        builder.Append("ERROR two\n");

        using var stream = Stream(builder.ToString());

        var excerpt = await LogReducer.SearchAsync(
            stream, "ERROR", contextLines: 0, maxLines: 100, totalBytes: null,
            TestContext.CurrentContext.CancellationToken);

        Assert.That(excerpt.MatchCount, Is.EqualTo(2));
        Assert.That(excerpt.Text, Does.Contain("50 lines skipped"));
    }

    /// <summary>
    /// Counting continues past the cap so the marker can say how many matches there really were —
    /// "3 of 40" tells the caller to narrow the pattern; "3, and possibly more" does not.
    /// </summary>
    [Test]
    public async Task SearchCapsTheOutputButStillCountsEveryMatch()
    {
        var builder = new StringBuilder();

        for (var i = 0; i < 40; i++)
        {
            builder.Append("ERROR ").Append(i).Append('\n');
        }

        using var stream = Stream(builder.ToString());

        var excerpt = await LogReducer.SearchAsync(
            stream, "ERROR", contextLines: 0, maxLines: 3, totalBytes: null,
            TestContext.CurrentContext.CancellationToken);

        Assert.That(excerpt.Truncated, Is.True);
        Assert.That(excerpt.LinesShown, Is.EqualTo(3));
        Assert.That(excerpt.MatchCount, Is.EqualTo(40));
        Assert.That(excerpt.Text, Does.Contain("3 lines shown of 40 matches"));
    }

    [Test]
    public async Task SearchThatMatchesNothingSaysSoAndPointsAtTheTail()
    {
        using var stream = Stream("nothing to see\nhere at all\n");

        var excerpt = await LogReducer.SearchAsync(
            stream, "kaboom", contextLines: 2, maxLines: 100, totalBytes: null,
            TestContext.CurrentContext.CancellationToken);

        Assert.That(excerpt.MatchCount, Is.EqualTo(0));
        Assert.That(excerpt.Truncated, Is.False);
        Assert.That(excerpt.Text, Does.Contain("no line of this step's log contains \"kaboom\""));
        Assert.That(excerpt.Hint!, Does.Contain("Omit pattern"));
    }

    /// <summary>
    /// A regex-shaped pattern is matched literally. This is the guarantee that keeps a
    /// model-supplied pattern from being an unbounded-backtracking denial of service.
    /// </summary>
    [Test]
    public async Task SearchTreatsThePatternAsALiteralSubstring()
    {
        using var stream = Stream("plain text\na+b+c\n");

        var excerpt = await LogReducer.SearchAsync(
            stream, "a+b+", contextLines: 0, maxLines: 100, totalBytes: null,
            TestContext.CurrentContext.CancellationToken);

        Assert.That(excerpt.MatchCount, Is.EqualTo(1));
        Assert.That(excerpt.Text, Does.Contain("a+b+c"));
    }

    [TestCase(Esc + "[1;31mERROR" + Esc + "[0m", "ERROR")]
    [TestCase(Esc + "[0m", "")]
    [TestCase("no escapes here", "no escapes here")]
    [TestCase(Esc + "]0;window title\aafter", "after")]
    [TestCase("keep " + Esc + "[32mgreen" + Esc + "[39m text", "keep green text")]
    public void AnsiSequencesAreStripped(string input, string expected) =>
        Assert.That(LogReducer.StripAnsi(input), Is.EqualTo(expected));

    /// <summary>An unterminated escape is not a sequence, so it survives rather than eating the line.</summary>
    [Test]
    public void AnUnterminatedEscapeIsLeftAlone() =>
        Assert.That(LogReducer.StripAnsi(Esc + "[1;31"), Is.EqualTo(Esc + "[1;31"));

    [Test]
    public async Task ColouredOutputIsStrippedOnTheWayThroughTheTail()
    {
        using var stream = Stream(Esc + "[1;31mERROR: build failed" + Esc + "[0m\n");

        var excerpt = await LogReducer.TailAsync(
            stream, maxLines: 10, byteBudget: 4096, rangeStart: 0, totalBytes: null,
            TestContext.CurrentContext.CancellationToken);

        Assert.That(excerpt.Text, Is.EqualTo("ERROR: build failed\n"));
    }

    [Test]
    public async Task CarriageReturnsAreNotKeptAsPartOfTheLine()
    {
        using var stream = Stream("alpha\r\nbravo\r\n");

        var excerpt = await LogReducer.TailAsync(
            stream, maxLines: 10, byteBudget: 4096, rangeStart: 0, totalBytes: null,
            TestContext.CurrentContext.CancellationToken);

        Assert.That(excerpt.Text, Is.EqualTo("alpha\nbravo\n"));
    }

    [Test]
    public async Task AnEmptyLogIsNotAnError()
    {
        using var stream = Stream(string.Empty);

        var excerpt = await LogReducer.TailAsync(
            stream, maxLines: 10, byteBudget: 4096, rangeStart: 0, totalBytes: null,
            TestContext.CurrentContext.CancellationToken);

        Assert.That(excerpt.Truncated, Is.False);
        Assert.That(excerpt.LinesShown, Is.EqualTo(0));
        Assert.That(excerpt.Text, Is.Empty);
    }

    private static MemoryStream Stream(string content) => new(Encoding.UTF8.GetBytes(content));
}
