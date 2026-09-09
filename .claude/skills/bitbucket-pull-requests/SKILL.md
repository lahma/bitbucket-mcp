---
name: bitbucket-pull-requests
description: >-
  Review, create and merge Bitbucket Cloud pull requests, and diagnose failed Bitbucket Pipelines
  builds, through the bitbucket-mcp MCP server. Use when a task touches a Bitbucket pull request —
  reading a diff, leaving inline comments or tasks, approving or requesting changes, opening a pull
  request from a branch, merging or declining one — or when a Bitbucket build or pipeline is
  failing and the question is why, and the bitbucket-mcp tools (`listPullRequests`,
  `getPullRequestDiff`, `addPullRequestComment`, `mergePullRequest`, `listPipelines`,
  `getPipelineStepLog` and the rest) are attached. Covers the order the calls go in: diffstat
  before diff content, snippet-anchored inline comments, build statuses before a merge decision,
  the failing step before its log, and when to ask local git instead of spending an API call.
license: MIT
compatibility: Requires the bitbucket-mcp MCP server, signed in and attached to the client. Bitbucket Cloud only.
---

# Bitbucket pull requests

The server's `initialize` instructions already state the conventions — slugs, opaque cursors,
snippet anchors, reviewer UUIDs — and every tool's schema carries its own rules. This file holds
what neither can: the order the calls go in, and what to do when one of them fails.

## Review a pull request

1. `listPullRequests` — find it. Open ones only unless `state` says otherwise, most recently
   updated first. `sourceBranch` answers "does this branch already have one?".
2. `getPullRequest` — description, reviewers, their approvals, and `url`. Read it before writing
   anything to it.
3. `getPullRequestDiff` with no `paths` — the changed-file list. **Always this first.** A
   whole-pull-request diff is what Bitbucket answers 555 to on a large one, and what burns the
   context window when it succeeds.
4. `getPullRequestDiff` with `paths=[...]` — only the files worth reading, spelled exactly as the
   file list spelled them. `paths` on its own selects diff mode; naming `mode="diffstat"` beside it
   is refused rather than ignored. Read `truncated` on the way out: a truncated diff is not the
   change.
5. `getPullRequestComments` — what has already been said, before saying it again. A comment's `id`
   is the `parentCommentId` that makes the next one a reply.
6. `addPullRequestComment` with `path` and `codeSnippet` — the line's text copied verbatim out of
   the diff. The snippet beats a line number because the server resolves it against that file's
   diff and reports ambiguity with the candidate lines, instead of commenting on the wrong line.
   `line` plus `lineType` is the fallback for a line whose text repeats.
7. `addPullRequestTask` for anything that has to actually be done, with `commentId` to hang it off
   the remark rather than restating it. A comment can be read and forgotten; Bitbucket counts
   tasks, and a repository can require them resolved before it merges. Check `listPullRequestTasks`
   first — calling twice makes two tasks.
8. `setPullRequestReviewStatus` — `APPROVED`, `CHANGES_REQUESTED`, or `UNAPPROVED` to withdraw
   both. It moves your own stance and nobody else's; `comment` posts the reasoning in the same
   call.

Coming back to a review you already left:

- `updatePullRequestTask` with `state="RESOLVED"` ticks a task off. `resolvePullRequestComment`
  marks the thread resolved. Do both — they are separate counters, and a repository can gate on
  either.
- `getPullRequestDiff` again with the same `paths` to see what the author actually changed.

## Merge or decline

- `listPullRequestStatuses` **before** the decision. Bitbucket merges over a failing build when the
  repository does not require it, and an empty list means nothing has reported yet — which is not
  the same as passing.
- `getPullRequest` to confirm it is the pull request you think it is, and that it is approved.
- `mergePullRequest` — immediate, and not undoable from here. Omit `mergeStrategy` to take the
  repository's default; a strategy it has disabled is rejected, not substituted, and a conflict is
  a 409 rather than a half-merge.
- `declinePullRequest` closes without merging and cannot be reopened through this server. When the
  author is expected to keep working, `setPullRequestReviewStatus` with `CHANGES_REQUESTED` is the
  tool instead.

## Create a pull request

1. `listPullRequests` with `sourceBranch` and `state="ALL"` — the deduplication check.
   `createPullRequest` is not idempotent: called twice it opens two pull requests.
2. Reviewers — **only if the user asked for them.** Opening a pull request with no reviewers is
   the normal call, and `createPullRequest` leaves the field out when `reviewers` is unset. Where
   the repository or its project carries a default-reviewer rule and the user does not want it,
   pass `reviewers: []`, which is the one way to say "nobody". When reviewers *are* wanted,
   `listDefaultReviewers` is where their UUIDs come from — braced account UUIDs and nothing else,
   never a display name turned into one by guessing. Listing them is not a reason to add them.
3. `createPullRequest` — `title` and `sourceBranch` are the only required arguments, and omitting
   `destinationBranch` targets the main branch. The result carries `url`: the link to hand a human,
   and the one value that cannot be derived.
4. `updatePullRequest` to amend it afterwards. `reviewers` REPLACES the list, so send the existing
   ones too — or `[]` to remove everyone — and the call overwrites anything edited in the browser
   meanwhile, so read first. It is
   also the only way to reach `closeSourceBranch` and `draft` once the pull request exists:
   `draft=false` marks a draft ready for review, and `closeSourceBranch=true` makes the merge delete
   the branch. An omitted flag keeps its current value, so either one is a complete update on its
   own — no need to resend the title to make the call legal.

## Diagnose a failed build

The order matters more here than anywhere else, because the last step is the expensive one and is
usually unnecessary.

1. `listPullRequestStatuses`, if the starting point is a pull request — it says *whether* CI is red
   without spending a pipeline call.
2. `listPipelines` to find the run. `targetBranch` is the pull request's `sourceBranch`; `commit`
   is its `sourceCommit`, which is more precise when the branch has moved on. `status` takes the
   same words results report (`FAILED`, `SUCCESSFUL`, `RUNNING`, …), and an unrecognised one is
   refused rather than silently matching nothing. An empty list means the repository has never run
   a pipeline — not that the build failed.
3. `getPipeline` on that run. It returns the steps and `failedStepUuid` in one call. **Read the
   failing step's `errorMessage` before going further**: "the step timed out", "the image could not
   be pulled" and their kind are the whole answer, and reading a log to rediscover them wastes a
   large fraction of the context budget.
4. `listCodeInsights` with the run's `commitHash` — often faster than any log, because a linter,
   scanner or test reporter names the file, the line and the message directly. It also needs no
   scope the pull-request tools do not already have, so it works when step 5 answers 403.
5. `getPipelineStepLog` last, with `stepUuid` set to `failedStepUuid`. It returns the **end** of
   the log by default, which is where a step running under `set -e` reports what broke. When the
   failure is not at the end, pass `pattern` — a literal, case-insensitive substring, not a regular
   expression — to search the whole log instead; supplying it selects search mode on its own.
   `mode="head"` is only for a container that died before producing output of its own.

Never ask for a whole log. The response is always capped at `maxLines`, and a cut is marked inside
the text and flagged by `truncated` — a truncated log must never be reported as the whole log. If
the answer is not in the tail, search for it; do not raise `maxLines` repeatedly.

## Discipline

- **Cursors are opaque.** Pass a result's `nextCursor` back as `cursor`, byte for byte; never edit,
  decode or invent one. Every filter is already encoded inside it.
- **`workspace` and `repository` are URL slugs** — the two segments of
  `bitbucket.org/{workspace}/{repository}`, not display names. A 404 on a repository you can see in
  a browser is usually this.
- **Page deliberately.** `pageSize` is clamped to 1–50. Fetch a second page because the first one
  ran out, not to be thorough.
- **Do not spend a call on what local git already answers.** Branch names, commit messages, what a
  branch changed, whether a file exists: `git` is free and instant, and every tool here is a
  network round trip against an hourly rate limit. Use the server for what lives on bitbucket.org —
  pull request state, reviewers, comments, tasks, build statuses.
- **Writes land on the real repository immediately.** There is no dry run and no staging step.

## When a call fails

- **555 on a diff.** The pull request is too large to diff whole (roughly 8,000 lines or 200
  files), and retrying never helps. Go back to `getPullRequestDiff` with no `paths`, then name
  files.
- **An ambiguous or unmatched `codeSnippet`.** The error lists the candidate lines: copy more of
  the line and repeat, or fall back to `line` plus `lineType`. Do not guess a line number.
- **403 on a write.** Scopes, Basic-versus-Bearer, or the account's own repository access — see
  *Troubleshooting* in the server's README.
- **429.** The client already retried with backoff, so one that reaches you means slow down:
  smaller pages, per-file diffs.
- **403 on a pipeline tool.** The credential predates these tools rather than lacking repository
  access: reading Pipelines needs the `pipeline` OAuth scope, or `read:pipeline:bitbucket` on an
  API token. Widening an OAuth consumer does not widen a grant that was already cached, so it takes
  `bitbucket-mcp logout` then `bitbucket-mcp login`; an API token's scopes cannot be edited at all,
  so it takes a new token. `listCodeInsights` needs neither and often answers the question anyway.
- **404 from `getPipelineStepLog`.** The step has produced no log yet, or never started. Check the
  step's state with `getPipeline` rather than retrying.
- **"Not signed in".** `bitbucket-mcp login`, or the token environment variables — see
  *Authentication* in the server's README.
