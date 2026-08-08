# 1.0.0

Initial release.

- Ten Bitbucket Cloud pull-request tools over MCP stdio: `listPullRequests`, `getPullRequest`,
  `getPullRequestDiff`, `getPullRequestComments`, `createPullRequest`, `updatePullRequest`,
  `addPullRequestComment`, `setPullRequestReviewStatus`, `mergePullRequest`, `declinePullRequest`.
  Every tool carries explicit read-only / destructive / idempotent annotations and returns
  structured content.
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
- Errors are translated into instructions: missing scopes, the scoped-token 403 workaround, merge
  conflicts, rate limits and diff-too-large all name the next call to make.
- Native AOT single binary for win-x64, win-arm64, linux-x64, linux-arm64 and osx-arm64, published
  from a four-package runtime dependency tree.
