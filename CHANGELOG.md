# 1.2.1

- The plugin manifest no longer blanks an ambient `BITBUCKET_*` credential
  ([#4](https://github.com/lahma/bitbucket-mcp/issues/4)). It mapped all six credential variables
  onto their own names through `${user_config.X}` placeholders, and an option the user never filled
  in substitutes as the **empty string** rather than being omitted — so installing the plugin wrote
  `BITBUCKET_ACCESS_TOKEN=""` into the server's environment and shadowed a value the user already
  had. With three auth mechanisms in a precedence chain the failure was quiet rather than loud: it
  did not say "no credential", it demoted the caller to the next mechanism down, or off the end of the
  chain into a browser sign-in nobody asked for.
- The manifest now writes to `CLAUDE_PLUGIN_OPTION_*` — Claude Code's own convention for these
  values — and `BitbucketMcpOptions` reads the prefixed name first, treating blank as absent, then
  falls back to the plain one. A filled-in prompt still wins; a blank one changes nothing.
- `${user_config.KEY:-fallback}` is deliberately *not* the fix. The substituter's capture group is
  `[^}]+`, so the whole `key:-fallback` string is looked up as an option name, comes back undefined,
  and throws — the plugin then fails to load outright. The shell-style `${VAR:-default}` form is
  supported, but by a different expander that runs over the ambient environment rather than over
  plugin options.
- `bitbucket-mcp status` and the credential's own description now name the variable that actually
  supplied the credential, rather than the one it is usually called. Two variables can supply each
  one, and which of them won is the difference between "my token is being ignored" and "my token is
  wrong". No part of any value is printed.
- The sign-in error names the plugin case directly, because the advice it already gave — set
  `BITBUCKET_ACCESS_TOKEN` in the environment the client launches the server with — was the exact
  thing the manifest then overwrote.
- `PluginManifestTests` fails if the manifest is ever mapped back to the plain names. Reverting the
  manifest is otherwise an easy and completely invisible regression.

# 1.2.0

- Four Bitbucket Pipelines and build-diagnosis tools, taking the surface to twenty:
  `listPipelines`, `getPipeline`, `getPipelineStepLog` and `listCodeInsights`. Until now the only
  build signal was `listPullRequestStatuses`, which reports a state and a bitbucket.org URL that a
  model cannot open — "why did the build fail?" was unanswerable through this server.
- `getPipeline` returns a run *and* its steps in one call, with each step's `errorMessage` and the
  `failedStepUuid` to read next. Bitbucket's own message ("the step timed out", "the image could
  not be pulled") is frequently the entire diagnosis, so the tool puts it in front of the log
  rather than behind it.
- `getPipelineStepLog` reads the end of a step's log by default, because a step runs under `set -e`
  and the last thing printed is the thing that broke. It asks for a byte range, so the log's full
  size comes back in `Content-Range` and truncation is quantified rather than merely flagged; when
  storage ignores the range it streams into a fixed ring instead, so memory is bounded by the
  budget and never by the log. `pattern` searches the whole log for a literal substring — never a
  regular expression, which from a model over a multi-megabyte file is a denial of service. ANSI
  colour codes are stripped, and every cut is marked inside the text and reported by `truncated`.
- The step-log endpoint is the first in this server that redirects **off** `api.bitbucket.org`: it
  answers `307` to a presigned storage URL. D16's rule — re-attach the credential only for the API
  host, follow anything else anonymously — turns out to be required rather than merely prudent,
  because storage rejects a request that presents both a query signature and an `Authorization`
  header. A test now covers that hop.
- `listPipelines` accepts the state vocabulary its own results use (`SUCCESSFUL`, `FAILED`,
  `RUNNING`, …) and translates to the different vocabulary Bitbucket's `status` filter wants
  (`PASSED` for `SUCCESSFUL`, and so on). An unrecognised value is refused rather than sent:
  Bitbucket answers an unknown `status` with `200` and an empty page, which reads exactly like "this
  repository has never run a pipeline".
- Following a pipelines cursor now re-applies the `fields=` list. Unlike the pull-request and commit
  endpoints, `/pipelines` does not echo `fields=` into its own `next` link, so page two came back
  untrimmed — measured at 2,615 bytes per run against 50, a 52x cost that landed in the model's
  context and only after the first page.
- `listCodeInsights` reads a commit's Code Insights reports and the file-and-line findings behind
  the failing ones — the one build signal that points at the code rather than at a log. It needs no
  scope the pull-request tools do not already hold, which also makes it the fallback when a pipeline
  call is refused.
- Reading pipelines needs a scope existing credentials do not have (`pipeline`, or
  `read:pipeline:bitbucket` on an API token), so a 403 from a pipeline tool now says exactly that,
  in both vocabularies, and names the `bitbucket-mcp logout` + `login` step — widening an OAuth
  consumer does not widen a grant that was already cached. The pull-request 403 message is
  unchanged.
- `createPullRequest` and `updatePullRequest` can now express an **empty** reviewer list. `[]` was
  previously folded into "unspecified", so a pull request could not be opened without the
  repository's default reviewers and an existing reviewer list could not be cleared at all. Omitting
  `reviewers` still lets Bitbucket apply its own rule; `[]` now sends `"reviewers": []`. The shipped
  skill no longer reads as though fetching the default reviewers were a step in opening a pull
  request.
- `getPullRequest` and `listPullRequests` report `sourceCommit`. The field set had always requested
  it and the wire model had always deserialised it — only the result records lacked the property, so
  it was fetched and discarded. It is also the precise bridge from a pull request to its builds.
- A Bitbucket error whose body is not the documented envelope is no longer swallowed. The scopeless
  API token case answers `{"error": "API Token provided has no Bitbucket scopes."}` — a string where
  an object is documented — so parsing failed and the one sentence explaining the failure was
  dropped in favour of generic advice. The 401 message now names that case directly.
- An `HttpClient` timeout is no longer reported as a cancellation. It arrives as a
  `TaskCanceledException` with nobody's token cancelled, and was rethrown as "the caller cancelled",
  which is both wrong and unactionable; it now says what timed out and what to ask for instead.
- Bitbucket accepts an API token as `Bearer` as well as `Basic` (Atlassian shipped this on
  2026-08-18). The README and one error message still said Bearer was rejected, which told users
  they had made a mistake when they had not.
- Dependencies: `ModelContextProtocol` 2.1.0 to 2.2.0, `Microsoft.Extensions.*` and
  `System.Security.Cryptography.ProtectedData` 10.0.10 to 10.0.12, `Microsoft.NET.Test.Sdk` 18.8.1
  to 18.10.0. `xunit.v3` deliberately stays at 3.2.2 — 4.0.0 drops the VSTest bridge D11 depends on
  — and Fallout stays at 10.4.0, which is the stable channel and newer than the 11.0.x edge line
  despite sorting lower.

# 1.1.0

- `updatePullRequest` takes `closeSourceBranch` and `draft`. Both fields existed on the request
  model and were serialised correctly, but the tool bound neither, so passing one was accepted,
  answered `200 OK`, and changed nothing — and the arity guard counted only the four fields it did
  bind, so the flag on its own was refused as "nothing to update"
  ([#1](https://github.com/lahma/bitbucket-mcp/issues/1)). They are the only way to
  reach either setting once a pull request is open: `draft=false` marks a draft ready for review,
  and `closeSourceBranch=true` makes the merge delete the branch on a pull request opened without
  it. Both are nullable, so an omitted flag keeps the current value rather than forcing `false`.
- A flag-only update is one `PUT` carrying one field. Bitbucket documents the endpoint as a partial
  update but publishes no example without a `title`, so a `400` on a body that named no title — and
  only that combination — is answered by fetching the pull request and resending its own title with
  the change, the same fallback `updatePullRequestTask` already makes for a state-only update.
- `createPullRequest`'s `closeSourceBranch` and `draft` are nullable too. As plain booleans they
  defaulted to `false` and were therefore written into every create body, forcing a value where the
  caller had expressed no opinion; omitting them now leaves the field out and lets Bitbucket apply
  its own default.
- `listPullRequests` reports `closeSourceBranch` on each entry. The field set had always requested
  it and the wire model had always deserialised it — only the summary result lacked the property,
  so the value was fetched and discarded, and "which of these open pull requests will leave their
  branch behind?" cost one `getPullRequest` per entry to answer.

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
- The repository is also its own Claude Code plugin marketplace, so
  `/plugin marketplace add lahma/bitbucket-mcp` followed by `/plugin install bitbucket-mcp` wires
  up the skill **and** the server in one step: the plugin runs `dnx bitbucket-mcp@1.0.0` and prompts
  for the credentials, storing secrets in the OS keychain. The plugin's source is the repository
  root, which is what lets it point at the one canonical `SKILL.md` instead of carrying a copy, and
  the `dnx` pin, the plugin version and this changelog are asserted to be the same string.
  Users of other agents install the same file with `npx skills add lahma/bitbucket-mcp` or
  `gh skill install`.
- Native AOT single binary for win-x64, win-arm64, linux-x64, linux-arm64 and osx-arm64, published
  from a four-package runtime dependency tree.
- Also on nuget.org as the `bitbucket-mcp` .NET tool package, so `dnx bitbucket-mcp@1.0.0 --yes`
  runs the server without a download step. It is pushed by trusted publishing — a tag-triggered
  workflow exchanges its GitHub OIDC token for an API key that lives minutes — so no NuGet API key
  is stored anywhere. The Native AOT binaries remain the recommended way to run the server.
