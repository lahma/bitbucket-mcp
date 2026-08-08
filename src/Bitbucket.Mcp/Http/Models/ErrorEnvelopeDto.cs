using System.Text.Json.Serialization;

namespace Bitbucket.Mcp.Http.Models;

/// <summary>
/// Bitbucket's error body: <c>{"type":"error","error":{"message":"…","detail":"…","fields":{…}}}</c>.
/// </summary>
/// <remarks>
/// Parsing this is always best effort. Not every non-2xx response is JSON at all (a 302 to a CDN,
/// an HTML maintenance page, a bare <c>555</c>), and some endpoints put a shape in <c>detail</c> or
/// <c>fields</c> that does not match the documented one. Callers therefore catch
/// <see cref="System.Text.Json.JsonException"/> and fall back to the raw body that
/// <c>BitbucketApiException</c> carries anyway.
/// </remarks>
internal sealed record ErrorEnvelopeDto
{
    /// <summary>Always <c>error</c> in practice.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    /// <summary>The error itself.</summary>
    [JsonPropertyName("error")]
    public ErrorDetailDto? Error { get; init; }
}

/// <summary>The body of a Bitbucket error.</summary>
internal sealed record ErrorDetailDto
{
    /// <summary>A one-line summary, suitable for showing to the user verbatim.</summary>
    [JsonPropertyName("message")]
    public string? Message { get; init; }

    /// <summary>Longer explanation, when the endpoint provides one.</summary>
    [JsonPropertyName("detail")]
    public string? Detail { get; init; }

    /// <summary>
    /// Per-field validation errors on a 400 — for example
    /// <c>{"title":["This field is required."]}</c>. The most useful part of a rejected create or
    /// update, so it is surfaced rather than swallowed.
    /// </summary>
    [JsonPropertyName("fields")]
    public IReadOnlyDictionary<string, IReadOnlyList<string>>? Fields { get; init; }
}
