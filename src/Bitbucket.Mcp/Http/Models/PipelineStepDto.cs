using System.Text.Json.Serialization;

namespace Bitbucket.Mcp.Http.Models;

/// <summary>
/// One step of a pipeline run — the granularity at which a build actually fails.
/// </summary>
/// <remarks>
/// <c>setup_commands</c> and <c>script_commands</c> are deliberately never requested. The setup
/// list alone is around forty entries of git plumbing on every step of every run, and the script
/// list is the repository's own <c>bitbucket-pipelines.yml</c>, which local git already has. Both
/// would cost more context than the whole rest of the response.
/// </remarks>
internal sealed record PipelineStepDto
{
    /// <summary>The step's UUID, in braced form — what the log endpoint is addressed by.</summary>
    [JsonPropertyName("uuid")]
    public string? Uuid { get; init; }

    /// <summary>
    /// The step's name from <c>bitbucket-pipelines.yml</c>, for example <c>Test and Build</c>.
    /// Undocumented in Atlassian's OpenAPI schema, but without it a list of steps is unreadable.
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>The nested progress state, the same shape a pipeline carries.</summary>
    [JsonPropertyName("state")]
    public PipelineStepStateDto? State { get; init; }

    /// <summary>When the step started; absent if it never did.</summary>
    [JsonPropertyName("started_on")]
    public DateTimeOffset? StartedOn { get; init; }

    /// <summary>When the step finished; absent while it is still running.</summary>
    [JsonPropertyName("completed_on")]
    public DateTimeOffset? CompletedOn { get; init; }

    /// <summary>Wall-clock duration in seconds.</summary>
    [JsonPropertyName("duration_in_seconds")]
    public int? DurationInSeconds { get; init; }

    /// <summary>The container the step ran in.</summary>
    [JsonPropertyName("image")]
    public PipelineImageDto? Image { get; init; }
}

/// <summary>
/// A step's progress state. Identical in shape to <see cref="PipelineStateDto"/>, but it also
/// carries the error that explains a failure.
/// </summary>
/// <remarks>
/// <see cref="PipelineStepResultDto.Error"/> is the highest-value field in the whole pipeline
/// surface: "the step timed out" or "the image could not be pulled" answers the question without
/// fetching a log at all.
/// </remarks>
internal sealed record PipelineStepStateDto
{
    /// <summary><c>PENDING</c>, <c>READY</c>, <c>IN_PROGRESS</c> or <c>COMPLETED</c>.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>The outcome, once <see cref="Name"/> is <c>COMPLETED</c>.</summary>
    [JsonPropertyName("result")]
    public PipelineStepResultDto? Result { get; init; }

    /// <summary>The phase, while <see cref="Name"/> is <c>IN_PROGRESS</c>.</summary>
    [JsonPropertyName("stage")]
    public PipelineStateNameDto? Stage { get; init; }
}

/// <summary>A completed step's outcome, and the error behind a failure.</summary>
internal sealed record PipelineStepResultDto
{
    /// <summary>
    /// <c>SUCCESSFUL</c>, <c>FAILED</c>, <c>ERROR</c>, <c>STOPPED</c>, <c>EXPIRED</c> or
    /// <c>NOT_RUN</c>.
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>Why the step failed, when Bitbucket knows.</summary>
    [JsonPropertyName("error")]
    public PipelineStepErrorDto? Error { get; init; }
}

/// <summary>An error that caused a step to fail.</summary>
internal sealed record PipelineStepErrorDto
{
    /// <summary>A stable identifier for the kind of failure.</summary>
    [JsonPropertyName("key")]
    public string? Key { get; init; }

    /// <summary>The human-readable message.</summary>
    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

/// <summary>The Docker image a step ran in.</summary>
internal sealed record PipelineImageDto
{
    /// <summary>The image name, for example <c>python:3.11</c>.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }
}
