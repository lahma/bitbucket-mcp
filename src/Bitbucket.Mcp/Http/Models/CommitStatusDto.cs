using System.Text.Json.Serialization;

namespace Bitbucket.Mcp.Http.Models;

/// <summary>
/// One build status attached to a pull request's commits — what CI reported, and where to read it.
/// </summary>
/// <remarks>
/// Bitbucket also returns <c>links</c>, and in practice the <c>commit</c> and <c>repository</c>
/// objects that are not in the documented schema. None of them are requested: the caller needs to
/// know whether the checks passed and where the failing one is, and <see cref="Url"/> already
/// answers the second half.
/// </remarks>
internal sealed record CommitStatusDto
{
    /// <summary><c>SUCCESSFUL</c>, <c>FAILED</c>, <c>INPROGRESS</c> or <c>STOPPED</c>.</summary>
    [JsonPropertyName("state")]
    public string? State { get; init; }

    /// <summary>The status's own identifier, unique per provider — for example <c>BB-DEPLOY</c>.</summary>
    [JsonPropertyName("key")]
    public string? Key { get; init; }

    /// <summary>The build's name, for example <c>BB-DEPLOY-1</c>.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>Where a human reads the build output.</summary>
    [JsonPropertyName("url")]
    public string? Url { get; init; }

    /// <summary>What the provider had to say about this run.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>
    /// The ref the commit was on when the status was created. Optional — a provider that does not
    /// build per branch leaves it out.
    /// </summary>
    [JsonPropertyName("refname")]
    public string? Refname { get; init; }

    /// <summary>When the status last changed.</summary>
    [JsonPropertyName("updated_on")]
    public DateTimeOffset? UpdatedOn { get; init; }
}
