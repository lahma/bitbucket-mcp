using System.Text.Json.Serialization;

namespace Bitbucket.Mcp.Http.Models;

/// <summary>
/// One person's involvement in a pull request. Approvals and change requests live here and
/// nowhere else — there is no top-level "approved" flag on a pull request.
/// </summary>
internal sealed record ParticipantDto
{
    /// <summary><c>PARTICIPANT</c> or <c>REVIEWER</c>.</summary>
    [JsonPropertyName("role")]
    public string? Role { get; init; }

    /// <summary>Whether this person has approved.</summary>
    [JsonPropertyName("approved")]
    public bool? Approved { get; init; }

    /// <summary>
    /// <c>approved</c>, <c>changes_requested</c>, or <see langword="null"/> for no opinion yet.
    /// Note the lower-case wire values, which do not match <see cref="Role"/>'s casing.
    /// </summary>
    [JsonPropertyName("state")]
    public string? State { get; init; }

    /// <summary>The person.</summary>
    [JsonPropertyName("user")]
    public AccountDto? User { get; init; }
}
