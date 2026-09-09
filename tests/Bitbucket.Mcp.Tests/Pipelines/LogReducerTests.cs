using System.Text;

using Bitbucket.Mcp.Pipelines;

using Xunit;

namespace Bitbucket.Mcp.Tests.Pipelines;

/// <summary>
/// The log reducers, driven straight off a <see cref="MemoryStream"/>. Nothing here touches HTTP:
/// the byte ring, the partial-line rule and every marker string are decidable without a transport,
/// and they are the only genuinely novel logic in the pipeline surface.
/// </summary>
public sealed class LogReducerTests
{
    private const string Esc = "\u001b";

    [Fact]
    public async Task TailKeepsTheLastLinesWhenTheWholeLogFitsTheRing()
    {
        using var stream = Stream("alpha\nbravo\ncharlie\ndelta\n");

        var excerpt = await LogReducer.TailAsync(
            stream,
            maxLines: 2,
            byteBudget: 4096,
            rangeStart: 0,
            totalBytes: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, excerpt.LinesShown);
        Assert.Contains("charlie", excerpt.Text, StringComparison.Ordinal);
        Assert.Contains("delta", excerpt.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("alpha", excerpt.Text, StringComparison.Ordinal);
        Assert.True(excerpt.Truncated);
        Assert.NotNull(excerpt.Hint);
    }

    [Fact]
    public async Task TailOfAShortLogIsNotReportedAsTruncated()
    {
        using var stream = Stream("only\ntwo\n");

        var excerpt = await LogReducer.TailAsync(
            stream, maxLines: 50, byteBudget: 4096, rangeStart: 0, totalBytes: null,
            TestContext.Current.CancellationToken);

        Assert.False(excerpt.Truncated);
        Assert.Null(excerpt.Hint);
        Assert.Equal(2, excerpt.LinesShown);
        Assert.Equal(9, excerpt.TotalBytes);
    }

    /// <summary>
    /// The 200 path: storage ignored <c>Range</c> and sent the whole log. The ring must still bound
    /// memory and still yield the tail — this is the branch a very large log actually takes.
    /// </summary>
    [Fact]
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
            TestContext.Current.CancellationToken);

        Assert.Equal(3, excerpt.LinesShown);
        Assert.Contains("line 19999", excerpt.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("line 0\n", excerpt.Text, StringComparison.Ordinal);
        Assert.True(excerpt.Truncated);

        // The ring is the memory bound, and the reported total is the whole stream.
        Assert.Equal(512, excerpt.BytesShown);
        Assert.Equal(builder.Length, excerpt.TotalBytes);
    }

    /// <summary>
    /// A ranged read starts mid-line, so the first line is half a line. Reporting it whole is how a
    /// model ends up diagnosing a build from a fragment.
    /// </summary>
    [Fact]
    public async Task TailDropsThePartialFirstLineOfARangedRead()
    {
        using var stream = Stream("dle of a cut line\nsecond\nthird\n");

        var excerpt = await LogReducer.TailAsync(
            stream, maxLines: 50, byteBudget: 4096, rangeStart: 900, totalBytes: 1000,
            TestContext.Current.CancellationToken);

        Assert.DoesNotContain("dle of a cut line", excerpt.Text, StringComparison.Ordinal);
        Assert.Contains("second", excerpt.Text, StringComparison.Ordinal);
        Assert.Equal(2, excerpt.LinesShown);
        Assert.True(excerpt.Truncated);
        Assert.Equal(1000, excerpt.TotalBytes);
    }

    [Fact]
    public async Task TailMarkerQuantifiesWhatWasLeftOut()
    {
        using var stream = Stream("one\ntwo\nthree\n");

        var excerpt = await LogReducer.TailAsync(
            stream, maxLines: 1, byteBudget: 4096, rangeStart: 0, totalBytes: 14,
            TestContext.Current.CancellationToken);

        Assert.Contains("showing the last 1 line", excerpt.Text, StringComparison.Ordinal);
        Assert.Contains("14 bytes", excerpt.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HeadKeepsTheStartAndSaysSo()
    {
        using var stream = Stream("first\nsecond\nthird\nfourth\n");

        var excerpt = await LogReducer.HeadAsync(
            stream, maxLines: 2, byteBudget: 4096, totalBytes: null, TestContext.Current.CancellationToken);

        Assert.Equal(2, excerpt.LinesShown);
        Assert.Contains("first", excerpt.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("third", excerpt.Text, StringComparison.Ordinal);
        Assert.Contains("showing the first 2 lines", excerpt.Text, StringComparison.Ordinal);
        Assert.True(excerpt.Truncated);
    }

    [Fact]
    public async Task SearchEmitsMatchesWithContextOnEitherSide()
    {
        using var stream = Stream("a\nb\nBOOM here\nd\ne\n");

        var excerpt = await LogReducer.SearchAsync(
            stream, "boom", contextLines: 1, maxLines: 100, totalBytes: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, excerpt.MatchCount);
        Assert.Contains("b\n", excerpt.Text, StringComparison.Ordinal);
        Assert.Contains("BOOM here", excerpt.Text, StringComparison.Ordinal);
        Assert.Contains("d\n", excerpt.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("\ne\n", excerpt.Text, StringComparison.Ordinal);
        Assert.False(excerpt.Truncated);
    }

    [Fact]
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
            TestContext.Current.CancellationToken);

        Assert.Equal(2, excerpt.MatchCount);
        Assert.Contains("50 lines skipped", excerpt.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Counting continues past the cap so the marker can say how many matches there really were —
    /// "3 of 40" tells the caller to narrow the pattern; "3, and possibly more" does not.
    /// </summary>
    [Fact]
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
            TestContext.Current.CancellationToken);

        Assert.True(excerpt.Truncated);
        Assert.Equal(3, excerpt.LinesShown);
        Assert.Equal(40, excerpt.MatchCount);
        Assert.Contains("3 lines shown of 40 matches", excerpt.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchThatMatchesNothingSaysSoAndPointsAtTheTail()
    {
        using var stream = Stream("nothing to see\nhere at all\n");

        var excerpt = await LogReducer.SearchAsync(
            stream, "kaboom", contextLines: 2, maxLines: 100, totalBytes: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, excerpt.MatchCount);
        Assert.False(excerpt.Truncated);
        Assert.Contains("no line of this step's log contains \"kaboom\"", excerpt.Text, StringComparison.Ordinal);
        Assert.Contains("Omit pattern", excerpt.Hint!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A regex-shaped pattern is matched literally. This is the guarantee that keeps a
    /// model-supplied pattern from being an unbounded-backtracking denial of service.
    /// </summary>
    [Fact]
    public async Task SearchTreatsThePatternAsALiteralSubstring()
    {
        using var stream = Stream("plain text\na+b+c\n");

        var excerpt = await LogReducer.SearchAsync(
            stream, "a+b+", contextLines: 0, maxLines: 100, totalBytes: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, excerpt.MatchCount);
        Assert.Contains("a+b+c", excerpt.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Esc + "[1;31mERROR" + Esc + "[0m", "ERROR")]
    [InlineData(Esc + "[0m", "")]
    [InlineData("no escapes here", "no escapes here")]
    [InlineData(Esc + "]0;window title\aafter", "after")]
    [InlineData("keep " + Esc + "[32mgreen" + Esc + "[39m text", "keep green text")]
    public void AnsiSequencesAreStripped(string input, string expected) =>
        Assert.Equal(expected, LogReducer.StripAnsi(input));

    /// <summary>An unterminated escape is not a sequence, so it survives rather than eating the line.</summary>
    [Fact]
    public void AnUnterminatedEscapeIsLeftAlone() =>
        Assert.Equal(Esc + "[1;31", LogReducer.StripAnsi(Esc + "[1;31"));

    [Fact]
    public async Task ColouredOutputIsStrippedOnTheWayThroughTheTail()
    {
        using var stream = Stream(Esc + "[1;31mERROR: build failed" + Esc + "[0m\n");

        var excerpt = await LogReducer.TailAsync(
            stream, maxLines: 10, byteBudget: 4096, rangeStart: 0, totalBytes: null,
            TestContext.Current.CancellationToken);

        Assert.Equal("ERROR: build failed\n", excerpt.Text);
    }

    [Fact]
    public async Task CarriageReturnsAreNotKeptAsPartOfTheLine()
    {
        using var stream = Stream("alpha\r\nbravo\r\n");

        var excerpt = await LogReducer.TailAsync(
            stream, maxLines: 10, byteBudget: 4096, rangeStart: 0, totalBytes: null,
            TestContext.Current.CancellationToken);

        Assert.Equal("alpha\nbravo\n", excerpt.Text);
    }

    [Fact]
    public async Task AnEmptyLogIsNotAnError()
    {
        using var stream = Stream(string.Empty);

        var excerpt = await LogReducer.TailAsync(
            stream, maxLines: 10, byteBudget: 4096, rangeStart: 0, totalBytes: null,
            TestContext.Current.CancellationToken);

        Assert.False(excerpt.Truncated);
        Assert.Equal(0, excerpt.LinesShown);
        Assert.Empty(excerpt.Text);
    }

    private static MemoryStream Stream(string content) => new(Encoding.UTF8.GetBytes(content));
}
