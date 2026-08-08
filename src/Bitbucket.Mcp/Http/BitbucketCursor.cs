using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Bitbucket.Mcp.Http;

/// <summary>
/// Turns Bitbucket's opaque <c>next</c> page URL into a cursor string and back.
/// </summary>
/// <remarks>
/// <para>
/// Bitbucket's pagination token <em>is</em> a URL, and a URL in a tool result invites the model to
/// "helpfully" edit it. Base64url encoding makes it plainly opaque, keeps it free of characters
/// that need escaping in JSON or a shell, and gives us one place to validate it on the way back
/// in.
/// </para>
/// <para>
/// <b>The validation is a server-side request forgery guard, not a formality.</b> A cursor arrives
/// as a tool argument from the model, and the model's context is full of attacker-influenced text
/// — pull request descriptions, comments, diff hunks. A crafted cursor decoding to
/// <c>http://169.254.169.254/latest/meta-data/</c> or <c>https://evil.example/2.0/</c> would
/// otherwise be fetched by the client with a live <c>Authorization</c> header attached. So a
/// decoded cursor must be <c>https</c>, on exactly <c>api.bitbucket.org</c>, on the default port,
/// with no embedded credentials, under <c>/2.0/</c>. Anything else is not a cursor.
/// </para>
/// </remarks>
internal static class BitbucketCursor
{
    /// <summary>The only host a cursor may point at.</summary>
    private const string ApiHost = "api.bitbucket.org";

    /// <summary>The only path prefix a cursor may use.</summary>
    private const string ApiPathPrefix = "/2.0/";

    /// <summary>
    /// Upper bound on an accepted cursor, in characters. Real cursors are a few hundred; this only
    /// exists so that a hostile multi-megabyte "cursor" cannot make us allocate a decode buffer for
    /// it.
    /// </summary>
    private const int MaxCursorLength = 4096;

    /// <summary>
    /// Encodes a <c>next</c> URL as a cursor, or returns <see langword="null"/> when there is no
    /// next page or the URL does not pass the same validation a decoded cursor has to pass.
    /// </summary>
    /// <remarks>
    /// Refusing to encode an unexpected URL means pagination stops rather than handing out a
    /// cursor that would be rejected on its way back in — the failure surfaces as "no more pages",
    /// which is the safe direction.
    /// </remarks>
    internal static string? Encode(string? nextUrl)
    {
        if (string.IsNullOrWhiteSpace(nextUrl) || !IsBitbucketApiUrl(nextUrl))
        {
            return null;
        }

        return Base64Url.EncodeToString(Encoding.UTF8.GetBytes(nextUrl));
    }

    /// <summary>
    /// Decodes a cursor produced by <see cref="Encode"/> and validates the URL inside it.
    /// </summary>
    /// <param name="cursor">The cursor, as it came back from the model.</param>
    /// <param name="nextUrl">The validated absolute URL to request next.</param>
    /// <returns><see langword="true"/> only for a well-formed cursor pointing into the Bitbucket API.</returns>
    internal static bool TryDecode(string? cursor, [NotNullWhen(true)] out string? nextUrl)
    {
        nextUrl = null;

        if (string.IsNullOrWhiteSpace(cursor) || cursor.Length > MaxCursorLength)
        {
            return false;
        }

        // TryDecodeFromChars is only "try" about the destination size: it throws FormatException on
        // a malformed input, and a cursor that came from the model is malformed sooner or later.
        if (!Base64Url.IsValid(cursor))
        {
            return false;
        }

        var buffer = new byte[Base64Url.GetMaxDecodedLength(cursor.Length)];

        if (!Base64Url.TryDecodeFromChars(cursor, buffer, out var decodedLength))
        {
            return false;
        }

        // Invalid UTF-8 decodes to replacement characters rather than throwing; the URL validation
        // below rejects whatever that produces.
        var candidate = Encoding.UTF8.GetString(buffer, 0, decodedLength);

        if (!IsBitbucketApiUrl(candidate))
        {
            return false;
        }

        nextUrl = candidate;
        return true;
    }

    /// <summary>
    /// Whether a URL is one we are willing to issue an authenticated request to. See the type
    /// remarks for why each clause is here.
    /// </summary>
    internal static bool IsBitbucketApiUrl([NotNullWhen(true)] string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        // Uri lower-cases the scheme and the host, but an explicit comparison documents the intent
        // and does not depend on that.
        return string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && string.Equals(uri.Host, ApiHost, StringComparison.OrdinalIgnoreCase)
            && uri.IsDefaultPort
            && uri.UserInfo.Length == 0
            && uri.AbsolutePath.StartsWith(ApiPathPrefix, StringComparison.Ordinal);
    }
}
