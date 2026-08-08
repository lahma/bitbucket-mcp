using System.Text.Json.Serialization;

namespace Bitbucket.Mcp.Http.Models;

/// <summary>
/// Body of <c>POST /pullrequests/{id}/merge</c>. Every field is optional; an empty body merges
/// with the repository's configured defaults.
/// </summary>
internal sealed record MergeRequest
{
    /// <summary>
    /// Bitbucket's object type discriminator. The endpoint accepts the body without it, but some
    /// deployments echo validation errors that only make sense when it is present.
    /// </summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    /// <summary>The merge commit message. Bitbucket composes a default when omitted.</summary>
    [JsonPropertyName("message")]
    public string? Message { get; init; }

    /// <summary>Whether to delete the source branch after merging.</summary>
    [JsonPropertyName("close_source_branch")]
    public bool? CloseSourceBranch { get; init; }

    /// <summary>
    /// <c>merge_commit</c>, <c>squash</c> or <c>fast_forward</c>. A strategy the repository has
    /// disabled is rejected with a 400, not silently substituted.
    /// </summary>
    [JsonPropertyName("merge_strategy")]
    public string? MergeStrategy { get; init; }
}
