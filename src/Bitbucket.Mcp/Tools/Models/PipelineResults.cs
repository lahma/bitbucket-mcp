namespace Bitbucket.Mcp.Tools.Models;

/// <summary>One pipeline run, with its nested Bitbucket state flattened into a single word.</summary>
internal sealed record PipelineSummary
{
    /// <summary>The pipeline's UUID, in braced form. Accepted as <c>pipeline</c> by the other tools.</summary>
    public string? Uuid { get; init; }

    /// <summary>
    /// The build number, as it appears in the run's web URL. Also accepted as <c>pipeline</c>, and
    /// the friendlier of the two to quote to a human.
    /// </summary>
    public int? BuildNumber { get; init; }

    /// <summary>
    /// The outcome when the run has finished — <c>SUCCESSFUL</c>, <c>FAILED</c>, <c>ERROR</c>,
    /// <c>STOPPED</c> or <c>EXPIRED</c> — otherwise the phase it is in: <c>PENDING</c>,
    /// <c>RUNNING</c>, <c>PAUSED</c> or <c>HALTED</c>.
    /// </summary>
    public string? State { get; init; }

    /// <summary>
    /// Whether the run has finished. <see langword="false"/> means <see cref="State"/> is a phase
    /// rather than an outcome, so a red build cannot be inferred from it yet.
    /// </summary>
    public bool? Completed { get; init; }

    /// <summary>The branch or tag the run was for.</summary>
    public string? RefName { get; init; }

    /// <summary><c>branch</c>, <c>tag</c>, <c>named_branch</c> or <c>bookmark</c>.</summary>
    public string? RefType { get; init; }

    /// <summary>The revision that was built.</summary>
    public string? CommitHash { get; init; }

    /// <summary>Which <c>bitbucket-pipelines.yml</c> definition matched — <c>branches</c>, <c>custom</c>, ….</summary>
    public string? SelectorType { get; init; }

    /// <summary>The matching pattern from that definition.</summary>
    public string? SelectorPattern { get; init; }

    /// <summary>What started the run: <c>PUSH</c>, <c>MANUAL</c>, <c>SCHEDULED</c> or <c>PARENT_STEP</c>.</summary>
    public string? Trigger { get; init; }

    /// <summary>Who started it.</summary>
    public UserSummary? Creator { get; init; }

    /// <summary>When it was queued.</summary>
    public DateTimeOffset? CreatedOn { get; init; }

    /// <summary>When it finished; absent while it is still running.</summary>
    public DateTimeOffset? CompletedOn { get; init; }

    /// <summary>How long it took, in seconds.</summary>
    public int? DurationSeconds { get; init; }

    /// <summary>The run's page on bitbucket.org — the link to hand a human.</summary>
    public string? Url { get; init; }
}

/// <summary>One page of a repository's pipeline runs, newest first.</summary>
internal sealed record PipelineListResult
{
    /// <summary>The runs on this page.</summary>
    public IReadOnlyList<PipelineSummary> Pipelines { get; init; } = [];

    /// <summary>Cursor for the next page, or absent on the last one.</summary>
    public string? NextCursor { get; init; }

    /// <summary>Total runs matching the filter, when Bitbucket reported a count.</summary>
    public int? TotalSize { get; init; }
}

/// <summary>One step of a run — the granularity at which a build actually fails.</summary>
internal sealed record PipelineStepSummary
{
    /// <summary>The step's UUID, in braced form. This is what getPipelineStepLog wants as <c>stepUuid</c>.</summary>
    public string? Uuid { get; init; }

    /// <summary>The step's name from <c>bitbucket-pipelines.yml</c>, for example <c>Test and Build</c>.</summary>
    public string? Name { get; init; }

    /// <summary>The same vocabulary a pipeline uses, plus <c>NOT_RUN</c> for a step that was skipped.</summary>
    public string? State { get; init; }

    /// <summary>Whether the step has finished.</summary>
    public bool? Completed { get; init; }

    /// <summary>Bitbucket's identifier for the kind of failure, when it knows one.</summary>
    public string? ErrorKey { get; init; }

    /// <summary>
    /// Why the step failed, in Bitbucket's words. Read this before fetching any log: "the step
    /// timed out" or "the image could not be pulled" is frequently the whole answer.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>The container image the step ran in.</summary>
    public string? Image { get; init; }

    /// <summary>How long the step took, in seconds.</summary>
    public int? DurationSeconds { get; init; }

    /// <summary>When the step started; absent if it never did.</summary>
    public DateTimeOffset? StartedOn { get; init; }

    /// <summary>When the step finished; absent while it is still running.</summary>
    public DateTimeOffset? CompletedOn { get; init; }
}

/// <summary>
/// One pipeline run with its steps — enough to say what failed and where to read about it.
/// </summary>
internal sealed record PipelineDetail
{
    /// <summary>The run's UUID, in braced form.</summary>
    public string? Uuid { get; init; }

    /// <summary>The build number, as it appears in the run's web URL.</summary>
    public int? BuildNumber { get; init; }

    /// <summary>The flattened outcome or phase. Same vocabulary as <see cref="PipelineSummary.State"/>.</summary>
    public string? State { get; init; }

    /// <summary>Whether the run has finished.</summary>
    public bool? Completed { get; init; }

    /// <summary>The branch or tag the run was for.</summary>
    public string? RefName { get; init; }

    /// <summary><c>branch</c>, <c>tag</c>, <c>named_branch</c> or <c>bookmark</c>.</summary>
    public string? RefType { get; init; }

    /// <summary>The revision that was built. Also what listCodeInsights takes as <c>commit</c>.</summary>
    public string? CommitHash { get; init; }

    /// <summary>Which <c>bitbucket-pipelines.yml</c> definition matched.</summary>
    public string? SelectorType { get; init; }

    /// <summary>The matching pattern from that definition.</summary>
    public string? SelectorPattern { get; init; }

    /// <summary>What started the run.</summary>
    public string? Trigger { get; init; }

    /// <summary>Who started it.</summary>
    public UserSummary? Creator { get; init; }

    /// <summary>When it was queued.</summary>
    public DateTimeOffset? CreatedOn { get; init; }

    /// <summary>When it finished; absent while it is still running.</summary>
    public DateTimeOffset? CompletedOn { get; init; }

    /// <summary>How long it took, in seconds.</summary>
    public int? DurationSeconds { get; init; }

    /// <summary>The run's page on bitbucket.org.</summary>
    public string? Url { get; init; }

    /// <summary>The run's steps, in execution order.</summary>
    public IReadOnlyList<PipelineStepSummary> Steps { get; init; } = [];

    /// <summary>
    /// The UUID of the first step that did not succeed, or absent when none failed. The argument to
    /// pass straight to getPipelineStepLog.
    /// </summary>
    public string? FailedStepUuid { get; init; }

    /// <summary>What to do next, when there is an obvious next call.</summary>
    public string? Hint { get; init; }
}

/// <summary>The part of a step's log that was read, and an honest account of what was left out.</summary>
internal sealed record PipelineStepLogResult
{
    /// <summary>
    /// The log text, with any truncation markers already inside it. Never present this as the whole
    /// log when <see cref="Truncated"/> is set.
    /// </summary>
    public string Log { get; init; } = string.Empty;

    /// <summary>Whether anything was left out.</summary>
    public bool Truncated { get; init; }

    /// <summary>Lines returned, markers excluded.</summary>
    public int LinesShown { get; init; }

    /// <summary>Bytes read.</summary>
    public long BytesShown { get; init; }

    /// <summary>The log's full size, when Bitbucket reported one.</summary>
    public long? TotalBytes { get; init; }

    /// <summary>In <c>search</c> mode, how many lines matched — which can exceed <see cref="LinesShown"/>.</summary>
    public int? MatchCount { get; init; }

    /// <summary>Which call would show more. Absent when nothing was left out.</summary>
    public string? Hint { get; init; }
}
