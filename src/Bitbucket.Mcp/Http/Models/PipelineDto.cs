using System.Text.Json.Serialization;

namespace Bitbucket.Mcp.Http.Models;

/// <summary>
/// One pipeline run — a build, in Bitbucket's own vocabulary.
/// </summary>
/// <remarks>
/// Bitbucket also returns <c>repository</c>, <c>links</c>, <c>labels</c>, <c>has_variables</c>,
/// <c>first_successful</c>, <c>expired</c>, <c>run_number</c>, <c>run_creation_date</c> and
/// <c>pipeline_request_uuid</c>. None are requested: they are either noise or already known to the
/// caller, who named the repository to get here. <c>links</c> is deliberately skipped too — it
/// carries only <c>self</c> and <c>steps</c>, both API URLs, and no <c>html</c> link, so the web
/// URL is composed from the build number rather than read (verified against the live API).
/// </remarks>
internal sealed record PipelineDto
{
    /// <summary>The pipeline's UUID, in braced form.</summary>
    [JsonPropertyName("uuid")]
    public string? Uuid { get; init; }

    /// <summary>The build number, as it appears in the pipeline's web URL.</summary>
    [JsonPropertyName("build_number")]
    public int? BuildNumber { get; init; }

    /// <summary>The nested progress state. See <see cref="PipelineStateDto"/>.</summary>
    [JsonPropertyName("state")]
    public PipelineStateDto? State { get; init; }

    /// <summary>What the pipeline ran against — a branch or tag, and the commit.</summary>
    [JsonPropertyName("target")]
    public PipelineTargetDto? Target { get; init; }

    /// <summary>What started it: <c>PUSH</c>, <c>MANUAL</c>, <c>SCHEDULED</c> or <c>PARENT_STEP</c>.</summary>
    [JsonPropertyName("trigger")]
    public PipelineTriggerDto? Trigger { get; init; }

    /// <summary>Who started it.</summary>
    [JsonPropertyName("creator")]
    public AccountDto? Creator { get; init; }

    /// <summary>When it was queued.</summary>
    [JsonPropertyName("created_on")]
    public DateTimeOffset? CreatedOn { get; init; }

    /// <summary>When it finished; absent while it is still running.</summary>
    [JsonPropertyName("completed_on")]
    public DateTimeOffset? CompletedOn { get; init; }

    /// <summary>
    /// Wall-clock duration in seconds. Undocumented in Atlassian's OpenAPI schema but present on
    /// every live response, and the one number that makes a list of runs comparable at a glance.
    /// </summary>
    [JsonPropertyName("duration_in_seconds")]
    public int? DurationInSeconds { get; init; }
}

/// <summary>
/// The progress state, which Bitbucket nests one or two levels deep.
/// </summary>
/// <remarks>
/// The shapes are <c>{name: PENDING}</c>, <c>{name: IN_PROGRESS, stage: {name: RUNNING}}</c> and
/// <c>{name: COMPLETED, result: {name: FAILED}}</c>. Only the innermost name carries the answer, so
/// <c>ResultMapper</c> flattens all three into one word.
/// </remarks>
internal sealed record PipelineStateDto
{
    /// <summary><c>PENDING</c>, <c>IN_PROGRESS</c> or <c>COMPLETED</c>.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>The outcome, once <see cref="Name"/> is <c>COMPLETED</c>.</summary>
    [JsonPropertyName("result")]
    public PipelineStateNameDto? Result { get; init; }

    /// <summary>The phase, while <see cref="Name"/> is <c>IN_PROGRESS</c>.</summary>
    [JsonPropertyName("stage")]
    public PipelineStateNameDto? Stage { get; init; }
}

/// <summary>The inner half of a nested state: a name, and nothing else worth requesting.</summary>
internal sealed record PipelineStateNameDto
{
    /// <summary>
    /// <c>SUCCESSFUL</c>, <c>FAILED</c>, <c>ERROR</c>, <c>STOPPED</c>, <c>EXPIRED</c> or
    /// <c>NOT_RUN</c> for a result; <c>RUNNING</c>, <c>PAUSED</c> or <c>HALTED</c> for a stage.
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

/// <summary>What a pipeline ran against.</summary>
internal sealed record PipelineTargetDto
{
    /// <summary><c>branch</c>, <c>tag</c>, <c>named_branch</c> or <c>bookmark</c>, in lower case.</summary>
    [JsonPropertyName("ref_type")]
    public string? RefType { get; init; }

    /// <summary>The branch or tag name.</summary>
    [JsonPropertyName("ref_name")]
    public string? RefName { get; init; }

    /// <summary>The revision that was built.</summary>
    [JsonPropertyName("commit")]
    public CommitRefDto? Commit { get; init; }

    /// <summary>Which definition in <c>bitbucket-pipelines.yml</c> matched.</summary>
    [JsonPropertyName("selector")]
    public PipelineSelectorDto? Selector { get; init; }
}

/// <summary>The <c>bitbucket-pipelines.yml</c> entry that produced the run.</summary>
internal sealed record PipelineSelectorDto
{
    /// <summary><c>branches</c>, <c>tags</c>, <c>bookmarks</c>, <c>default</c>, <c>custom</c>, ….</summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    /// <summary>The matching pattern, for example <c>master</c> or <c>feature/*</c>.</summary>
    [JsonPropertyName("pattern")]
    public string? Pattern { get; init; }
}

/// <summary>What started a pipeline.</summary>
internal sealed record PipelineTriggerDto
{
    /// <summary><c>PUSH</c>, <c>MANUAL</c>, <c>SCHEDULED</c> or <c>PARENT_STEP</c>.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }
}
