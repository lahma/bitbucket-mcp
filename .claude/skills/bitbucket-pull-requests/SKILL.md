---
name: bitbucket-pull-requests
description: >-
  Review, create and merge Bitbucket Cloud pull requests through the bitbucket-mcp MCP server.
  Use when a task touches a Bitbucket pull request — reading a diff, leaving inline comments or
  tasks, approving or requesting changes, opening a pull request from a branch, merging or
  declining one — and the bitbucket-mcp tools (`listPullRequests`, `getPullRequestDiff`,
  `addPullRequestComment`, `mergePullRequest` and the rest) are attached. Covers the order the
  calls go in: diffstat before diff content, snippet-anchored inline comments, build statuses
  before a merge decision, and when to ask local git instead of spending an API call.
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
2. `listDefaultReviewers` — reviewers are Bitbucket account UUIDs in braced form and nothing else.
   Read them from here, or from `getPullRequest` on an existing pull request. Never turn a display
   name into a UUID by guessing.
3. `createPullRequest` — `title` and `sourceBranch` are the only required arguments, and omitting
   `destinationBranch` targets the main branch. The result carries `url`: the link to hand a human,
   and the one value that cannot be derived.
4. `updatePullRequest` to amend it afterwards. `reviewers` REPLACES the list, so send the existing
   ones too, and the call overwrites anything edited in the browser meanwhile — read first.

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
- **"Not signed in".** `bitbucket-mcp login`, or the token environment variables — see
  *Authentication* in the server's README.
