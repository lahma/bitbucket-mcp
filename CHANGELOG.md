# 1.0.0

Initial release.

- Sixteen Bitbucket Cloud pull-request tools over MCP stdio: `listPullRequests`, `getPullRequest`,
  `getPullRequestDiff`, `getPullRequestComments`, `listDefaultReviewers`,
  `listPullRequestStatuses`, `listPullRequestTasks`, `createPullRequest`, `updatePullRequest`,
  `addPullRequestComment`, `resolvePullRequestComment`, `addPullRequestTask`,
  `updatePullRequestTask`, `setPullRequestReviewStatus`, `mergePullRequest`, `declinePullRequest`.
  Every tool carries explicit read-only / destructive / idempotent annotations and returns
  structured content.
- `listDefaultReviewers` reads the repository's effective default reviewers — its own plus the ones
  inherited from its project — so a reviewer's account UUID is obtainable on a repository that has
  never had a pull request. `createPullRequest` and `updatePullRequest` point at it.
- `listPullRequestStatuses` reports every build, deployment and external check with its state and
  URL: the merge-readiness question, answerable before `mergePullRequest` rather than after.
- Pull request tasks, the tracked half of a review: `listPullRequestTasks`, `addPullRequestTask`
  (optionally hung off a comment) and `updatePullRequestTask` to tick one off or reopen it. A
  state-only update is one request; if Bitbucket rejects it for a missing field, the task's own
  text is fetched and resent with the new state rather than the call failing.
- `resolvePullRequestComment` marks an inline comment thread resolved or reopens it, and is
  genuinely idempotent: Bitbucket's `409` for an already-resolved thread and `404` for reopening an
  open one are both the requested end state, and are treated as such.
- Every pull request and comment result now carries `url`, the bitbucket.org page — the one link a
  model cannot derive and the one a human asks for.
- `listPullRequests` takes `sourceBranch`, composing a `source.branch.name` filter alongside the
  state and author ones. A branch name containing a double quote or backslash is refused rather
  than escaped, because Bitbucket's query language documents no escape sequence at all.
- `getPullRequestDiff` reads `paths` as the request for a diff: supplying it selects `mode="diff"`
  instead of being ignored in the default listing mode, and `mode="diffstat"` alongside it is
  refused as the contradiction it is.
- OAuth 2.0 browser flow as the primary authentication, with a `login` / `logout` / `status` CLI on
  the same binary. Tokens are cached per user, DPAPI-encrypted on Windows and `0600` elsewhere, and
  refreshed silently — including Bitbucket's single-use refresh-token rotation, serialised across
  processes.
- Environment-token fallback for headless and CI use: `BITBUCKET_ACCESS_TOKEN` (Bearer) or
  `BITBUCKET_EMAIL` + `BITBUCKET_API_TOKEN` (Basic). App passwords, removed by Atlassian on
  2026-07-28, are deliberately not implemented.
- Diffstat-first diff handling: `getPullRequestDiff` defaults to listing changed files and fetches
  content only for named paths, so large pull requests do not hit Bitbucket's 555. Truncation is
  always marked inline and reported with continuation guidance.
- Inline comments anchored by `codeSnippet` — the line's text copied out of the diff — resolved
  against the diff, with ambiguous or missing matches reported instead of guessed.
- Errors are translated into instructions: missing scopes, an API token sent as Bearer instead of
  Basic, merge conflicts, rate limits and diff-too-large all name the next call to make.
- An Agent Skill ships with the repository at
  `.claude/skills/bitbucket-pull-requests/SKILL.md`, in the open `SKILL.md` format: the review,
  create and merge playbooks — the call *order* no single tool schema can describe — plus the
  recovery moves and when to ask local git instead. Claude Code loads it as a project skill from a
  checkout, Cursor and VS Code read the same path, and other tools are pointed at the one canonical
  copy rather than given a second. `AgentSkillTests` cross-checks every tool it names against the
  reflected inventory in both directions, so it can neither invent a tool nor silently omit one.
- Native AOT single binary for win-x64, win-arm64, linux-x64, linux-arm64 and osx-arm64, published
  from a four-package runtime dependency tree.
- Also on nuget.org as the `bitbucket-mcp` .NET tool package, so `dnx bitbucket-mcp@1.0.0 --yes`
  runs the server without a download step. It is pushed by trusted publishing — a tag-triggered
  workflow exchanges its GitHub OIDC token for an API key that lives minutes — so no NuGet API key
  is stored anywhere. The Native AOT binaries remain the recommended way to run the server.
