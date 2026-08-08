using System.Globalization;
using System.Net;
using System.Text;

using Bitbucket.Mcp.Http.Models;

namespace Bitbucket.Mcp.Http;

/// <summary>
/// A non-2xx response from Bitbucket, carrying everything the tool-layer error funnel needs to
/// turn a status code into advice: the code itself, Bitbucket's parsed error envelope when the
/// body was JSON, the raw body when it was not, and how many times the request was already
/// retried.
/// </summary>
/// <remarks>
/// The raw body is kept because the interesting failures are exactly the ones that do not parse —
/// <c>555</c> on an oversized diff, an HTML error page from a proxy, a redirect target that
/// answered with something unexpected.
/// </remarks>
internal sealed class BitbucketApiException : Exception
{
    /// <summary>
    /// Hard cap on the retained body, in characters. Bounded because this string ends up in log
    /// lines and in an <c>McpException</c> message that goes back into the model's context.
    /// </summary>
    internal const int MaxRawBodyLength = 16 * 1024;

    /// <summary>Appended in place of the tail of a body that hit <see cref="MaxRawBodyLength"/>.</summary>
    private const string TruncationMarker = "… [truncated]";

    /// <summary>Longest body excerpt quoted in <see cref="Exception.Message"/>.</summary>
    private const int MessageSnippetLength = 200;

    /// <param name="statusCode">The HTTP status. May be a code outside the enum, notably Bitbucket's <c>555</c>.</param>
    /// <param name="error">Bitbucket's error envelope, when the body parsed as one.</param>
    /// <param name="rawBody">The response body as read; truncated to <see cref="MaxRawBodyLength"/>.</param>
    /// <param name="retryAttempts">How many retries the pipeline had already spent on this request.</param>
    /// <param name="retryAfterSeconds">
    /// The <c>Retry-After</c> the failing response carried, in whole seconds, or
    /// <see langword="null"/> when it carried none.
    /// </param>
    /// <param name="innerException">The underlying failure, if any.</param>
    internal BitbucketApiException(
        HttpStatusCode statusCode,
        ErrorEnvelopeDto? error,
        string? rawBody,
        int retryAttempts,
        int? retryAfterSeconds = null,
        Exception? innerException = null)
        : base(BuildMessage(statusCode, error, rawBody, retryAttempts), innerException)
    {
        StatusCode = statusCode;
        Error = error;
        RawBody = Truncate(rawBody);
        RetryAttempts = retryAttempts;
        RetryAfterSeconds = retryAfterSeconds;
    }

    /// <summary>The HTTP status code the request failed with.</summary>
    internal HttpStatusCode StatusCode { get; }

    /// <summary>Bitbucket's parsed error envelope, or <see langword="null"/> if the body was not one.</summary>
    internal ErrorEnvelopeDto? Error { get; }

    /// <summary>The response body, truncated to <see cref="MaxRawBodyLength"/>. Empty if there was none.</summary>
    internal string RawBody { get; }

    /// <summary>Retries already spent, so a 5xx message can say the request was not simply unlucky once.</summary>
    internal int RetryAttempts { get; }

    /// <summary>
    /// What <c>Retry-After</c> asked for on the response that finally failed, in whole seconds, or
    /// <see langword="null"/> when there was no usable header. This is how a 429 can name the wait
    /// instead of guessing at one.
    /// </summary>
    internal int? RetryAfterSeconds { get; }

    private static string Truncate(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return string.Empty;
        }

        return body.Length <= MaxRawBodyLength
            ? body
            : string.Concat(body.AsSpan(0, MaxRawBodyLength - TruncationMarker.Length), TruncationMarker);
    }

    private static string BuildMessage(HttpStatusCode statusCode, ErrorEnvelopeDto? error, string? rawBody, int retryAttempts)
    {
        var numeric = ((int) statusCode).ToString(CultureInfo.InvariantCulture);
        var name = statusCode.ToString();

        var message = new StringBuilder("Bitbucket API returned HTTP ").Append(numeric);

        // ToString() on an undefined value just repeats the number (Bitbucket's 555 has no name).
        if (!string.Equals(name, numeric, StringComparison.Ordinal))
        {
            message.Append(" (").Append(name).Append(')');
        }

        message.Append('.');

        var detail = FirstNonEmpty(error?.Error?.Message, error?.Error?.Detail) ?? Snippet(rawBody);

        if (detail is not null)
        {
            message.Append(' ').Append(detail);
        }

        if (retryAttempts > 0)
        {
            message
                .Append(" (after ")
                .Append(retryAttempts.ToString(CultureInfo.InvariantCulture))
                .Append(retryAttempts == 1 ? " retry)" : " retries)");
        }

        return message.ToString();
    }

    private static string? FirstNonEmpty(string? first, string? second)
    {
        if (!string.IsNullOrWhiteSpace(first))
        {
            return first.Trim();
        }

        return string.IsNullOrWhiteSpace(second) ? null : second.Trim();
    }

    /// <summary>Collapses a body to a single short line, so an HTML page does not become the message.</summary>
    private static string? Snippet(string? rawBody)
    {
        if (string.IsNullOrWhiteSpace(rawBody))
        {
            return null;
        }

        var collapsed = new StringBuilder(Math.Min(rawBody.Length, MessageSnippetLength));
        var lastWasSpace = false;

        foreach (var c in rawBody)
        {
            var isSpace = char.IsWhiteSpace(c);

            if (isSpace && lastWasSpace)
            {
                continue;
            }

            collapsed.Append(isSpace ? ' ' : c);
            lastWasSpace = isSpace;

            if (collapsed.Length >= MessageSnippetLength)
            {
                collapsed.Append(TruncationMarker);
                break;
            }
        }

        var text = collapsed.ToString().Trim();
        return text.Length == 0 ? null : text;
    }
}
