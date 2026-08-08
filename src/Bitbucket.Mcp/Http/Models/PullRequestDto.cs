using System.Text.Json.Serialization;

namespace Bitbucket.Mcp.Http.Models;

/// <summary>
/// The fields of a pull request that a list view asks for. Kept separate from
/// <see cref="PullRequestDto"/> because listing a hundred pull requests with their descriptions,
/// participants and reviewers attached is the single easiest way to burn a model's context.
/// </summary>
/// <remarks>
/// Everything is nullable: a <c>fields=</c> partial response returns exactly what was asked for
/// and nothing else, so a property's absence means "not requested" at least as often as it means
/// "not set".
/// </remarks>
internal record PullRequestSummaryDto
{
    /// <summary>The pull request number, unique within its repository.</summary>
    [JsonPropertyName("id")]
    public int? Id { get; init; }

    /// <summary>The title.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary><c>OPEN</c>, <c>MERGED</c>, <c>DECLINED</c> or <c>SUPERSEDED</c>.</summary>
    [JsonPropertyName("state")]
    public string? State { get; init; }

    /// <summary>Whether the pull request is still a draft.</summary>
    [JsonPropertyName("draft")]
    public bool? Draft { get; init; }

    /// <summary>Who opened it.</summary>
    [JsonPropertyName("author")]
    public AccountDto? Author { get; init; }

    /// <summary>The branch being merged from.</summary>
    [JsonPropertyName("source")]
    public PullRequestEndpointDto? Source { get; init; }

    /// <summary>The branch being merged into.</summary>
    [JsonPropertyName("destination")]
    public PullRequestEndpointDto? Destination { get; init; }

    /// <summary>When it was opened.</summary>
    [JsonPropertyName("created_on")]
    public DateTimeOffset? CreatedOn { get; init; }

    /// <summary>When it last changed.</summary>
    [JsonPropertyName("updated_on")]
    public DateTimeOffset? UpdatedOn { get; init; }

    /// <summary>Number of comments, including inline ones.</summary>
    [JsonPropertyName("comment_count")]
    public int? CommentCount { get; init; }

    /// <summary>Number of open tasks.</summary>
    [JsonPropertyName("task_count")]
    public int? TaskCount { get; init; }

    /// <summary>Whether merging will delete the source branch.</summary>
    [JsonPropertyName("close_source_branch")]
    public bool? CloseSourceBranch { get; init; }
}

/// <summary>
/// A pull request as returned by the single-pull-request endpoints (get, create, update, approve,
/// decline, merge). Adds the fields that are only worth fetching one at a time.
/// </summary>
/// <remarks>
/// Derives from <see cref="PullRequestSummaryDto"/> so that a mapper written against the summary
/// shape also accepts a detail response; System.Text.Json binds the inherited properties normally
/// because there is no polymorphic serialisation configured here.
/// </remarks>
internal sealed record PullRequestDto : PullRequestSummaryDto
{
    /// <summary>The description, in Bitbucket-flavoured Markdown.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>The decline or supersede reason, when the pull request has one.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    /// <summary>The merge commit, once merged.</summary>
    [JsonPropertyName("merge_commit")]
    public CommitRefDto? MergeCommit { get; init; }

    /// <summary>Who merged or declined it.</summary>
    [JsonPropertyName("closed_by")]
    public AccountDto? ClosedBy { get; init; }

    /// <summary>The explicitly requested reviewers.</summary>
    [JsonPropertyName("reviewers")]
    public IReadOnlyList<AccountDto>? Reviewers { get; init; }

    /// <summary>
    /// Everyone involved and their review state — the only place approvals and change requests
    /// are visible.
    /// </summary>
    [JsonPropertyName("participants")]
    public IReadOnlyList<ParticipantDto>? Participants { get; init; }
}
