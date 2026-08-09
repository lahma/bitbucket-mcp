using Bitbucket.Mcp.Diffs;
using Bitbucket.Mcp.Http;
using Bitbucket.Mcp.Http.Models;
using Bitbucket.Mcp.Tools.Models;

namespace Bitbucket.Mcp.Tools;

/// <summary>
/// Turns Bitbucket's wire shapes into the tool results.
/// </summary>
/// <remarks>
/// <para>
/// The mapping is where the token budget is actually won: a Bitbucket pull request object carries a
/// dozen <c>links</c> sub-objects, rendered HTML bodies, object-type discriminators and repository
/// echoes, none of which any caller reads. What survives is what a reviewer would ask for.
/// </para>
/// <para>
/// It is also the boundary that keeps the wire DTOs out of the tool schemas: the result records are
/// camelCase and ours, the DTOs are snake_case and Bitbucket's, and nothing serialises both.
/// </para>
/// </remarks>
internal static class ResultMapper
{
    /// <summary>Collapses an account to a name and the UUID that identifies it.</summary>
    internal static UserSummary? User(AccountDto? account)
    {
        if (account is null)
        {
            return null;
        }

        return new UserSummary
        {
            Name = account.DisplayName ?? account.Nickname,
            Uuid = account.Uuid,
        };
    }

    /// <summary>Maps a list entry.</summary>
    internal static PullRequestSummary Summary(PullRequestSummaryDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        return new PullRequestSummary
        {
            Id = dto.Id ?? 0,
            Title = dto.Title,
            State = dto.State,
            Draft = dto.Draft,
            Author = User(dto.Author),
            SourceBranch = dto.Source?.Branch?.Name,
            DestinationBranch = dto.Destination?.Branch?.Name,
            CreatedOn = dto.CreatedOn,
            UpdatedOn = dto.UpdatedOn,
            CommentCount = dto.CommentCount,
            TaskCount = dto.TaskCount,
            Url = dto.Links?.Html?.Href,
        };
    }

    /// <summary>Maps one page of list entries.</summary>
    internal static PullRequestListResult List(Page<PullRequestSummaryDto> page)
    {
        ArgumentNullException.ThrowIfNull(page);

        var pullRequests = new List<PullRequestSummary>(page.Items.Count);

        foreach (var item in page.Items)
        {
            pullRequests.Add(Summary(item));
        }

        return new PullRequestListResult
        {
            PullRequests = pullRequests,
            NextCursor = page.NextCursor,
            TotalSize = page.TotalSize,
        };
    }

    /// <summary>Maps a single pull request, folding review stances into the reviewer list.</summary>
    internal static PullRequestDetail Detail(PullRequestDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        var participants = Participants(dto.Participants);

        return new PullRequestDetail
        {
            Id = dto.Id ?? 0,
            Title = dto.Title,
            State = dto.State,
            Draft = dto.Draft,
            Description = dto.Description,
            Author = User(dto.Author),
            SourceBranch = dto.Source?.Branch?.Name,
            DestinationBranch = dto.Destination?.Branch?.Name,
            CreatedOn = dto.CreatedOn,
            UpdatedOn = dto.UpdatedOn,
            CommentCount = dto.CommentCount,
            TaskCount = dto.TaskCount,
            CloseSourceBranch = dto.CloseSourceBranch,
            Reason = string.IsNullOrWhiteSpace(dto.Reason) ? null : dto.Reason,
            MergeCommitHash = dto.MergeCommit?.Hash,
            ClosedBy = User(dto.ClosedBy),
            Reviewers = Reviewers(dto.Reviewers, dto.Participants),
            Participants = participants,
            Url = dto.Links?.Html?.Href,
        };
    }

    /// <summary>Maps the participant list, dropping entries with nobody attached.</summary>
    private static List<ParticipantSummary>? Participants(IReadOnlyList<ParticipantDto>? participants)
    {
        if (participants is null || participants.Count == 0)
        {
            return null;
        }

        var result = new List<ParticipantSummary>(participants.Count);

        foreach (var participant in participants)
        {
            if (participant.User is null)
            {
                continue;
            }

            result.Add(new ParticipantSummary
            {
                Name = participant.User.DisplayName ?? participant.User.Nickname,
                Uuid = participant.User.Uuid,
                Approved = participant.Approved,
                State = participant.State,
            });
        }

        return result.Count == 0 ? null : result;
    }

    /// <summary>
    /// Maps the requested reviewers, filling each one's stance from the participant list.
    /// </summary>
    /// <remarks>
    /// Bitbucket keeps the two apart: <c>reviewers</c> says whose review was asked for and
    /// <c>participants</c> says what everyone did. A reviewer without their stance is not much use
    /// to a caller deciding whether a pull request is ready, so they are joined here on UUID — the
    /// only identifier stable enough to join on.
    /// </remarks>
    private static List<ParticipantSummary>? Reviewers(
        IReadOnlyList<AccountDto>? reviewers,
        IReadOnlyList<ParticipantDto>? participants)
    {
        if (reviewers is null || reviewers.Count == 0)
        {
            return null;
        }

        var stances = new Dictionary<string, ParticipantDto>(StringComparer.Ordinal);

        if (participants is not null)
        {
            foreach (var participant in participants)
            {
                if (!string.IsNullOrEmpty(participant.User?.Uuid))
                {
                    stances[participant.User.Uuid] = participant;
                }
            }
        }

        var result = new List<ParticipantSummary>(reviewers.Count);

        foreach (var reviewer in reviewers)
        {
            ParticipantDto? stance = null;

            if (!string.IsNullOrEmpty(reviewer.Uuid))
            {
                _ = stances.TryGetValue(reviewer.Uuid, out stance);
            }

            result.Add(new ParticipantSummary
            {
                Name = reviewer.DisplayName ?? reviewer.Nickname,
                Uuid = reviewer.Uuid,
                Approved = stance?.Approved,
                State = stance?.State,
            });
        }

        return result;
    }

    /// <summary>Maps one page of diffstat entries.</summary>
    internal static DiffStatResult DiffStat(Page<DiffStatEntryDto> page)
    {
        ArgumentNullException.ThrowIfNull(page);

        var files = new List<DiffStatFile>(page.Items.Count);

        foreach (var entry in page.Items)
        {
            files.Add(new DiffStatFile
            {
                Path = entry.New?.Path ?? entry.Old?.Path,
                Status = entry.Status,
                LinesAdded = entry.LinesAdded,
                LinesRemoved = entry.LinesRemoved,
            });
        }

        return new DiffStatResult
        {
            Files = files,
            NextCursor = page.NextCursor,
            TotalFiles = page.TotalSize,
        };
    }

    /// <summary>Maps a truncated diff, attaching the continuation hint when anything was cut.</summary>
    internal static DiffResult Diff(TruncatedDiff diff)
    {
        ArgumentNullException.ThrowIfNull(diff);

        var files = new List<DiffFileDiff>(diff.Files.Count);

        foreach (var file in diff.Files)
        {
            files.Add(new DiffFileDiff
            {
                Path = file.Path,
                Status = Status(file.Status),
                Diff = file.Text,
                Truncated = file.Truncated,
                LinesShown = file.LinesShown,
                LinesTotal = file.LinesTotal,
            });
        }

        return new DiffResult
        {
            Files = files,
            Truncated = diff.Truncated,
            Hint = diff.Truncated
                ? "Part of this diff was left out; every cut is marked inline in the diff text. To see more, " +
                  "call getPullRequestDiff again with paths=[\"...\"] naming fewer files and a larger " +
                  "maxLinesPerFile, or with mode=\"diffstat\" to list every changed file first."
                : null,
        };
    }

    /// <summary>
    /// Renders a parsed file's status in the same vocabulary diffstat uses, so a caller does not
    /// have to know which of the two calls produced the word.
    /// </summary>
    internal static string Status(DiffFileStatus status) => status switch
    {
        DiffFileStatus.Added => "added",
        DiffFileStatus.Removed => "removed",
        DiffFileStatus.Renamed => "renamed",
        DiffFileStatus.Binary => "binary",
        _ => "modified",
    };

    /// <summary>Maps one page of comments, dropping deleted ones.</summary>
    /// <remarks>
    /// Bitbucket returns deleted comments with their content blanked so a thread keeps its shape.
    /// Rendering them would mean showing the model empty comments it cannot act on, so they are
    /// filtered out here — the one place that decision has to be made.
    /// </remarks>
    internal static CommentListResult Comments(Page<CommentDto> page)
    {
        ArgumentNullException.ThrowIfNull(page);

        var comments = new List<CommentSummary>(page.Items.Count);

        foreach (var comment in page.Items)
        {
            if (comment.Deleted == true)
            {
                continue;
            }

            comments.Add(new CommentSummary
            {
                Id = comment.Id ?? 0,
                Author = User(comment.User),
                CreatedOn = comment.CreatedOn,
                Content = comment.Content?.Raw,
                Path = comment.Inline?.Path,
                Line = comment.Inline?.To ?? comment.Inline?.From,
                ParentId = comment.Parent?.Id,
                Resolved = comment.Resolution is not null,
                Url = comment.Links?.Html?.Href,
            });
        }

        return new CommentListResult
        {
            Comments = comments,
            NextCursor = page.NextCursor,
        };
    }

    /// <summary>Maps a freshly created comment, echoing the anchor it was placed on.</summary>
    internal static CommentResult Comment(CommentDto dto, InlineAnchor? anchor)
    {
        ArgumentNullException.ThrowIfNull(dto);

        return new CommentResult
        {
            Id = dto.Id ?? 0,
            Author = User(dto.User),
            CreatedOn = dto.CreatedOn,
            Content = dto.Content?.Raw,
            Path = dto.Inline?.Path ?? anchor?.Inline.Path,
            Line = dto.Inline?.To ?? dto.Inline?.From ?? anchor?.Line,
            LineType = anchor is null ? null : LineType(anchor.LineType),
            MatchedText = anchor?.MatchedText,
            ParentId = dto.Parent?.Id,
            Url = dto.Links?.Html?.Href,
        };
    }

    /// <summary>Maps one page of effective default reviewers, dropping entries with nobody in them.</summary>
    internal static DefaultReviewerListResult DefaultReviewers(Page<DefaultReviewerDto> page)
    {
        ArgumentNullException.ThrowIfNull(page);

        var reviewers = new List<DefaultReviewerSummary>(page.Items.Count);

        foreach (var entry in page.Items)
        {
            if (entry.User is null)
            {
                continue;
            }

            reviewers.Add(new DefaultReviewerSummary
            {
                Name = entry.User.DisplayName ?? entry.User.Nickname,
                Uuid = entry.User.Uuid,
                ReviewerType = entry.ReviewerType,
            });
        }

        return new DefaultReviewerListResult
        {
            Reviewers = reviewers,
            NextCursor = page.NextCursor,
            TotalSize = page.TotalSize,
        };
    }

    /// <summary>Maps one page of build statuses.</summary>
    internal static PullRequestStatusListResult Statuses(Page<CommitStatusDto> page)
    {
        ArgumentNullException.ThrowIfNull(page);

        var statuses = new List<PullRequestStatusSummary>(page.Items.Count);

        foreach (var status in page.Items)
        {
            statuses.Add(new PullRequestStatusSummary
            {
                State = status.State,
                Key = status.Key,
                Name = status.Name,
                Url = status.Url,
                Description = string.IsNullOrWhiteSpace(status.Description) ? null : status.Description,
                Refname = status.Refname,
                UpdatedOn = status.UpdatedOn,
            });
        }

        return new PullRequestStatusListResult
        {
            Statuses = statuses,
            NextCursor = page.NextCursor,
            TotalSize = page.TotalSize,
        };
    }

    /// <summary>Maps one task.</summary>
    internal static PullRequestTask Task(TaskDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        return new PullRequestTask
        {
            Id = dto.Id ?? 0,
            State = dto.State,
            Content = dto.Content?.Raw,
            Creator = User(dto.Creator),
            ResolvedBy = User(dto.ResolvedBy),
            CommentId = dto.Comment?.Id,
            CreatedOn = dto.CreatedOn,
            UpdatedOn = dto.UpdatedOn,
        };
    }

    /// <summary>Maps one page of tasks.</summary>
    internal static PullRequestTaskListResult Tasks(Page<TaskDto> page)
    {
        ArgumentNullException.ThrowIfNull(page);

        var tasks = new List<PullRequestTask>(page.Items.Count);

        foreach (var task in page.Items)
        {
            tasks.Add(Task(task));
        }

        return new PullRequestTaskListResult
        {
            Tasks = tasks,
            NextCursor = page.NextCursor,
            TotalSize = page.TotalSize,
        };
    }

    /// <summary>Renders a diff line's side in the vocabulary the <c>lineType</c> parameter uses.</summary>
    internal static string LineType(DiffLineType lineType) => lineType switch
    {
        DiffLineType.Removed => "REMOVED",
        DiffLineType.Context => "CONTEXT",
        _ => "ADDED",
    };
}
