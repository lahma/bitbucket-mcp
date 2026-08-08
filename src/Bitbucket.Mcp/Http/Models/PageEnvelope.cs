using System.Text.Json.Serialization;

namespace Bitbucket.Mcp.Http.Models;

/// <summary>
/// Bitbucket's paginated collection wrapper.
/// </summary>
/// <remarks>
/// Only <c>values</c> and <c>next</c> are guaranteed by the API; everything else is best effort,
/// and a <c>fields=</c> partial response can strip any of it — including <c>next</c> itself, which
/// is why every paginated field set has to ask for it explicitly (see <c>FieldSets</c>).
/// <c>next</c> is an opaque absolute URL, not a page number: never synthesise one.
/// </remarks>
/// <typeparam name="T">The element type of <c>values</c>.</typeparam>
internal sealed record PageEnvelope<T>
{
    /// <summary>The page's items. Absent on an empty response from some endpoints.</summary>
    [JsonPropertyName("values")]
    public IReadOnlyList<T>? Values { get; init; }

    /// <summary>Absolute URL of the next page, or <see langword="null"/> on the last page.</summary>
    [JsonPropertyName("next")]
    public string? Next { get; init; }

    /// <summary>Total number of items across all pages, when the endpoint computes it.</summary>
    [JsonPropertyName("size")]
    public int? Size { get; init; }

    /// <summary>1-based page number, when the endpoint reports it.</summary>
    [JsonPropertyName("page")]
    public int? Page { get; init; }

    /// <summary>Items per page, when the endpoint reports it.</summary>
    [JsonPropertyName("pagelen")]
    public int? Pagelen { get; init; }
}
