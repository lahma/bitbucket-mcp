using System.Text.Json.Serialization;

namespace Bitbucket.Mcp.Http.Models;

/// <summary>
/// Body of <c>POST /pullrequests</c>. Only <see cref="Title"/> and <see cref="Source"/> are
/// required by the API; omitting <see cref="Destination"/> targets the repository's main branch.
/// </summary>
/// <remarks>
/// Null properties are omitted from the JSON (the wire context serialises with
/// <c>WhenWritingNull</c>), so an unset field means "leave it to Bitbucket" rather than "set it to
/// null" — which matters a great deal for <see cref="UpdatePullRequestRequest"/>.
/// </remarks>
internal sealed record CreatePullRequestRequest
{
    /// <summary>The pull request title. Required.</summary>
    [JsonPropertyName("title")]
    public required string Title { get; init; }

    /// <summary>The branch to merge from. Required.</summary>
    [JsonPropertyName("source")]
    public required PullRequestEndpointRequest Source { get; init; }

    /// <summary>The branch to merge into. Defaults to the repository's main branch.</summary>
    [JsonPropertyName("destination")]
    public PullRequestEndpointRequest? Destination { get; init; }

    /// <summary>The description, in Bitbucket-flavoured Markdown.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>Whether merging should delete the source branch.</summary>
    [JsonPropertyName("close_source_branch")]
    public bool? CloseSourceBranch { get; init; }

    /// <summary>Whether to open the pull request as a draft.</summary>
    [JsonPropertyName("draft")]
    public bool? Draft { get; init; }

    /// <summary>Reviewers to request, identified by UUID only.</summary>
    [JsonPropertyName("reviewers")]
    public IReadOnlyList<AccountRefRequest>? Reviewers { get; init; }
}

/// <summary>
/// Body of <c>PUT /pullrequests/{id}</c>. Every field is optional: the endpoint is a partial
/// update and anything omitted keeps its current value.
/// </summary>
/// <remarks>
/// <see cref="Reviewers"/> is the exception to "omitted means unchanged" in spirit — sending it
/// <em>replaces</em> the reviewer list wholesale, so a caller adding one reviewer has to send the
/// existing ones too.
/// </remarks>
internal sealed record UpdatePullRequestRequest
{
    /// <summary>A new title.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>A new description.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>Retarget the pull request at a different branch.</summary>
    [JsonPropertyName("destination")]
    public PullRequestEndpointRequest? Destination { get; init; }

    /// <summary>Change whether merging deletes the source branch.</summary>
    [JsonPropertyName("close_source_branch")]
    public bool? CloseSourceBranch { get; init; }

    /// <summary>Change draft status.</summary>
    [JsonPropertyName("draft")]
    public bool? Draft { get; init; }

    /// <summary>Replace the reviewer list, identified by UUID only.</summary>
    [JsonPropertyName("reviewers")]
    public IReadOnlyList<AccountRefRequest>? Reviewers { get; init; }
}

/// <summary>One end of a pull request in a request body: only the branch name is ever needed.</summary>
internal sealed record PullRequestEndpointRequest
{
    /// <summary>The branch.</summary>
    [JsonPropertyName("branch")]
    public required BranchRequest Branch { get; init; }
}

/// <summary>A branch, by name.</summary>
internal sealed record BranchRequest
{
    /// <summary>The branch name, without any <c>refs/heads/</c> prefix.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }
}

/// <summary>
/// An account in a request body. Bitbucket matches reviewers by UUID only — a display name or
/// nickname here is silently ignored or rejected.
/// </summary>
internal sealed record AccountRefRequest
{
    /// <summary>The account UUID, in Bitbucket's <c>{…}</c> braced form.</summary>
    [JsonPropertyName("uuid")]
    public required string Uuid { get; init; }
}
