using System.Buffers.Text;
using System.Net;
using System.Text;

using Bitbucket.Mcp.Http;

using Microsoft.Extensions.Logging.Abstractions;


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
    public static string[] UnsafeUrls =>
    [
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
    ];

    [Test]
    public void RoundTripsANextUrl()
    {
        var cursor = BitbucketCursor.Encode(NextUrl);

        Assert.That(cursor, Is.Not.Null);
        Assert.That(cursor, Is.Not.EqualTo(NextUrl));

        Assert.That(BitbucketCursor.TryDecode(cursor, out var decoded), Is.True);
        Assert.That(decoded, Is.EqualTo(NextUrl));
    }

    [Test]
    public void EncodesToUrlAndJsonSafeCharactersOnly()
    {
        var cursor = BitbucketCursor.Encode(NextUrl);

        Assert.That(cursor, Is.Not.Null);
        Assert.That(
            cursor,
            Has.All.Matches<char>(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'),
            "Every character must be in the base64url alphabet.");
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void EncodeReturnsNullWhenThereIsNoNextPage(string? nextUrl) =>
        Assert.That(BitbucketCursor.Encode(nextUrl), Is.Null);

    [TestCaseSource(nameof(UnsafeUrls))]
    public void EncodeRefusesUrlsItWouldNotAcceptBack(string nextUrl) =>
        Assert.That(BitbucketCursor.Encode(nextUrl), Is.Null);

    [TestCaseSource(nameof(UnsafeUrls))]
    public void TryDecodeRejectsUnsafeUrls(string url)
    {
        var cursor = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(url));

        Assert.That(BitbucketCursor.TryDecode(cursor, out var decoded), Is.False);
        Assert.That(decoded, Is.Null);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("!!!not-base64url!!!")]
    [TestCase("aGVsbG8+")]        // '+' belongs to standard base64, not base64url
    [TestCase("aGVsbG8/d29ybGQ")] // '/' likewise
    [TestCase("äöü")]
    public void TryDecodeRejectsMalformedInputWithoutThrowing(string? cursor)
    {
        // Base64Url.TryDecodeFromChars is only "try" about the destination buffer: it throws
        // FormatException on a malformed input. TryDecode gates on Base64Url.IsValid first, and
        // this test is what keeps that gate in place.
        Assert.That(BitbucketCursor.TryDecode(cursor, out var decoded), Is.False);
        Assert.That(decoded, Is.Null);
    }

    [Test]
    public void TryDecodeRejectsAValidEncodingOfSomethingThatIsNotAUrl()
    {
        var cursor = Base64Url.EncodeToString("hello"u8.ToArray());

        Assert.That(BitbucketCursor.TryDecode(cursor, out _), Is.False);
    }

    [Test]
    public void TryDecodeRejectsInvalidUtf8()
    {
        var cursor = Base64Url.EncodeToString([0xFF, 0xFE, 0xFD, 0xFC]);

        Assert.That(BitbucketCursor.TryDecode(cursor, out _), Is.False);
    }

    [Test]
    public void TryDecodeRejectsAnOversizedCursorBeforeAllocating()
    {
        var cursor = new string('A', 8192);

        Assert.That(BitbucketCursor.TryDecode(cursor, out _), Is.False);
    }

    [TestCase("https://api.bitbucket.org/2.0/", true)]
    [TestCase("https://API.BITBUCKET.ORG/2.0/pullrequests", true)]
    [TestCase("https://api.bitbucket.org/2.0", false)]
    [TestCase("https://api.bitbucket.org/", false)]
    [TestCase(null, false)]
    public void IsBitbucketApiUrlMatchesTheDocumentedRules(string? url, bool expected) =>
        Assert.That(BitbucketCursor.IsBitbucketApiUrl(url), Is.EqualTo(expected));

    [Test]
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
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var request = Expect.Single(stub.Requests);

        // Byte for byte: a cursor is Bitbucket's own opaque URL and re-composing it would drop the
        // query state it encodes. The filters passed alongside it are ignored for the same reason.
        Assert.That(request.Uri?.AbsoluteUri, Is.EqualTo(NextUrl));
    }

    [Test]
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
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(Expect.Single(stub.Requests).Uri?.AbsoluteUri, Is.EqualTo(DiffStatNext));
    }

    [Test]
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
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(Expect.Single(stub.Requests).Uri?.AbsoluteUri, Is.EqualTo(CommentsNext));
    }

    [TestCaseSource(nameof(UnsafeUrls))]
    public async Task AHostileCursorIsRejectedBeforeAnyRequestIsMade(string url)
    {
        var cursor = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(url));

        using var stub = new StubHttpMessageHandler
        {
            Fallback = _ => StubHttpMessageHandler.CreateResponse(HttpStatusCode.OK, """{"values":[]}"""),
        };

        using var client = CreateClient(stub);

        var exception = await Expect.ThrowsAsync<InvalidCursorException>(() => client.ListPullRequestsAsync(
            "acme",
            "widget-api",
            cursor: cursor,
            cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(exception.Cursor, Is.EqualTo(cursor));

        // The point of the guard: nothing left the process, so no credential was offered to the
        // host the cursor named.
        Assert.That(stub.Requests, Is.Empty);

        // The offending cursor stays out of the user-visible message.
        Assert.That(exception.Message, Does.Not.Contain(cursor));
    }

    [Test]
    public async Task AMalformedCursorIsRejectedBeforeAnyRequestIsMade()
    {
        using var stub = new StubHttpMessageHandler();
        using var client = CreateClient(stub);

        await Assert.ThrowsAsync<InvalidCursorException>(() => client.GetCommentsAsync(
            "acme",
            "widget-api",
            412,
            cursor: "!!!not-base64url!!!",
            cancellationToken: TestContext.CurrentContext.CancellationToken));

        Assert.That(stub.Requests, Is.Empty);
    }

    private static BitbucketApiClient CreateClient(StubHttpMessageHandler stub) =>
        new(new StubCredentialProvider(), NullLoggerFactory.Instance, stub, baseAddress: null, timeProvider: new ManualTimeProvider());
}
