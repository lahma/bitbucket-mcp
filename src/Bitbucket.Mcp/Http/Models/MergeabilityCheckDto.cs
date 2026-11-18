using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bitbucket.Mcp.Http.Models;

/// <summary>
/// The body of <c>…/pullrequests/{id}/mergeability/checks</c>: every check Bitbucket's merge would
/// run, evaluated without merging. Not paginated — the list is bounded by the repository's own
/// configuration.
/// </summary>
internal sealed record MergeabilityChecksDto
{
    /// <summary>The checks, in Bitbucket's order.</summary>
    [JsonPropertyName("values")]
    public IReadOnlyList<MergeabilityCheckDto>? Values { get; init; }
}

/// <summary>
/// One mergeability check. <see cref="Type"/> says which, and which of the other fields it carries.
/// </summary>
/// <remarks>
/// <para>
/// The types are <c>pullrequest_state_check</c> (<see cref="State"/>),
/// <c>current_user_permission_check</c>, <c>git_mergeability_check</c> (<see cref="Reason"/>), one
/// <c>standard_merge_check</c> per configured branch restriction (<see cref="Check"/>'s kind, with
/// <see cref="Requirement"/> and <see cref="Observed"/>), <c>custom_pre_merge_check</c> and
/// <c>custom_on_merge_check</c> (<see cref="Check"/>'s name and a <see cref="Message"/>), and
/// <c>merge_queue_check</c> (<see cref="Queued"/>). A check that is not configured is omitted, not
/// reported as passing.
/// </para>
/// <para>
/// <see cref="Requirement"/> and <see cref="Observed"/> change shape with the check's kind
/// (<c>{"minimum_approvals": 2}</c> against <c>{"approval_count": 1}</c>), so they are kept as raw
/// JSON rather than modelled one kind at a time.
/// </para>
/// </remarks>
internal sealed record MergeabilityCheckDto
{
    /// <summary>Which check this is.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    /// <summary>
    /// <c>PASSED</c>, <c>FAILED</c>, <c>PENDING</c>, <c>SKIPPED</c> or <c>UNKNOWN</c> — the last
    /// meaning the check could not be evaluated, which does not establish that the pull request is
    /// mergeable.
    /// </summary>
    [JsonPropertyName("status")]
    public string? Status { get; init; }

    /// <summary>Whether this check is preventing the merge right now. A failed check need not be.</summary>
    [JsonPropertyName("blocking")]
    public bool? Blocking { get; init; }

    /// <summary>Which standard or custom check this is.</summary>
    [JsonPropertyName("check")]
    public MergeCheckDefinitionDto? Check { get; init; }

    /// <summary>A standard check's configured threshold, keyed per kind.</summary>
    [JsonPropertyName("requirement")]
    public JsonElement? Requirement { get; init; }

    /// <summary>A standard check's current value, keyed per kind.</summary>
    [JsonPropertyName("observed")]
    public JsonElement? Observed { get; init; }

    /// <summary>A git check's outcome: <c>clean</c>, <c>conflicts</c> or <c>merge_impossible</c>.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    /// <summary>A state check's pull request state. A queued pull request reports <c>OPEN</c> and blocking.</summary>
    [JsonPropertyName("state")]
    public string? State { get; init; }

    /// <summary>A merge-queue check's answer to "is it already queued?".</summary>
    [JsonPropertyName("queued")]
    public bool? Queued { get; init; }

    /// <summary>A custom check's own message, when it gave one.</summary>
    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

/// <summary>What a standard or custom mergeability check is.</summary>
internal sealed record MergeCheckDefinitionDto
{
    /// <summary>A standard check's kind, for example <c>minimum_approvals</c> or <c>resolved_tasks</c>.</summary>
    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    /// <summary>A custom check's display name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }
}
