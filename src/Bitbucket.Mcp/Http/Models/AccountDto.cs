using System.Text.Json.Serialization;

namespace Bitbucket.Mcp.Http.Models;

/// <summary>
/// A Bitbucket account, trimmed to what a reviewer actually needs to identify a person.
/// </summary>
/// <remarks>
/// The <see cref="Uuid"/> is the load-bearing field: reviewers are added by UUID (including the
/// surrounding braces, exactly as returned), never by display name or nickname, both of which are
/// mutable and ambiguous.
/// </remarks>
internal sealed record AccountDto
{
    /// <summary>The account's display name.</summary>
    [JsonPropertyName("display_name")]
    public string? DisplayName { get; init; }

    /// <summary>The account UUID, in Bitbucket's <c>{…}</c> braced form.</summary>
    [JsonPropertyName("uuid")]
    public string? Uuid { get; init; }

    /// <summary>The account's nickname, when it has one.</summary>
    [JsonPropertyName("nickname")]
    public string? Nickname { get; init; }
}
