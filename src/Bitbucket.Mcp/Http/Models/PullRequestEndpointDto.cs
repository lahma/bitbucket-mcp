using System.Text.Json.Serialization;

namespace Bitbucket.Mcp.Http.Models;

/// <summary>
/// One end of a pull request — the <c>source</c> or <c>destination</c> object: a branch, the
/// commit it pointed at when the pull request was last updated, and the repository it lives in
/// (which differs from the pull request's own repository for a fork).
/// </summary>
internal sealed record PullRequestEndpointDto
{
    /// <summary>The branch at this end.</summary>
    [JsonPropertyName("branch")]
    public BranchRefDto? Branch { get; init; }

    /// <summary>The commit at this end.</summary>
    [JsonPropertyName("commit")]
    public CommitRefDto? Commit { get; init; }

    /// <summary>The repository at this end; a fork for cross-repository pull requests.</summary>
    [JsonPropertyName("repository")]
    public RepositoryRefDto? Repository { get; init; }
}

/// <summary>A branch reference.</summary>
internal sealed record BranchRefDto
{
    /// <summary>The branch name, without any <c>refs/heads/</c> prefix.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

/// <summary>A commit reference.</summary>
internal sealed record CommitRefDto
{
    /// <summary>The commit hash. Usually abbreviated to 12 characters by the API.</summary>
    [JsonPropertyName("hash")]
    public string? Hash { get; init; }
}

/// <summary>A repository reference.</summary>
internal sealed record RepositoryRefDto
{
    /// <summary>The <c>workspace/repo-slug</c> pair — the form that goes back into a URL.</summary>
    [JsonPropertyName("full_name")]
    public string? FullName { get; init; }

    /// <summary>The repository's display name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }
}
