using System.Buffers.Text;
using System.Net;
using System.Text;

using Bitbucket.Mcp.Http;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace Bitbucket.Mcp.Tests.Http;

/// <summary>
/// Covers the pagination cursor and the request-forgery guard inside it.
/// </summary>
/// <remarks>
/// A cursor is a tool argument, so it arrives from the model, whose context is full of text an
/// attacker can write — pull request descriptions, comments, diff hunks. Every rejection case here
/// is a URL that would otherwise be fetched with a live <c>Authorization</c> header attached.
/// </remarks>
public class BitbucketCursorTests
{
    private const string NextUrl =
        "https://api.bitbucket.org/2.0/repositories/acme/widget-api/pullrequests?fields=next%2Csize&page=2&pagelen=3";

    /// <summary>URLs that must never be reachable through a cursor, and why.</summary>
    public static TheoryData<string> UnsafeUrls => new()
    {
        "http://api.bitbucket.org/2.0/repositories/acme/widget-api/pullrequests",   // plaintext
        "https://evil.example/2.0/repositories/acme/widget-api/pullrequests",       // wrong host
        "https://api.bitbucket.org.evil.example/2.0/pullrequests",                  // host suffix trick
        "https://api.bitbucket.org:8443/2.0/pullrequests",                          // non-default port
        "https://user:secret@api.bitbucket.org/2.0/pullrequests",                   // embedded credentials
        "https://api.bitbucket.org/1.0/repositories/acme/widget-api/pullrequests",  // outside /2.0/
        "https://api.bitbucket.org/internal/pullrequests",                          // outside /2.0/
        "https://169.254.169.254/2.0/",                                             // cloud metadata
        "file:///etc/passwd",                                                       // not http at all
        "not a url at all",
    };

    [Fact]
    public void RoundTripsANextUrl()
    {
        var cursor = BitbucketCursor.Encode(NextUrl);

        Assert.NotNull(cursor);
        Assert.NotEqual(NextUrl, cursor);

        Assert.True(BitbucketCursor.TryDecode(cursor, out var decoded));
        Assert.Equal(NextUrl, decoded);
    }

    [Fact]
    public void EncodesToUrlAndJsonSafeCharactersOnly()
    {
        var cursor = BitbucketCursor.Encode(NextUrl);

        Assert.NotNull(cursor);
        Assert.All(cursor, c => Assert.True(
            char.IsAsciiLetterOrDigit(c) || c is '-' or '_',
            $"'{c}' is outside the base64url alphabet."));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EncodeReturnsNullWhenThereIsNoNextPage(string? nextUrl) =>
        Assert.Null(BitbucketCursor.Encode(nextUrl));

    [Theory]
    [MemberData(nameof(UnsafeUrls))]
    public void EncodeRefusesUrlsItWouldNotAcceptBack(string nextUrl) =>
        Assert.Null(BitbucketCursor.Encode(nextUrl));

    [Theory]
    [MemberData(nameof(UnsafeUrls))]
    public void TryDecodeRejectsUnsafeUrls(string url)
    {
        var cursor = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(url));

        Assert.False(BitbucketCursor.TryDecode(cursor, out var decoded));
        Assert.Null(decoded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!not-base64url!!!")]
    [InlineData("aGVsbG8+")]        // '+' belongs to standard base64, not base64url
    [InlineData("aGVsbG8/d29ybGQ")] // '/' likewise
    [InlineData("äöü")]
    public void TryDecodeRejectsMalformedInputWithoutThrowing(string? cursor)
    {
        // Base64Url.TryDecodeFromChars is only "try" about the destination buffer: it throws
        // FormatException on a malformed input. TryDecode gates on Base64Url.IsValid first, and
        // this test is what keeps that gate in place.
        Assert.False(BitbucketCursor.TryDecode(cursor, out var decoded));
        Assert.Null(decoded);
    }

    [Fact]
    public void TryDecodeRejectsAValidEncodingOfSomethingThatIsNotAUrl()
    {
        var cursor = Base64Url.EncodeToString("hello"u8.ToArray());

        Assert.False(BitbucketCursor.TryDecode(cursor, out _));
    }

    [Fact]
    public void TryDecodeRejectsInvalidUtf8()
    {
        var cursor = Base64Url.EncodeToString([0xFF, 0xFE, 0xFD, 0xFC]);

        Assert.False(BitbucketCursor.TryDecode(cursor, out _));
    }

    [Fact]
    public void TryDecodeRejectsAnOversizedCursorBeforeAllocating()
    {
        var cursor = new string('A', 8192);

        Assert.False(BitbucketCursor.TryDecode(cursor, out _));
    }

    [Theory]
    [InlineData("https://api.bitbucket.org/2.0/", true)]
    [InlineData("https://API.BITBUCKET.ORG/2.0/pullrequests", true)]
    [InlineData("https://api.bitbucket.org/2.0", false)]
    [InlineData("https://api.bitbucket.org/", false)]
    [InlineData(null, false)]
    public void IsBitbucketApiUrlMatchesTheDocumentedRules(string? url, bool expected) =>
        Assert.Equal(expected, BitbucketCursor.IsBitbucketApiUrl(url));

    [Fact]
    public async Task ListPullRequestsWithACursorRequestsExactlyTheDecodedUrl()
    {
        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson("""{"values":[]}""");

        using var client = CreateClient(stub);

        _ = await client.ListPullRequestsAsync(
            "acme",
            "widget-api",
            states: ["OPEN"],
            author: "jane",
            pageSize: 25,
            cursor: BitbucketCursor.Encode(NextUrl),
            cancellationToken: TestContext.Current.CancellationToken);

        var request = Assert.Single(stub.Requests);

        // Byte for byte: a cursor is Bitbucket's own opaque URL and re-composing it would drop the
        // query state it encodes. The filters passed alongside it are ignored for the same reason.
        Assert.Equal(NextUrl, request.Uri?.AbsoluteUri);
    }

    [Fact]
    public async Task DiffStatWithACursorRequestsExactlyTheDecodedUrl()
    {
        const string DiffStatNext =
            "https://api.bitbucket.org/2.0/repositories/acme/widget-api/pullrequests/412/diffstat?page=2&pagelen=5";

        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson("""{"values":[]}""");

        using var client = CreateClient(stub);

        _ = await client.GetDiffStatAsync(
            "acme",
            "widget-api",
            412,
            cursor: BitbucketCursor.Encode(DiffStatNext),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(DiffStatNext, Assert.Single(stub.Requests).Uri?.AbsoluteUri);
    }

    [Fact]
    public async Task CommentsWithACursorRequestExactlyTheDecodedUrl()
    {
        const string CommentsNext =
            "https://api.bitbucket.org/2.0/repositories/acme/widget-api/pullrequests/412/comments?page=3&pagelen=50";

        using var stub = new StubHttpMessageHandler();
        stub.EnqueueJson("""{"values":[]}""");

        using var client = CreateClient(stub);

        _ = await client.GetCommentsAsync(
            "acme",
            "widget-api",
            412,
            cursor: BitbucketCursor.Encode(CommentsNext),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(CommentsNext, Assert.Single(stub.Requests).Uri?.AbsoluteUri);
    }

    [Theory]
    [MemberData(nameof(UnsafeUrls))]
    public async Task AHostileCursorIsRejectedBeforeAnyRequestIsMade(string url)
    {
        var cursor = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(url));

        using var stub = new StubHttpMessageHandler
        {
            Fallback = _ => StubHttpMessageHandler.CreateResponse(HttpStatusCode.OK, """{"values":[]}"""),
        };

        using var client = CreateClient(stub);

        var exception = await Assert.ThrowsAsync<InvalidCursorException>(() => client.ListPullRequestsAsync(
            "acme",
            "widget-api",
            cursor: cursor,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(cursor, exception.Cursor);

        // The point of the guard: nothing left the process, so no credential was offered to the
        // host the cursor named.
        Assert.Empty(stub.Requests);

        // The offending cursor stays out of the user-visible message.
        Assert.DoesNotContain(cursor, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMalformedCursorIsRejectedBeforeAnyRequestIsMade()
    {
        using var stub = new StubHttpMessageHandler();
        using var client = CreateClient(stub);

        await Assert.ThrowsAsync<InvalidCursorException>(() => client.GetCommentsAsync(
            "acme",
            "widget-api",
            412,
            cursor: "!!!not-base64url!!!",
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Empty(stub.Requests);
    }

    private static BitbucketApiClient CreateClient(StubHttpMessageHandler stub) =>
        new(new StubCredentialProvider(), NullLoggerFactory.Instance, stub, baseAddress: null, timeProvider: new ManualTimeProvider());
}
