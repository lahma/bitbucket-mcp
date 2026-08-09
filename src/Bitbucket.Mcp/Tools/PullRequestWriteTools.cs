using System.ComponentModel;
using System.Globalization;
using System.Net;

using Bitbucket.Mcp.Configuration;
using Bitbucket.Mcp.Diffs;
using Bitbucket.Mcp.Http;
using Bitbucket.Mcp.Http.Models;
using Bitbucket.Mcp.Tools.Models;

using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Bitbucket.Mcp.Tools;

/// <summary>
/// The write half of the pull request surface: create, update, comment, resolve, task, review,
/// merge, decline.
/// </summary>
/// <remarks>
/// <para>
/// The annotations are the load-bearing part of this file. <c>Destructive</c> defaults to
/// <see langword="true"/> in the SDK, so the six tools that do not destroy anything — opening a pull
/// request, posting a comment, resolving a thread, adding or updating a task, setting one's own
/// review state — say <c>Destructive = false</c> explicitly. Getting that wrong makes a client
/// prompt for confirmation before every comment, or worse, not prompt before a merge.
/// </para>
/// <para>
/// <c>updatePullRequestTask</c> is the one judgement call in that list: its <c>content</c> argument
/// overwrites a task's text irrecoverably, which is exactly why <c>updatePullRequest</c> is marked
/// destructive. It is marked non-destructive anyway, because the overwhelmingly common call is the
/// state flip — ticking a task off — and a confirmation prompt in front of every tick would train a
/// user to click through the prompts that matter. The description says the text is replaced.
/// </para>
/// <para>
/// Sealed rather than <c>static</c> because C# forbids a static class as a type argument (CS0718)
/// and registration goes through <c>WithTools&lt;PullRequestWriteTools&gt;(jsonOptions)</c>; the
/// methods themselves are static (D8).
/// </para>
/// </remarks>
[McpServerToolType]
internal sealed class PullRequestWriteTools
{
    /// <summary>Bitbucket's object-type discriminator for a merge body.</summary>
    private const string MergeRequestType = "pullrequest_merge_parameters";

    private PullRequestWriteTools()
    {
    }

    [McpServerTool(
        Name = "createPullRequest",
        Title = "Create pull request",
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = true,
        UseStructuredContent = true)]
    [Description(
        "Opens a new pull request. Only the title and the source branch are required; omitting " +
        "destinationBranch targets the repository's main branch. Reviewers are Bitbucket account UUIDs in " +
        "braced form ({...}) — read them from listDefaultReviewers or getPullRequest, never guess them from " +
        "names. Calling this twice creates two pull requests, so check first with listPullRequests using " +
        "sourceBranch and state=\"ALL\" to see whether that branch already has one.")]
    public static async Task<PullRequestDetail> CreatePullRequestAsync(
        BitbucketApiClient client,
        BitbucketMcpOptions options,
        [Description("Repository slug — the second URL segment of bitbucket.org/{workspace}/{repository}, not the repository's display name.")]
        string repository,
        [Description("The pull request title.")]
        string title,
        [Description("The branch to merge from, without any refs/heads/ prefix.")]
        string sourceBranch,
        [Description("Workspace slug — the first URL segment of bitbucket.org/{workspace}/{repository}, not the workspace's display name. Optional when BITBUCKET_DEFAULT_WORKSPACE is set.")]
        string? workspace = null,
        [Description("The branch to merge into, without any refs/heads/ prefix. Omit to target the repository's main branch.")]
        string? destinationBranch = null,
        [Description("The pull request description, in Bitbucket-flavoured Markdown.")]
        string? description = null,
        [Description("Reviewers to request, as Bitbucket account UUIDs in braced form ({...}). Read them from listDefaultReviewers, or from getPullRequest on an existing pull request. Display names, nicknames and emails are rejected.")]
        string[]? reviewers = null,
        [Description("Delete the source branch when the pull request is merged. Defaults to false.")]
        bool closeSourceBranch = false,
        [Description("Open the pull request as a draft. Defaults to false.")]
        bool draft = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        var slug = ToolDefaults.RequireRepository(repository);
        var resolvedWorkspace = ToolDefaults.ResolveWorkspace(workspace, options);

        if (string.IsNullOrWhiteSpace(title))
        {
            throw new McpException("title is required: Bitbucket rejects a pull request without one.");
        }

        if (string.IsNullOrWhiteSpace(sourceBranch))
        {
            throw new McpException(
                "sourceBranch is required: it is the branch whose commits the pull request proposes to merge.");
        }

        var body = new CreatePullRequestRequest
        {
            Title = title.Trim(),
            Source = Endpoint(sourceBranch),
            Destination = string.IsNullOrWhiteSpace(destinationBranch) ? null : Endpoint(destinationBranch),
            Description = string.IsNullOrWhiteSpace(description) ? null : description,
            Reviewers = Accounts(reviewers),
            CloseSourceBranch = closeSourceBranch,
            Draft = draft,
        };

        var context = new ToolCallContext("createPullRequest", resolvedWorkspace, slug);

        return await ToolErrors.ExecuteAsync(context, async () =>
        {
            var dto = await client.CreatePullRequestAsync(resolvedWorkspace, slug, body, cancellationToken)
                .ConfigureAwait(false);

            return ResultMapper.Detail(dto);
        }).ConfigureAwait(false);
    }

    [McpServerTool(
        Name = "updatePullRequest",
        Title = "Update pull request",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = true,
        UseStructuredContent = true)]
    [Description(
        "Changes an existing pull request's title, description, destination branch or reviewer list. Anything " +
        "left unset keeps its current value — except reviewers, which REPLACES the whole list, so include the " +
        "existing reviewers (from getPullRequest) when adding one. Overwrites whatever is there now, " +
        "including edits made by someone else, so read the pull request first.")]
    public static async Task<PullRequestDetail> UpdatePullRequestAsync(
        BitbucketApiClient client,
        BitbucketMcpOptions options,
        [Description("Repository slug — the second URL segment of bitbucket.org/{workspace}/{repository}, not the repository's display name.")]
        string repository,
        [Description("The pull request number, as it appears in the pull request's URL.")]
        int pullRequestId,
        [Description("Workspace slug — the first URL segment of bitbucket.org/{workspace}/{repository}, not the workspace's display name. Optional when BITBUCKET_DEFAULT_WORKSPACE is set.")]
        string? workspace = null,
        [Description("A new title. Omit to keep the current one.")]
        string? title = null,
        [Description("A new description, in Bitbucket-flavoured Markdown. Replaces the current one entirely. Omit to keep it.")]
        string? description = null,
        [Description("Retarget the pull request at a different branch, without any refs/heads/ prefix. Omit to keep the current destination.")]
        string? destinationBranch = null,
        [Description("The complete new reviewer list, as Bitbucket account UUIDs in braced form ({...}). Read UUIDs from listDefaultReviewers or getPullRequest. This REPLACES the existing list; omit to leave the reviewers untouched.")]
        string[]? reviewers = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        var slug = ToolDefaults.RequireRepository(repository);
        var resolvedWorkspace = ToolDefaults.ResolveWorkspace(workspace, options);
        var id = ToolDefaults.RequirePullRequestId(pullRequestId);

        var body = new UpdatePullRequestRequest
        {
            Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim(),
            Description = string.IsNullOrWhiteSpace(description) ? null : description,
            Destination = string.IsNullOrWhiteSpace(destinationBranch) ? null : Endpoint(destinationBranch),
            Reviewers = Accounts(reviewers),
        };

        if (body.Title is null && body.Description is null && body.Destination is null && body.Reviewers is null)
        {
            throw new McpException(
                "Nothing to update: pass at least one of title, description, destinationBranch or reviewers.");
        }

        var context = new ToolCallContext("updatePullRequest", resolvedWorkspace, slug, id);

        return await ToolErrors.ExecuteAsync(context, async () =>
        {
            var dto = await client.UpdatePullRequestAsync(resolvedWorkspace, slug, id, body, cancellationToken)
                .ConfigureAwait(false);

            return ResultMapper.Detail(dto);
        }).ConfigureAwait(false);
    }

    [McpServerTool(
        Name = "addPullRequestComment",
        Title = "Add pull request comment",
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = true,
        UseStructuredContent = true)]
    [Description(
        "Posts a comment on a pull request. Without path it comments on the pull request as a whole (or " +
        "replies, with parentCommentId). With path it becomes an inline comment on a line of the diff: pass " +
        "codeSnippet with the line's text copied verbatim out of getPullRequestDiff, which is far more " +
        "reliable than counting lines — the snippet is resolved against the diff and an ambiguous or missing " +
        "match is reported with the candidate lines rather than guessed. Use line plus lineType only when a " +
        "snippet cannot identify the line.")]
    public static async Task<CommentResult> AddPullRequestCommentAsync(
        BitbucketApiClient client,
        BitbucketMcpOptions options,
        [Description("Repository slug — the second URL segment of bitbucket.org/{workspace}/{repository}, not the repository's display name.")]
        string repository,
        [Description("The pull request number, as it appears in the pull request's URL.")]
        int pullRequestId,
        [Description("The comment body, in Bitbucket-flavoured Markdown.")]
        string content,
        [Description("Workspace slug — the first URL segment of bitbucket.org/{workspace}/{repository}, not the workspace's display name. Optional when BITBUCKET_DEFAULT_WORKSPACE is set.")]
        string? workspace = null,
        [Description("The id of the comment to reply to, from getPullRequestComments. Omit to start a new thread.")]
        long? parentCommentId = null,
        [Description("Repository-relative path of the file to comment on, spelled exactly as the diff spells it. Setting this makes the comment inline; omit it to comment on the pull request itself.")]
        string? path = null,
        [Description("The line number to anchor to, when codeSnippet cannot identify the line. Numbered in the file after the change for ADDED and CONTEXT, before it for REMOVED.")]
        int? line = null,
        [Description("How to read line: ADDED (a line the change adds), REMOVED (one it deletes) or CONTEXT (an unchanged line shown for context). Defaults to ADDED.")]
        string? lineType = null,
        [Description("First line of a multi-line comment; must be smaller than line. Omit for a comment on a single line.")]
        int? startLine = null,
        [Description("The text of the line to comment on, copied verbatim out of the diff. The preferred way to place an inline comment: whitespace is ignored and a copied +/- marker is tolerated.")]
        string? codeSnippet = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        var slug = ToolDefaults.RequireRepository(repository);
        var resolvedWorkspace = ToolDefaults.ResolveWorkspace(workspace, options);
        var id = ToolDefaults.RequirePullRequestId(pullRequestId);

        if (string.IsNullOrWhiteSpace(content))
        {
            throw new McpException("content is required: a comment needs a body.");
        }

        var inline = !string.IsNullOrWhiteSpace(path);

        if (!inline && (line is not null || startLine is not null || !string.IsNullOrWhiteSpace(codeSnippet)))
        {
            throw new McpException(
                "An inline comment needs path as well: line, startLine and codeSnippet only mean something " +
                "relative to a file. Pass path with the file to comment on, or drop them for a comment on the " +
                "pull request itself.");
        }

        var resolvedLineType = ResolveLineType(lineType);

        var body = new CommentRequest
        {
            Content = new CommentContentRequest { Raw = content },
            Parent = parentCommentId is { } parent ? new CommentParentRequest { Id = parent } : null,
        };

        var context = new ToolCallContext("addPullRequestComment", resolvedWorkspace, slug, id);

        return await ToolErrors.ExecuteAsync(context, async () =>
        {
            InlineAnchor? anchor = null;

            if (inline)
            {
                // One file's diff, not the whole pull request's: the anchor only ever needs the file
                // being commented on, and a whole-PR diff is exactly what answers 555.
                var raw = await client.GetDiffAsync(
                        resolvedWorkspace,
                        slug,
                        id,
                        [path!],
                        contextLines: null,
                        ignoreWhitespace: null,
                        cancellationToken)
                    .ConfigureAwait(false);

                var files = UnifiedDiffParser.Split(raw);

                // The resolver validates and produces actionable messages of its own; the funnel
                // passes an InlineAnchorException through unchanged.
                anchor = InlineAnchorResolver.Resolve(files, path!, codeSnippet, line, resolvedLineType, startLine);
                body = body with { Inline = anchor.Inline };
            }

            var dto = await client.AddCommentAsync(resolvedWorkspace, slug, id, body, cancellationToken)
                .ConfigureAwait(false);

            return ResultMapper.Comment(dto, anchor);
        }).ConfigureAwait(false);
    }

    [McpServerTool(
        Name = "resolvePullRequestComment",
        Title = "Resolve pull request comment",
        ReadOnly = false,
        Destructive = false,
        Idempotent = true,
        OpenWorld = true,
        UseStructuredContent = true)]
    [Description(
        "Marks a comment thread resolved, or reopens it with resolved=false. This is the \"done\" tick on a " +
        "review thread — resolve the threads you have addressed rather than only replying to them, because a " +
        "repository can require every thread resolved before it will merge. Works on any top-level comment, " +
        "inline or on the pull request as a whole; a reply has no thread of its own and is refused. Asking for " +
        "the state it is already in is not an error, but it comes back without resolvedBy and resolvedOn, " +
        "because Bitbucket answers a no-op with nothing to echo.")]
    public static async Task<CommentResolutionResult> ResolvePullRequestCommentAsync(
        BitbucketApiClient client,
        BitbucketMcpOptions options,
        [Description("Repository slug — the second URL segment of bitbucket.org/{workspace}/{repository}, not the repository's display name.")]
        string repository,
        [Description("The pull request number, as it appears in the pull request's URL.")]
        int pullRequestId,
        [Description("The comment whose thread to resolve, from getPullRequestComments. Must be a top-level comment — one with no parentId. It does not have to be inline; a comment on the pull request as a whole has a thread too.")]
        long commentId,
        [Description("true resolves the thread, false reopens it.")]
        bool resolved,
        [Description("Workspace slug — the first URL segment of bitbucket.org/{workspace}/{repository}, not the workspace's display name. Optional when BITBUCKET_DEFAULT_WORKSPACE is set.")]
        string? workspace = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        var slug = ToolDefaults.RequireRepository(repository);
        var resolvedWorkspace = ToolDefaults.ResolveWorkspace(workspace, options);
        var id = ToolDefaults.RequirePullRequestId(pullRequestId);
        var comment = RequireCommentId(commentId);

        var context = new ToolCallContext("resolvePullRequestComment", resolvedWorkspace, slug, id);

        return await ToolErrors.ExecuteAsync(context, async () =>
        {
            CommentResolutionDto? resolution = null;

            if (resolved)
            {
                try
                {
                    resolution = await client
                        .ResolveCommentAsync(resolvedWorkspace, slug, id, comment, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (BitbucketApiException exception) when (exception.StatusCode == HttpStatusCode.Conflict)
                {
                    // Bitbucket answers 409 for "already resolved". The caller asked for an end
                    // state and it is already in place, which is what makes this tool idempotent.
                }
            }
            else
            {
                try
                {
                    await client.UnresolveCommentAsync(resolvedWorkspace, slug, id, comment, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (BitbucketApiException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
                {
                    // 404 here means either "no such comment" or "it was not resolved". The second
                    // is the requested end state; the first would have failed the resolve path too,
                    // and reporting it as a failure to reopen an open thread helps nobody.
                }
            }

            return new CommentResolutionResult
            {
                CommentId = comment,
                Resolved = resolved,
                ResolvedBy = ResultMapper.User(resolution?.User),
                ResolvedOn = resolution?.CreatedOn,
            };
        }).ConfigureAwait(false);
    }

    [McpServerTool(
        Name = "addPullRequestTask",
        Title = "Add pull request task",
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = true,
        UseStructuredContent = true)]
    [Description(
        "Adds a task to a pull request: one tracked item of work the author has to deal with, which Bitbucket " +
        "counts and can require resolved before merging. Prefer a task over a comment for anything that must " +
        "actually be done — a comment can be read and forgotten, a task cannot. Pass commentId to hang the " +
        "task off an existing comment, which is how a review remark becomes actionable without repeating it. " +
        "Calling this twice creates two tasks, so check listPullRequestTasks first.")]
    public static async Task<PullRequestTask> AddPullRequestTaskAsync(
        BitbucketApiClient client,
        BitbucketMcpOptions options,
        [Description("Repository slug — the second URL segment of bitbucket.org/{workspace}/{repository}, not the repository's display name.")]
        string repository,
        [Description("The pull request number, as it appears in the pull request's URL.")]
        int pullRequestId,
        [Description("The task text, in Bitbucket-flavoured Markdown. One thing to do, phrased so it can be ticked off.")]
        string content,
        [Description("Workspace slug — the first URL segment of bitbucket.org/{workspace}/{repository}, not the workspace's display name. Optional when BITBUCKET_DEFAULT_WORKSPACE is set.")]
        string? workspace = null,
        [Description("The comment to attach the task to, from getPullRequestComments or addPullRequestComment. Omit for a free-standing task.")]
        long? commentId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        var slug = ToolDefaults.RequireRepository(repository);
        var resolvedWorkspace = ToolDefaults.ResolveWorkspace(workspace, options);
        var id = ToolDefaults.RequirePullRequestId(pullRequestId);

        if (string.IsNullOrWhiteSpace(content))
        {
            throw new McpException("content is required: a task needs to say what has to be done.");
        }

        var body = new CreateTaskRequest
        {
            Content = new CommentContentRequest { Raw = content },
            Comment = commentId is { } parent
                ? new TaskCommentRequest { Id = RequireCommentId(parent) }
                : null,
        };

        var context = new ToolCallContext("addPullRequestTask", resolvedWorkspace, slug, id);

        return await ToolErrors.ExecuteAsync(context, async () =>
        {
            var dto = await client.CreateTaskAsync(resolvedWorkspace, slug, id, body, cancellationToken)
                .ConfigureAwait(false);

            return ResultMapper.Task(dto);
        }).ConfigureAwait(false);
    }

    [McpServerTool(
        Name = "updatePullRequestTask",
        Title = "Update pull request task",
        ReadOnly = false,
        Destructive = false,
        Idempotent = true,
        OpenWorld = true,
        UseStructuredContent = true)]
    [Description(
        "Ticks a pull request task off (state=\"RESOLVED\"), reopens it (\"UNRESOLVED\"), or rewrites its " +
        "text. Pass at least one of state and content. Setting the state it already has changes nothing. " +
        "content REPLACES the task's text, so read it with listPullRequestTasks first if you mean to amend " +
        "rather than replace it.")]
    public static async Task<PullRequestTask> UpdatePullRequestTaskAsync(
        BitbucketApiClient client,
        BitbucketMcpOptions options,
        [Description("Repository slug — the second URL segment of bitbucket.org/{workspace}/{repository}, not the repository's display name.")]
        string repository,
        [Description("The pull request number, as it appears in the pull request's URL.")]
        int pullRequestId,
        [Description("The task to change, from listPullRequestTasks.")]
        long taskId,
        [Description("Workspace slug — the first URL segment of bitbucket.org/{workspace}/{repository}, not the workspace's display name. Optional when BITBUCKET_DEFAULT_WORKSPACE is set.")]
        string? workspace = null,
        [Description("RESOLVED to tick the task off, UNRESOLVED to reopen it. Omit to leave the state alone.")]
        string? state = null,
        [Description("Replacement task text, in Bitbucket-flavoured Markdown. Replaces the current text entirely. Omit to leave it alone.")]
        string? content = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        var slug = ToolDefaults.RequireRepository(repository);
        var resolvedWorkspace = ToolDefaults.ResolveWorkspace(workspace, options);
        var id = ToolDefaults.RequirePullRequestId(pullRequestId);
        var task = RequireTaskId(taskId);
        var resolvedState = ToolDefaults.ResolveTaskState(state);

        var body = new UpdateTaskRequest
        {
            State = resolvedState,
            Content = string.IsNullOrWhiteSpace(content)
                ? null
                : new CommentContentRequest { Raw = content },
        };

        if (body.State is null && body.Content is null)
        {
            throw new McpException(
                "Nothing to update: pass at least one of state (RESOLVED or UNRESOLVED) or content.");
        }

        var context = new ToolCallContext("updatePullRequestTask", resolvedWorkspace, slug, id);

        return await ToolErrors.ExecuteAsync(context, async () =>
        {
            var dto = await UpdateTaskAsync(client, resolvedWorkspace, slug, id, task, body, cancellationToken)
                .ConfigureAwait(false);

            return ResultMapper.Task(dto);
        }).ConfigureAwait(false);
    }

    [McpServerTool(
        Name = "setPullRequestReviewStatus",
        Title = "Set pull request review status",
        ReadOnly = false,
        Destructive = false,
        Idempotent = true,
        OpenWorld = true,
        UseStructuredContent = true)]
    [Description(
        "Sets the authenticated user's own review state on a pull request: APPROVED, CHANGES_REQUESTED, or " +
        "UNAPPROVED to withdraw both. Affects only this user's stance, never anyone else's, and setting the " +
        "same state twice changes nothing. Pass comment to post a general comment explaining the decision at " +
        "the same time — for feedback on specific lines use addPullRequestComment with a path instead.")]
    public static async Task<ReviewStatusResult> SetPullRequestReviewStatusAsync(
        BitbucketApiClient client,
        BitbucketMcpOptions options,
        [Description("Repository slug — the second URL segment of bitbucket.org/{workspace}/{repository}, not the repository's display name.")]
        string repository,
        [Description("The pull request number, as it appears in the pull request's URL.")]
        int pullRequestId,
        [Description("The review state to set: APPROVED, CHANGES_REQUESTED or UNAPPROVED (clears both flags).")]
        string status,
        [Description("Workspace slug — the first URL segment of bitbucket.org/{workspace}/{repository}, not the workspace's display name. Optional when BITBUCKET_DEFAULT_WORKSPACE is set.")]
        string? workspace = null,
        [Description("A general comment to post alongside the change, in Bitbucket-flavoured Markdown. Omit to change the state silently.")]
        string? comment = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        var slug = ToolDefaults.RequireRepository(repository);
        var resolvedWorkspace = ToolDefaults.ResolveWorkspace(workspace, options);
        var id = ToolDefaults.RequirePullRequestId(pullRequestId);
        var resolvedStatus = ResolveReviewStatus(status);

        var context = new ToolCallContext("setPullRequestReviewStatus", resolvedWorkspace, slug, id);

        return await ToolErrors.ExecuteAsync(context, async () =>
        {
            ParticipantDto? participant = null;

            switch (resolvedStatus)
            {
                case ReviewStatus.Approved:
                    participant = await client.ApproveAsync(resolvedWorkspace, slug, id, cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case ReviewStatus.ChangesRequested:
                    participant = await client.RequestChangesAsync(resolvedWorkspace, slug, id, cancellationToken)
                        .ConfigureAwait(false);
                    break;

                default:
                    // Both flags, because "unapproved" is the absence of either and Bitbucket tracks
                    // them separately. Deleting one that was never set is not an error worth
                    // surfacing - the requested end state is reached either way.
                    await TolerateMissingFlagAsync(
                            client.UnapproveAsync(resolvedWorkspace, slug, id, cancellationToken))
                        .ConfigureAwait(false);

                    await TolerateMissingFlagAsync(
                            client.UnrequestChangesAsync(resolvedWorkspace, slug, id, cancellationToken))
                        .ConfigureAwait(false);
                    break;
            }

            long? commentId = null;

            if (!string.IsNullOrWhiteSpace(comment))
            {
                var posted = await client.AddCommentAsync(
                        resolvedWorkspace,
                        slug,
                        id,
                        new CommentRequest { Content = new CommentContentRequest { Raw = comment } },
                        cancellationToken)
                    .ConfigureAwait(false);

                commentId = posted.Id;
            }

            return new ReviewStatusResult
            {
                Status = ReportedStatus(participant?.State, resolvedStatus),
                Approved = participant?.Approved ?? false,
                User = ResultMapper.User(participant?.User),
                CommentId = commentId,
            };
        }).ConfigureAwait(false);
    }

    [McpServerTool(
        Name = "mergePullRequest",
        Title = "Merge pull request",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = true,
        UseStructuredContent = true)]
    [Description(
        "Merges a pull request into its destination branch. This rewrites the destination branch and cannot " +
        "be undone from here — read the pull request with getPullRequest and confirm it is the right one and " +
        "actually approved, and check listPullRequestStatuses for a build that has not passed, before " +
        "calling. A strategy the repository has disabled is rejected, not substituted, and a conflicting " +
        "pull request fails with a 409 rather than merging partially.")]
    public static async Task<MergeResult> MergePullRequestAsync(
        BitbucketApiClient client,
        BitbucketMcpOptions options,
        [Description("Repository slug — the second URL segment of bitbucket.org/{workspace}/{repository}, not the repository's display name.")]
        string repository,
        [Description("The pull request number, as it appears in the pull request's URL.")]
        int pullRequestId,
        [Description("Workspace slug — the first URL segment of bitbucket.org/{workspace}/{repository}, not the workspace's display name. Optional when BITBUCKET_DEFAULT_WORKSPACE is set.")]
        string? workspace = null,
        [Description("How to merge: merge_commit, squash, fast_forward, squash_fast_forward, rebase_fast_forward or rebase_merge. Omit to use the repository's configured default.")]
        string? mergeStrategy = null,
        [Description("The merge commit message. Omit to let Bitbucket compose its default.")]
        string? message = null,
        [Description("Delete the source branch after merging. Omit to use the pull request's own close_source_branch setting.")]
        bool? closeSourceBranch = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        var slug = ToolDefaults.RequireRepository(repository);
        var resolvedWorkspace = ToolDefaults.ResolveWorkspace(workspace, options);
        var id = ToolDefaults.RequirePullRequestId(pullRequestId);

        var body = new MergeRequest
        {
            Type = MergeRequestType,
            MergeStrategy = ToolDefaults.ResolveMergeStrategy(mergeStrategy),
            Message = string.IsNullOrWhiteSpace(message) ? null : message,
            CloseSourceBranch = closeSourceBranch,
        };

        var context = new ToolCallContext("mergePullRequest", resolvedWorkspace, slug, id);

        return await ToolErrors.ExecuteAsync(context, async () =>
        {
            var dto = await client.MergeAsync(resolvedWorkspace, slug, id, body, cancellationToken)
                .ConfigureAwait(false);

            return new MergeResult
            {
                State = dto.State,
                MergeCommitHash = dto.MergeCommit?.Hash,
            };
        }).ConfigureAwait(false);
    }

    [McpServerTool(
        Name = "declinePullRequest",
        Title = "Decline pull request",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = true,
        UseStructuredContent = true)]
    [Description(
        "Declines a pull request, closing it without merging. The pull request cannot be reopened through " +
        "this server, so confirm with getPullRequest that it is the right one first. Prefer " +
        "setPullRequestReviewStatus with CHANGES_REQUESTED when the author is expected to keep working on it.")]
    public static async Task<DeclineResult> DeclinePullRequestAsync(
        BitbucketApiClient client,
        BitbucketMcpOptions options,
        [Description("Repository slug — the second URL segment of bitbucket.org/{workspace}/{repository}, not the repository's display name.")]
        string repository,
        [Description("The pull request number, as it appears in the pull request's URL.")]
        int pullRequestId,
        [Description("Workspace slug — the first URL segment of bitbucket.org/{workspace}/{repository}, not the workspace's display name. Optional when BITBUCKET_DEFAULT_WORKSPACE is set.")]
        string? workspace = null,
        [Description("Why it is being declined. Sent best effort: Bitbucket documents no request body for this endpoint, so the reason may not be stored.")]
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        var slug = ToolDefaults.RequireRepository(repository);
        var resolvedWorkspace = ToolDefaults.ResolveWorkspace(workspace, options);
        var id = ToolDefaults.RequirePullRequestId(pullRequestId);

        var context = new ToolCallContext("declinePullRequest", resolvedWorkspace, slug, id);

        return await ToolErrors.ExecuteAsync(context, async () =>
        {
            var dto = await client.DeclineAsync(
                    resolvedWorkspace,
                    slug,
                    id,
                    string.IsNullOrWhiteSpace(reason) ? null : reason,
                    cancellationToken)
                .ConfigureAwait(false);

            return new DeclineResult
            {
                State = dto.State,
                Reason = string.IsNullOrWhiteSpace(dto.Reason) ? null : dto.Reason,
            };
        }).ConfigureAwait(false);
    }

    /// <summary>The three review states a caller can ask for.</summary>
    private enum ReviewStatus
    {
        Approved,
        ChangesRequested,
        Unapproved,
    }

    private static PullRequestEndpointRequest Endpoint(string branch) =>
        new() { Branch = new BranchRequest { Name = branch.Trim() } };

    /// <summary>Rejects a comment id that cannot exist, before it becomes a confusing 404.</summary>
    private static long RequireCommentId(long commentId) => commentId > 0
        ? commentId
        : throw new McpException(string.Create(
            CultureInfo.InvariantCulture,
            $"commentId must be 1 or greater; got {commentId}. Read it from getPullRequestComments."));

    /// <summary>Rejects a task id that cannot exist.</summary>
    private static long RequireTaskId(long taskId) => taskId > 0
        ? taskId
        : throw new McpException(string.Create(
            CultureInfo.InvariantCulture,
            $"taskId must be 1 or greater; got {taskId}. Read it from listPullRequestTasks."));

    /// <summary>
    /// Sends the task update, retrying once with the task's existing text if Bitbucket turns out to
    /// insist on it.
    /// </summary>
    /// <remarks>
    /// Bitbucket's published schema for this body marks <em>both</em> <c>state</c> and <c>content</c>
    /// optional, so a state-only update is a legal partial update and is what goes out first — one
    /// request, no read. But the same endpoint documents a <c>400</c> for "there is a missing
    /// required field in the request", and the schema and the prose disagree often enough that
    /// betting the whole tool on the schema would be careless. So a 400 on a state-only body — and
    /// only that combination — is answered by fetching the task and resending its own text alongside
    /// the new state. If the fetched task has no text to resend, the original failure stands rather
    /// than becoming a second, less informative one.
    /// </remarks>
    private static async Task<TaskDto> UpdateTaskAsync(
        BitbucketApiClient client,
        string workspace,
        string repositorySlug,
        int pullRequestId,
        long taskId,
        UpdateTaskRequest body,
        CancellationToken cancellationToken)
    {
        try
        {
            return await client
                .UpdateTaskAsync(workspace, repositorySlug, pullRequestId, taskId, body, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (BitbucketApiException exception)
            when (exception.StatusCode == HttpStatusCode.BadRequest && body.Content is null)
        {
            var existing = await client
                .GetTaskAsync(workspace, repositorySlug, pullRequestId, taskId, cancellationToken)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(existing.Content?.Raw))
            {
                throw;
            }

            return await client
                .UpdateTaskAsync(
                    workspace,
                    repositorySlug,
                    pullRequestId,
                    taskId,
                    body with { Content = new CommentContentRequest { Raw = existing.Content.Raw } },
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Turns the <c>reviewers</c> argument into UUID references, or nothing at all.</summary>
    /// <remarks>
    /// <see langword="null"/> and an empty array are deliberately different on
    /// <c>updatePullRequest</c>: null omits the field (reviewers unchanged), while an explicitly
    /// empty array would clear the list. <see cref="ToolDefaults.CleanList"/> maps both an empty
    /// array and an array of blanks to null, so clearing the list is not expressible — which is the
    /// safer default for an argument a model fills in.
    /// </remarks>
    private static List<AccountRefRequest>? Accounts(string[]? uuids)
    {
        var cleaned = ToolDefaults.CleanList(uuids);

        if (cleaned is null)
        {
            return null;
        }

        var accounts = new List<AccountRefRequest>(cleaned.Count);

        foreach (var uuid in cleaned)
        {
            if (!uuid.StartsWith('{') || !uuid.EndsWith('}'))
            {
                throw new McpException(
                    $"'{uuid}' is not a Bitbucket account UUID. Reviewers are identified by UUID in braced " +
                    "form, for example {01234567-89ab-cdef-0123-456789abcdef}; names, nicknames and email " +
                    "addresses are rejected by Bitbucket. Read the UUIDs from listDefaultReviewers, or from " +
                    "getPullRequest on an existing pull request.");
            }

            accounts.Add(new AccountRefRequest { Uuid = uuid });
        }

        return accounts;
    }

    private static DiffLineType? ResolveLineType(string? lineType)
    {
        if (string.IsNullOrWhiteSpace(lineType))
        {
            return null;
        }

        return lineType.Trim().ToUpperInvariant() switch
        {
            "ADDED" => DiffLineType.Added,
            "REMOVED" => DiffLineType.Removed,
            "CONTEXT" => DiffLineType.Context,
            _ => throw new McpException(
                $"lineType must be ADDED, REMOVED or CONTEXT; got '{lineType}'. ADDED and CONTEXT are " +
                "numbered in the file after the change, REMOVED in the file before it."),
        };
    }

    private static ReviewStatus ResolveReviewStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            throw new McpException(
                "status is required: pass APPROVED, CHANGES_REQUESTED or UNAPPROVED.");
        }

        return status.Trim().ToUpperInvariant() switch
        {
            "APPROVED" => ReviewStatus.Approved,
            "CHANGES_REQUESTED" => ReviewStatus.ChangesRequested,
            "UNAPPROVED" => ReviewStatus.Unapproved,
            _ => throw new McpException(
                $"status must be APPROVED, CHANGES_REQUESTED or UNAPPROVED; got '{status}'. UNAPPROVED " +
                "withdraws both an approval and a change request."),
        };
    }

    /// <summary>
    /// Awaits a flag deletion, treating "there was no such flag" as the success it effectively is.
    /// </summary>
    /// <remarks>
    /// Bitbucket is documented to delete an approval idempotently, but in practice answers 404 (and
    /// on some paths 405) when the flag was never set. Since the caller asked for an end state —
    /// neither approved nor changes-requested — reaching it without doing anything is not a failure.
    /// Every other status still propagates.
    /// </remarks>
    private static async Task TolerateMissingFlagAsync(Task deletion)
    {
        try
        {
            await deletion.ConfigureAwait(false);
        }
        catch (BitbucketApiException exception)
            when (exception.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
        {
            // The flag was not set; the requested state is already in place.
        }
    }

    /// <summary>
    /// Reports the state Bitbucket confirmed, falling back to the state that was asked for when the
    /// endpoint answered with nothing to confirm (which is the case for the two deletions).
    /// </summary>
    private static string ReportedStatus(string? participantState, ReviewStatus requested)
    {
        if (!string.IsNullOrWhiteSpace(participantState))
        {
            var normalized = participantState.Trim().ToUpperInvariant();

            if (normalized is "APPROVED" or "CHANGES_REQUESTED")
            {
                return normalized;
            }
        }

        return requested switch
        {
            ReviewStatus.Approved => "APPROVED",
            ReviewStatus.ChangesRequested => "CHANGES_REQUESTED",
            _ => "UNAPPROVED",
        };
    }
}
