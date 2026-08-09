using System.Text.Json.Serialization;

namespace Bitbucket.Mcp.Http.Models;

/// <summary>
/// One entry of <c>GET /effective-default-reviewers</c>: a person, plus where the rule that names
/// them lives.
/// </summary>
/// <remarks>
/// The <em>effective</em> endpoint is the one this server calls, and it does not answer with a flat
/// list of accounts the way plain <c>default-reviewers</c> does — each account arrives wrapped
/// alongside <see cref="ReviewerType"/>. That wrapper is the whole reason to prefer it: it is what
/// distinguishes a reviewer configured on the repository from one inherited from its project.
/// (The wire <c>type</c> discriminator is <c>default_reviewer</c>; it is not requested, because
/// there is only one kind of entry.)
/// </remarks>
internal sealed record DefaultReviewerDto
{
    /// <summary>Where the reviewer comes from: <c>repository</c> or <c>project</c>.</summary>
    [JsonPropertyName("reviewer_type")]
    public string? ReviewerType { get; init; }

    /// <summary>The person.</summary>
    [JsonPropertyName("user")]
    public AccountDto? User { get; init; }
}
