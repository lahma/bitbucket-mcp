using System.Globalization;

using Bitbucket.Mcp.Http;
using Bitbucket.Mcp.Http.Models;
using Bitbucket.Mcp.Pipelines;
using Bitbucket.Mcp.Tools.Models;

namespace Bitbucket.Mcp.Tools;

/// <summary>
/// The pipeline half of the wire-to-result mapping.
/// </summary>
/// <remarks>
/// Split from <c>ResultMapper.cs</c> only for size — the pull-request half is already about 430
/// lines. The conventions are identical.
/// </remarks>
internal static partial class ResultMapper
{
    /// <summary>The state names that mean the run is over.</summary>
    private static readonly string[] TerminalStates =
        ["SUCCESSFUL", "FAILED", "ERROR", "STOPPED", "EXPIRED", "NOT_RUN"];

    /// <summary>Maps one page of pipeline runs.</summary>
    internal static PipelineListResult Pipelines(Page<PipelineDto> page, string workspace, string repositorySlug)
    {
        ArgumentNullException.ThrowIfNull(page);

        var pipelines = new List<PipelineSummary>(page.Items.Count);

        foreach (var item in page.Items)
        {
            pipelines.Add(PipelineSummary(item, workspace, repositorySlug));
        }

        return new PipelineListResult
        {
            Pipelines = pipelines,
            NextCursor = page.NextCursor,
            TotalSize = page.TotalSize,
        };
    }

    /// <summary>Maps one run summary.</summary>
    private static PipelineSummary PipelineSummary(PipelineDto dto, string workspace, string repositorySlug)
    {
        var state = FlattenState(dto.State?.Name, dto.State?.Result?.Name, dto.State?.Stage?.Name);

        return new PipelineSummary
        {
            Uuid = dto.Uuid,
            BuildNumber = dto.BuildNumber,
            State = state,
            Completed = IsTerminal(state),
            RefName = dto.Target?.RefName,
            RefType = dto.Target?.RefType,
            CommitHash = dto.Target?.Commit?.Hash,
            SelectorType = dto.Target?.Selector?.Type,
            SelectorPattern = dto.Target?.Selector?.Pattern,
            Trigger = dto.Trigger?.Name,
            Creator = User(dto.Creator),
            CreatedOn = dto.CreatedOn,
            CompletedOn = dto.CompletedOn,
            DurationSeconds = dto.DurationInSeconds,
            Url = PipelineUrl(workspace, repositorySlug, dto.BuildNumber),
        };
    }

    /// <summary>Maps one run with its steps, and works out which step to look at next.</summary>
    internal static PipelineDetail Pipeline(
        PipelineDto dto,
        IReadOnlyList<PipelineStepDto> steps,
        string workspace,
        string repositorySlug)
    {
        ArgumentNullException.ThrowIfNull(dto);
        ArgumentNullException.ThrowIfNull(steps);

        var state = FlattenState(dto.State?.Name, dto.State?.Result?.Name, dto.State?.Stage?.Name);

        var mapped = new List<PipelineStepSummary>(steps.Count);

        foreach (var step in steps)
        {
            mapped.Add(Step(step));
        }

        var failed = mapped.Find(step => step.State is "FAILED" or "ERROR");

        return new PipelineDetail
        {
            Uuid = dto.Uuid,
            BuildNumber = dto.BuildNumber,
            State = state,
            Completed = IsTerminal(state),
            RefName = dto.Target?.RefName,
            RefType = dto.Target?.RefType,
            CommitHash = dto.Target?.Commit?.Hash,
            SelectorType = dto.Target?.Selector?.Type,
            SelectorPattern = dto.Target?.Selector?.Pattern,
            Trigger = dto.Trigger?.Name,
            Creator = User(dto.Creator),
            CreatedOn = dto.CreatedOn,
            CompletedOn = dto.CompletedOn,
            DurationSeconds = dto.DurationInSeconds,
            Url = PipelineUrl(workspace, repositorySlug, dto.BuildNumber),
            Steps = mapped,
            FailedStepUuid = failed?.Uuid,
            Hint = Next(failed, state, dto.Target?.Commit?.Hash),
        };
    }

    /// <summary>Maps one step.</summary>
    private static PipelineStepSummary Step(PipelineStepDto dto)
    {
        var state = FlattenState(dto.State?.Name, dto.State?.Result?.Name, dto.State?.Stage?.Name);

        return new PipelineStepSummary
        {
            Uuid = dto.Uuid,
            Name = dto.Name,
            State = state,
            Completed = IsTerminal(state),
            ErrorKey = dto.State?.Result?.Error?.Key,
            ErrorMessage = dto.State?.Result?.Error?.Message,
            Image = dto.Image?.Name,
            DurationSeconds = dto.DurationInSeconds,
            StartedOn = dto.StartedOn,
            CompletedOn = dto.CompletedOn,
        };
    }

    /// <summary>
    /// Collapses Bitbucket's nested state into the one word that answers the question.
    /// </summary>
    /// <remarks>
    /// The shapes are <c>{name: COMPLETED, result: {name: FAILED}}</c>,
    /// <c>{name: IN_PROGRESS, stage: {name: RUNNING}}</c> and <c>{name: PENDING}</c>: the innermost
    /// name is always the informative one. An unrecognised shape falls through to the outer name
    /// rather than to null — Bitbucket's state union is open, and a future member must degrade to a
    /// word a reader can still act on.
    /// </remarks>
    internal static string? FlattenState(string? name, string? result, string? stage) =>
        result ?? stage ?? name;

    /// <summary>Whether a flattened state means the run or step is over.</summary>
    private static bool? IsTerminal(string? state) =>
        state is null ? null : Array.IndexOf(TerminalStates, state) >= 0;

    /// <summary>
    /// Composes the run's page on bitbucket.org.
    /// </summary>
    /// <remarks>
    /// Composed rather than read: a pipeline's <c>links</c> carries only <c>self</c> and
    /// <c>steps</c>, both API URLs, and no <c>html</c> link at all (verified against the live API on
    /// 2026-09-09). This is the same shape <c>listPullRequestStatuses</c> already returns as its
    /// <c>url</c> for a Pipelines build.
    /// </remarks>
    private static string? PipelineUrl(string workspace, string repositorySlug, int? buildNumber) =>
        buildNumber is { } number
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"https://bitbucket.org/{workspace}/{repositorySlug}/pipelines/results/{number}")
            : null;

    /// <summary>Names the call worth making next, when there is an obvious one.</summary>
    private static string? Next(PipelineStepSummary? failed, string? state, string? commit)
    {
        if (failed is null)
        {
            return state is "SUCCESSFUL" or null
                ? null
                : "No step reported a failure. If the run is still going, call getPipeline again; a STOPPED "
                  + "run was cancelled rather than broken.";
        }

        // An error message from Bitbucket usually is the diagnosis, so the log is offered as the
        // follow-up rather than as the first move.
        var opening = failed.ErrorMessage is { Length: > 0 }
            ? $"Step '{failed.Name}' failed: {failed.ErrorMessage}"
            : $"Step '{failed.Name}' failed.";

        var next = $" Read its log with getPipelineStepLog and stepUuid=\"{failed.Uuid}\".";

        var insights = commit is { Length: > 0 }
            ? $" listCodeInsights with commit=\"{commit}\" may name the failing file and line directly."
            : string.Empty;

        return opening + next + insights;
    }

    /// <summary>Maps a reduced log into the tool's result.</summary>
    internal static PipelineStepLogResult StepLog(LogExcerpt excerpt)
    {
        ArgumentNullException.ThrowIfNull(excerpt);

        return new PipelineStepLogResult
        {
            Log = excerpt.Text,
            Truncated = excerpt.Truncated,
            LinesShown = excerpt.LinesShown,
            BytesShown = excerpt.BytesShown,
            TotalBytes = excerpt.TotalBytes,
            MatchCount = excerpt.MatchCount,
            Hint = excerpt.Hint,
        };
    }

    /// <summary>Maps one Code Insights report.</summary>
    internal static CodeInsightReport Report(CodeInsightsReportDto dto, int? annotationsShown)
    {
        ArgumentNullException.ThrowIfNull(dto);

        return new CodeInsightReport
        {
            // external_id is the reporter's own key and is the friendlier of the two to pass back,
            // but it is optional — the UUID is what always exists.
            ReportId = dto.Uuid ?? dto.ExternalId,
            Title = dto.Title,
            Details = dto.Details,
            Reporter = dto.Reporter,
            ReportType = dto.ReportType,
            Result = dto.Result,
            Url = dto.Link,
            CreatedOn = dto.CreatedOn,
            AnnotationsShown = annotationsShown,
        };
    }

    /// <summary>Maps one annotation, optionally tagged with the report it came from.</summary>
    internal static CodeInsightAnnotation Annotation(CodeInsightsAnnotationDto dto, string? reportTitle)
    {
        ArgumentNullException.ThrowIfNull(dto);

        return new CodeInsightAnnotation
        {
            Path = dto.Path,
            Line = dto.Line,
            Summary = dto.Summary,
            Details = dto.Details,
            AnnotationType = dto.AnnotationType,
            Result = dto.Result,
            Severity = dto.Severity,
            Url = dto.Link,
            ReportTitle = reportTitle,
        };
    }
}
