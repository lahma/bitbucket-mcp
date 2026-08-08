using System.Text.Json.Serialization;

namespace Bitbucket.Mcp.Http.Models;

/// <summary>
/// Body of <c>POST /pullrequests/{id}/decline</c>.
/// </summary>
/// <remarks>
/// Bitbucket documents no request body for this endpoint; the pull request's <c>reason</c> field
/// is what surfaces a decline reason in the UI, so it is sent best effort and the response is read
/// back rather than assumed. Callers must not depend on the reason round-tripping — an empty
/// object is a valid decline.
/// </remarks>
internal sealed record DeclineRequest
{
    /// <summary>Why the pull request is being declined, if the caller supplied a reason.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}
