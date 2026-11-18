# AGENTS.md

Guidance for humans and AI agents working in this repository. `.claude/CLAUDE.md` imports this file,
so keep it the single source of truth.

`bitbucket-mcp` is a Model Context Protocol server for **Bitbucket Cloud** (REST API 2.0),
written in C# on .NET 10 and published as a Native AOT single binary per RID. Bitbucket Data
Center is explicitly out of scope. The point of the project is a dependency tree small enough
for one person to audit — treat that as a hard constraint, not a preference.

## Hard rules

1. **No new NuGet packages without a decision recorded in this file.** The package budget below
   is complete. If a package looks necessary, add a row to *Package budget changes* with the
   date, the package, and why nothing already present can do the job — before referencing it.
2. **Never hand-edit `.github/workflows/build.yml` or `.github/workflows/publish.yml`.** Both are
   generated from the two `[GitHubActions]` attributes in `build/Build.CI.GitHubActions.cs` and
   regenerating overwrites edits. Change the attribute and re-run the build (any run regenerates;
   `dotnet fallout --generate-configuration GitHubActions_publish --host GitHubActions` does just
   the one). A run whose generated output differs *aborts* with `Configuration files for
   GitHubActions (…) have changed` — run it again and commit the regenerated YAML.
   (`.github/workflows/release.yml` is hand-written by design — the generator has no matrix
   support — and *is* edited directly.)
3. **No `Console.Write*` outside `src/Bitbucket.Mcp/Cli/`.** In server mode stdout *is* the MCP
   protocol channel; a stray write corrupts the JSON-RPC stream. Logging goes to stderr
   (`AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace)`). Only the CLI modes
   (`login` / `logout` / `status`), which never speak the protocol, may write to stdout. A test
   enforces this.
4. **Diffstat first.** Never fetch a whole PR diff speculatively: call `getPullRequestDiff` with no
   `paths` to list the changed files, then again with `paths=[...]` for the ones worth reading —
   supplying `paths` is itself what selects the diff, so `mode` never has to be named, and naming
   `mode="diffstat"` alongside `paths` is refused rather than silently ignored. Bitbucket returns
   **555** on large diffs (~8k lines / 200 files) and a full diff burns the model's context either
   way. Truncation must always be visible, with continuation guidance — never silent.
5. **`.gitignore` must never contain a bare `build` rule.** The Fallout build project lives in
   `build/`; a stock Visual Studio .gitignore silently untracks the entire orchestrator.
6. LF line endings everywhere (`.gitattributes` enforces it). `TreatWarningsAsErrors` is on —
   the compiler and analyzers are the lint step.

## Design decisions (D1–D24)

| # | Decision |
|---|---|
| D1 | **One production project** `src/Bitbucket.Mcp` (Exe) + one test project. Testability via `InternalsVisibleTo`. |
| D2 | Binary `bitbucket-mcp` (`AssemblyName`), `RootNamespace Bitbucket.Mcp`. |
| D3 | **Bare `ServiceCollection`**, not `Host.CreateApplicationBuilder` — the SDK registers `McpServer` as a singleton; `provider.GetRequiredService<McpServer>().RunAsync()` (this is the SDK's own AOT test app shape). Drops config providers/metrics/lifetime from cold start. `PosixSignalRegistration` for SIGINT/SIGTERM. |
| D4 | **Hand-rolled retry `DelegatingHandler`** (~190 lines of code) — `Microsoft.Extensions.Http.Resilience` would pull in Polly (third-party) plus six more packages. |
| D5 | No `IHttpClientFactory` — one singleton `HttpClient` over a hand-built handler chain. |
| D6 | Our `JsonSerializerContext` goes **first** in `TypeInfoResolverChain`, the SDK resolver second (first-match-wins; ours-first guarantees identical JIT and AOT behavior). |
| D7 | `JsonSerializerIsReflectionEnabledByDefault=false` in the server **and** the test csproj — a missing `[JsonSerializable]` then fails in `dotnet test`, not only after an AOT publish. |
| D8 | **Static tool methods**; `BitbucketApiClient` as a plain parameter (DI-bound via `IServiceProviderIsService`, excluded from the schema); avoids per-call instance activation and is directly unit-testable. |
| D9 | No `RuntimeIdentifiers` in the csproj (would pull ILCompiler packs per RID on every restore); the RID list lives in `Build.cs` and the release matrix, and reaches publish via `-r`. |
| D10 | No `PublishSingleFile` (ignored under AOT). |
| D11 | **NUnit 5 on Microsoft.Testing.Platform (MTP), run by a hand-written `Test` target.** `global.json` selects MTP for `dotnet test`, and `NUnit3TestAdapter` is the runner through `EnableNUnitRunner`. MTP's `dotnet test` is a different command line from VSTest's — `--project` instead of a positional path, no `--logger`, no TRX by default — and Fallout's `ITest` generates only the VSTest one (Fallout-build/Fallout#674, open), so `Build.cs` owns `Test` instead of taking it from Fallout.Components. On GitHub Actions it adds `--report-github` (`GitHubActionsTestLogger`), which annotates each failing test at its file and line and writes a job summary of the failures — the reporting the VSTest bridge was kept for, and which this repository never actually had, since nothing referenced the logger. Two assembly attributes in `TestAssembly.cs` keep the contract the suite was written for under xunit: a fresh fixture instance per test case, and fixtures in parallel. The test project runs with `InvariantGlobalization` like the server, which also makes NUnit's string constraints — culture-sensitive by default — ordinal. Revisit the hand-written target when Fallout speaks MTP. |
| D12 | OAuth loopback callback via a raw **`TcpListener`** bound to both `127.0.0.1` and `::1` (~60 auditable lines, no `HttpListener` platform quirks). |
| D13 | **Confidential OAuth client** (key + secret, Basic auth at the token endpoint), no PKCE (unconfirmed for Bitbucket Cloud; leave an internal hook). The user creates their own consumer. |
| D14 | Access-token lifetime always taken from `expires_in` (minus 60 s skew) — never hard-coded; Atlassian's docs contradict themselves (1 h vs 2 h). |
| D15 | **`login` / `logout` / `status` CLI modes** on the same binary (argv dispatch, hand-rolled parsing); no args = stdio server. CLI mode may use stdout; server mode never. |
| D16 | **Redirects are followed by hand, not by the transport.** `SocketsHttpHandler` strips the `Authorization` header on **all** automatic redirects, same-origin included, and does not re-apply a default header to the redirected request either (verified 2026-08-09 against live Bitbucket and a local echo server — the diff and diffstat endpoints `302` to another path on `api.bitbucket.org` whose target still needs the credential, so the redirected request came back `404`). The transport therefore sets `AllowAutoRedirect=false` and `AuthenticationHandler` follows redirects itself: `GET`/`HEAD` only (never replay a request with content), 301/302/303/307/308, at most 5 hops, relative `Location` resolved against the current URI, and the credential re-attached **only** when the target is `https` on `api.bitbucket.org` — a redirect anywhere else is followed anonymously. |
| D17 | **NuGet is a second distribution channel, not the primary one.** `dotnet pack` produces `bitbucket-mcp`, a framework-dependent .NET tool package (`PackAsTool`) carrying `PackageType=McpServer` and an embedded `.mcp/server.json`, so `dnx bitbucket-mcp@{version}` works without a download step; the Native AOT binaries stay the recommended way to run the server. It is pushed by **trusted publishing** — the workflow exchanges its GitHub OIDC token for an API key that lives minutes — so no NuGet API key exists in the repository or in GitHub secrets. The exchange is **C# inside the build** (`build/Build.Publish.cs`), not the `NuGet/login` marketplace action: it is auditable alongside everything else and adds no third-party action to the release path. It has its **own generated workflow file**, `.github/workflows/publish.yml`, triggered by `v*.*.*` tags only, because a nuget.org policy is scoped by workflow *file name* and the nuget.org UI has no branch or tag filter — a one-job workflow that only a tag can start is the smallest thing that can hold the capability, and `build.yml` cannot mint a key because it is not the file named in the policy. GitHub environment `nuget` (deployment branch policy: tags `v*`) adds a second claim the policy matches on. |
| D18 | **A state-only `updatePullRequest` retries once with the pull request's own title.** Bitbucket documents `PUT /pullrequests/{id}` as a partial update but publishes no example without a `title`, and answers `400` to some bodies that name none. `SendUpdateAsync` catches exactly that combination — a 400 on a body with no title — fetches the pull request and resends with its existing title. `UpdateTaskAsync` does the same for a state-only task update. The cost is a last-write-wins window between the read and the resend, accepted because the alternative is a call that simply fails. |
| D19 | **Pipeline logs are read tail-first, over a byte `Range`, and reduced while streaming.** A step runs under `set -e`, so the failure is the last thing printed; the tool asks for a suffix range and gets the log's full size back in `Content-Range` for free, which is what makes truncation quantified rather than merely flagged. Storage that ignores the header is handled by streaming into a fixed byte ring instead, so memory is bounded by the budget and never by the log; a `416` retries once unranged onto that same path. `HEAD` is never used — the presigned URL is signed for `GET` and answers 403. `pattern` is a literal substring, never a regular expression: the argument comes from a model, and backtracking over a multi-megabyte log is a denial of service against the server that fetched it. Verified against live storage 2026-09-09, including that ranged responses are not compressed. |
| D20 | **The pipeline surface is read-only, and 403s are scope-aware per tool.** Running or stopping a pipeline needs `pipeline:write` on top of the read scope and spends build minutes; the problem being solved is diagnosis. Reading pipelines needs `pipeline` / `read:pipeline:bitbucket`, which **no existing user's credential has**, so `ToolCallContext` carries a `ToolScope` and `ToolErrors.Forbidden` branches on it: the pipeline message names that scope in both vocabularies, names the `logout`/`login` step (widening a consumer does not widen a cached grant), and offers `listCodeInsights` as a fallback. The pull-request branch is byte-identical to what it was, so nobody else's 403 grew a line. |
| D21 | **An empty `reviewers` array means an empty reviewer list.** `null` and `[]` are different: omitting the field lets Bitbucket apply the repository's default-reviewer rule, while `[]` sends `"reviewers": []` and is the only way to say "nobody". This reverses the original choice — `CleanList` folded both to null, so clearing was inexpressible — because opening pull requests without default reviewers is a normal thing to want, and on `updatePullRequest` there was otherwise no way to remove a reviewer at all. `CleanListPreservingEmpty` keeps the two apart; `CleanList` is unchanged and still serves `paths`. | |
| D22 | **The plugin manifest writes to `CLAUDE_PLUGIN_OPTION_*`, never to the plain variable names.** A Claude Code manifest maps every declared option into the server's environment through a `${user_config.X}` placeholder, and an option the user never filled in substitutes as the **empty string** rather than being omitted — so mapping it onto `BITBUCKET_ACCESS_TOKEN` set that variable to `""` in the child process and shadowed a value the user already had ([#4](https://github.com/lahma/bitbucket-mcp/issues/4)). With three auth mechanisms in a precedence chain the failure was silent: it demoted the caller to the next mechanism, or off the end of the chain into a browser sign-in nobody asked for. `BitbucketMcpOptions.ReadConfigured` therefore reads the prefixed name first, blank treated as absent, falling back to the plain one; a filled prompt still wins and a blank one changes nothing. `${user_config.KEY:-fallback}` is **not** an alternative — the substituter's capture group is `[^}]+`, so the whole `key:-fallback` string is looked up as an option name, is undefined, and throws, and the plugin fails to load. `PluginManifestTests` fails if the manifest is ever mapped back.
| D23 | **The target framework stays `net10.0` (LTS) through .NET 11.** Measured 2026-11-18 on SDK 10.0.404 and 11.0.100: `net11.0` grows the win-x64 AOT binary 2% (all of it runtime), leaves startup unchanged, and ends support the same month (2028-11). Its one gain — the server's restore graph falling from 21 packages to 12, because .NET 11 moved `Microsoft.Extensions.*.Abstractions`, `Options` and `Primitives` into the shared framework — exists only with `net11.0` *alone*, and `net11.0` alone breaks `dnx` (and with it the Claude Code plugin) for anyone without the .NET 11 runtime; carrying `net10.0` as well keeps the old graph. It needs no `NuGet.Frameworks` pin in `build/`: that pin was load-bearing only while Fallout's `ITest` evaluated test projects in-process, and the hand-written `Test` target (D11) does not — SDK 11.0.100 with `net11.0` runs the simulated CI `Test` green without it (2026-11-18). Revisit at .NET 12 LTS, or when RID-specific AOT tool packages decouple `dnx` from the installed runtime. |
| D24 | **An argument the schema does not name is refused, not dropped.** The SDK binds arguments by exact name and ignores the rest, so a mis-cased required argument failed with a bare "An error occurred invoking", and a mis-cased optional one vanished — `Workspace` for `workspace` silently ran the call against `BITBUCKET_DEFAULT_WORKSPACE`, which for a write tool is a change in the wrong place. A call-tool filter registered by `McpServerSetup.WithBitbucketTools` checks the names in `arguments` against the tool's input schema before the tool runs (`ToolArguments`) and answers with a tool error that names each unknown argument and, when only the case differs, the spelling meant. It is a tool error, not a protocol error, because the model is what has to correct the call. Only `arguments` is checked: `_meta` and anything else a client adds travel beside it in `params`. `ToolArgumentTests` asserts every tool accepts all of its own schema's names, and drives the filter through a real `McpClient`. |

### Risks carried (R3–R5)

Four source files and one test cite these by number; they were never written down, so here they
are. (R1 and R2 are release-path risks and live under *Release engineering*.)

- **R3 — the OAuth callback host.** Bitbucket compares the redirect URI as a string, and a consumer
  may have `localhost` registered where the server sends `127.0.0.1`, or the name may resolve to
  IPv6 first. Mitigated by binding the loopback listener to **both** `127.0.0.1` and `::1` (D12) and
  by making the host configurable (`BITBUCKET_OAUTH_CALLBACK_HOST`). Cited in
  `LoopbackCallbackListener.cs`, `BitbucketMcpOptions.cs` and `LoopbackCallbackListenerTests.cs`.
- **R4 — reporting the outcome of an interactive sign-in.** A browser flow can fail in ways a tool
  call has no vocabulary for (the user closed the tab, denied consent, or never returned).
  `InteractiveAuthorizationResult` carries the outcome back explicitly instead of collapsing it into
  a null or a timeout. Cited in `InteractiveAuthenticator.cs`.
- **R5 — an inclusive `fields=` list that omits `next` silently stops pagination.** Bitbucket
  returns page one with no continuation link, so the caller sees a complete-looking result and never
  learns there was more — and only against a repository large enough to have a second page.
  Enforced by `FieldSetTests.EveryPaginatedFieldSetRequestsNext`, and explained in full in
  `FieldSets.cs`. Cited in `FieldSetTests.cs`.

Other locked choices worth restating: Bitbucket **app passwords are dead** (removed 2026-07-28) —
never implement them. Tool names are **camelCase verbNoun** (`createPullRequest`) via
`[McpServerTool(Name = ...)]`. `Destructive` defaults to **true** in the SDK, so non-destructive
writes must set it explicitly `false`. Never call `WithToolsFromAssembly()` (IL2026) — one
`WithTools<T>(jsonOptions)` per tool class.

## Tool table

The design's tool inventory, in full. `ToolInventoryTests` asserts this set of names and every one
of these four flags against what an MCP client actually receives, and `Build.cs`'s
`ExpectedToolNames` asserts the names again over a real `tools/list`, and `AgentSkillTests` asserts
that the shipped skill names it too. **Adding a tool means editing six places**, and only the first
four of them fail the build:

1. this table;
2. `ToolInventoryTests` — `ExpectedToolNames` (ordinal order), the hard-coded tool and class counts,
   and the title and annotation test cases;
3. `Build.cs`'s `ExpectedToolNames`, which `SmokeTest` checks against a live `tools/list`;
4. the shipped skill;
5. `ToolSchemaTests`'s per-tool `[TestCase]` row — a second inventory, of exact parameter names;
6. `README.md`'s tool table **and** its tool count in the opening paragraph, which nothing enforces
   at all and which is therefore the one that actually drifts.

`Destructive` is blank on the read tools deliberately: `ReadOnly` already says they change nothing,
so the SDK omits the hint and the test asserts its *absence*. On a write tool it is never blank —
the SDK's default is `true`, so a non-destructive write that stays silent tells clients to prompt
before every comment.

| Tool | Class | ReadOnly | Destructive | Idempotent | OpenWorld |
|---|---|---|---|---|---|
| `listPullRequests` | pr read | true | — | true | true |
| `getPullRequest` | pr read | true | — | true | true |
| `getPullRequestDiff` | pr read | true | — | true | true |
| `getPullRequestComments` | pr read | true | — | true | true |
| `listDefaultReviewers` | pr read | true | — | true | true |
| `listPullRequestStatuses` | pr read | true | — | true | true |
| `listPullRequestTasks` | pr read | true | — | true | true |
| `getPullRequestMergeability` | pr read | true | — | true | true |
| `createPullRequest` | pr write | false | **false** | false | true |
| `updatePullRequest` | pr write | false | true | false | true |
| `addPullRequestComment` | pr write | false | **false** | false | true |
| `updatePullRequestComment` | pr write | false | true | true | true |
| `resolvePullRequestComment` | pr write | false | **false** | true | true |
| `addPullRequestTask` | pr write | false | **false** | false | true |
| `updatePullRequestTask` | pr write | false | **false** | true | true |
| `setPullRequestReviewStatus` | pr write | false | **false** | true | true |
| `mergePullRequest` | pr write | false | true | false | true |
| `declinePullRequest` | pr write | false | true | false | true |
| `listPipelines` | pipeline read | true | — | true | true |
| `getPipeline` | pipeline read | true | — | true | true |
| `getPipelineStepLog` | pipeline read | true | — | true | true |
| `listCodeInsights` | pipeline read | true | — | true | true |

Three of those rows are judgement calls rather than readings of the API:

- **`updatePullRequestTask` is non-destructive** even though its `content` argument overwrites a
  task's text irrecoverably — which is exactly why `updatePullRequest` is destructive. The common
  call by a wide margin is the state flip (ticking a task off), and a confirmation prompt in front
  of every tick teaches a user to click through the prompts that matter. The description says the
  text is replaced.
- **`listCodeInsights` sits in the pipeline class** although its endpoint is a commit one, not a
  Pipelines one. It answers the same question — why is the build red — and it is the only tool in
  that class that needs no scope the pull-request tools already hold, which makes it the documented
  fallback when a pipeline call answers 403. A fourth tool class holding one tool would have bought
  nothing but another `WithTools<T>` line.
- **`resolvePullRequestComment` is idempotent**, which it only is because the tool makes it so:
  Bitbucket answers `409` to resolving an already-resolved thread and `404` to reopening one that
  was never resolved, and both are swallowed because the caller asked for an end state and it is
  already in place. The same shape as `setPullRequestReviewStatus`'s `UNAPPROVED`.

## Agent skill

`.claude/skills/bitbucket-pull-requests/SKILL.md` is an [Agent Skill](https://agentskills.io)
shipped with the repository. It is the **only** copy — there is no second one under `.agents/`, and
none inside a plugin directory either. Everything that installs it installs *that* file:

- **Claude Code, in a checkout**: loaded as a project skill, because `.claude/skills/` is where
  project skills live. Nothing to install.
- **Claude Code, anywhere else**: the repository is its own plugin marketplace (below), and the
  plugin's `skills` path points back at the same file.
- **Every other tool**: `npx skills add lahma/bitbucket-mcp` and `gh skill install` both scan
  `.claude/skills/` in the source repository and write to whatever location the target agent uses,
  so no second location is needed here for Codex, Gemini CLI, Cursor or Copilot to install it.

### The plugin and its marketplace

`.claude-plugin/marketplace.json` lists exactly one plugin whose `source` is `"./"` — the
repository root. That is the load-bearing choice: **an installed plugin cannot reference files
outside its own root**, so any narrower source would have forced a second copy of `SKILL.md` or a
symlink (which a Windows checkout without `core.symlinks` silently turns into a text file). With
the root as the source, `.claude-plugin/plugin.json` names `./.claude/skills/bitbucket-pull-requests`
directly. The cost is that installing copies the tracked tree — about 1.2 MB — into the plugin
cache; that is the price of one canonical file, and it was judged worth paying.

The same manifest bundles the MCP server (`dnx bitbucket-mcp@{version} --yes`), so one
`/plugin install` delivers the skill and the server together. Two things about it are deliberate:

- **The `dnx` pin is explicit, never floating.** Claude Code keys plugin updates off the plugin
  `version`; a floating package id would let the server change under a user whose plugin version
  never moved, pairing one release's skill with another release's tool surface.
  `PluginManifestTests` asserts the pin, the plugin `version` and `CHANGELOG.md` are the same
  string, so releasing means bumping the changelog and nothing else.
- **The credential surface is derived, not restated.** `userConfig` declares one prompt per
  environment variable in `.mcp/server.json`, and the `env` block wires each one through
  `${user_config.…}`. The test asserts the two agree, including that a variable marked `isSecret`
  is marked `sensitive` — an unmarked secret is written to `settings.json` in the clear, and a
  mistyped placeholder is passed to the server as a literal credential rather than failing.

Validate both manifests with the official CLI, which is non-interactive:

```powershell
claude plugin validate . --strict          # the marketplace manifest; must end "Validation passed"
claude plugin marketplace add .            # resolves it; remove afterwards
```

`--strict` turns every plugin warning into a failure, so it must stay warning-free. Two things keep
it that way:

- **No `CLAUDE.md` at the repository root.** The root is the plugin's root, where Claude Code does
  not load one as context, and the validator warns about it. The import that gives Claude Code
  contributors this file lives in `.claude/CLAUDE.md` (`@../AGENTS.md`, relative to the importing
  file), which Claude Code reads as project instructions just the same.
- **One expected line that is not a warning: `ICON_MISSING`.** It is a *plugin directory* check,
  and the validator says itself that those "do not change the result". No icon ships yet, on
  purpose: the first icon a plugin is submitted to the directory with becomes its listing icon for
  good, so it is the owner's choice. When one is chosen: a square PNG of 512–2048 px, under 2 MB, at
  `.claude-plugin/icon.png`.

It holds what neither `ServerInstructions` nor a tool `[Description]` can, because both are scoped
to one call: the **order** the calls go in (diffstat before diff content, statuses before a merge,
the deduplication check before a create), the recovery moves (555, an ambiguous `codeSnippet`, 403,
429), and when *not* to call the server at all because local git already knows. It must not restate
the schema or the README — the schema arrives with every session anyway, and the README is for
humans.

The division of budget is the point. `ServerInstructions` is paid by every session that attaches
the server, which is why it stays at about ten lines; the skill's body is paid only by a session
that actually starts Bitbucket work, and its always-loaded part is the frontmatter `description`
alone. Guidance that only matters mid-workflow belongs in the skill; guidance a client needs before
the first call belongs in `ServerInstructions`.

`AgentSkillTests` is what stops it drifting — see *Testing*.

## Package budget

Complete, as of the initial implementation. Versions are centrally pinned in
`Directory.Packages.props`.

- `src/Bitbucket.Mcp`: `ModelContextProtocol` (pinned exactly, `[2.4.0]`),
  `Microsoft.Extensions.DependencyInjection`, `Microsoft.Extensions.Logging.Console`,
  `System.Security.Cryptography.ProtectedData` (DPAPI; Windows-only code path).
- `tests/`: `NUnit`, `NUnit3TestAdapter` (the MTP runner), `NUnit.Analyzers`, `GitHubActionsTestLogger`
  (D11). No mocking or assertion libraries — use the hand-rolled `StubHttpMessageHandler`, and
  `Expect` for the few assertions that hand back what they checked.
- `build/`: `Fallout.Common` + `Fallout.Components` 10.5.0 (the build project opts out of CPM).

SourceLink needs no package reference — it is in-SDK on .NET 8 and later.

### Package budget changes

**2026-08-17 — `NuGet.Frameworks` 7.9.0 in `build/_build.csproj`.** A version pin on a package that
was already there transitively, not a new dependency, and nothing in `src/` or `tests/` moves.
Fallout evaluates project files *in-process* — `ITest` asks each test project whether it references
`GitHubActionsTestLogger` — which runs the installed SDK's own targets inside the Fallout process.
Those targets bind `NuGet.Frameworks` by strong name; SDK 10.0.400 wants 7.9.0.0, while
`Fallout.Components` 10.4.0 resolves 6.14.3 transitively through `NuGet.Packaging`. App-local
assemblies win over the resolver `MSBuildLocator` installs, so the older copy shadowed the SDK's and
every `Test` run died in `[MSBuild]::GetTargetFrameworkIdentifier(net10.0)` with
`InvalidProjectFileException`. `Compile` and `Pack` shell out to the `dotnet` CLI and stayed green,
which is why it looked like a test failure rather than a toolchain one. Nothing already present can
supply the assembly: the version is chosen by Fallout's transitive graph, so the only lever is a
direct reference. Pinned *forward* rather than pinning the SDK back — the runtime binds a higher
assembly version than the one requested, so an SDK still asking for 6.14.3 is satisfied too, and the
repository is not dated to one feature band. Remove once Fallout ships a `NuGet.Packaging` new
enough to bring 7.9.0 in on its own.

**2026-09-09 — routine version bumps; no new package.** `ModelContextProtocol` `[2.1.0]` →
`[2.2.0]`, the three `Microsoft.Extensions.*` / `System.Security.Cryptography.ProtectedData`
10.0.10 → 10.0.12, `Microsoft.NET.Test.Sdk` 18.8.1 → 18.10.0. Nothing was added and nothing moved
between projects. The exact MCP pin moved on evidence rather than on faith: the public metadata of
`ModelContextProtocol.dll` and `ModelContextProtocol.Core.dll` is **identical** between 2.1.0 and
2.2.0 — no type or member added or removed — and both versions declare the same eight transitive
dependencies at the same versions, so the AOT/serializer contract the pin exists to protect cannot
have changed shape. 2.2.0's two changes (`HttpServerSessionMode`, an `McpHeaderEncoder.DecodeValue`
fix) are both on the HTTP transport; `HttpServerSessionMode` lives in `ModelContextProtocol.AspNetCore`,
which this repository does not reference, and the server speaks stdio only. Verified by `Test`
(which asserts every tool name, title, annotation and generated schema *through* the SDK) and by
`SmokeTest` (a real `tools/list` against the AOT binary).

**2026-11-18 — Fallout 10.5.0, the `NuGet.Frameworks` pin removed, the tests on NUnit/MTP.**
- `Fallout.Common` / `Fallout.Components` / `fallout.globaltool` 10.4.0 → 10.5.0. 10.4.0 restores
  `System.Security.Cryptography.Pkcs` / `Xml` 10.0.10 into `build/`, which now carry NU1902/NU1903
  advisories; 10.5.0 brings 10.0.13. It regenerates both workflows unchanged; only
  `.fallout/build.schema.json` moves.
- The `NuGet.Frameworks` 7.9.0 reference (2026-08-17) is gone: its exit condition is met, because
  Fallout 10.5.0 brings `NuGet.Packaging` 7.9.0, the version SDK 10.0.4xx binds. Verified on the path
  that used to fail — `dotnet fallout Test` with `GITHUB_ACTIONS=true` — with app-local
  `NuGet.Frameworks` 7.9.0.0. Removing `ITest` (D11) also removed the in-process evaluation that made
  the pin load-bearing at all: on SDK 11.0.100, whose MSBuild asks for 7.11.0.0, the same run is green
  without it too.
- Tests: `xunit.v3`, `xunit.runner.visualstudio` and `Microsoft.NET.Test.Sdk` out; `NUnit` 5.0.0,
  `NUnit3TestAdapter` 6.3.1, `NUnit.Analyzers` 4.15.0 and `GitHubActionsTestLogger` 3.0.5 in (D11).
  The test project's restore graph goes from 42 packages to 34. `GitHubActionsTestLogger` is the
  one third-party addition; it is test-only, and it is what turns a failure into an annotation.
  `Microsoft.ApplicationInsights` stays in the graph, through MTP's telemetry extension, which
  `NUnit3TestAdapter` requires; the `Test` target sets `TESTINGPLATFORM_TELEMETRY_OPTOUT`.
- `ModelContextProtocol` `[2.2.0]` → `[2.4.0]`, `Microsoft.Extensions.*` /
  `System.Security.Cryptography.ProtectedData` 10.0.12 → 10.0.15. 2.3.0 makes the client's
  `ServerInfo` nullable and keeps `tools/list` in registration order; 2.4.0 fixes legacy output
  schemas for delegating tools. The server builds with no new warning, every schema test passes,
  and the AOT binary grows 14 KB.

**Held deliberately, and not stale:** `Fallout.Common` / `Fallout.Components` stay on the 10.x line
because 11.0.x is the *edge* channel and sorts higher only as a versioning artifact — see *Build*.
The target framework stays `net10.0` (D23).

The trusted-publishing exchange (D17) uses `Fallout.Common.Utilities.Net`
(`CreateRequest` / `WithBearerAuthentication` / `WithJsonContent` / `GetResponse` /
`AssertResponse` / `AssertSuccessfulStatusCode` / `GetBodyAsJsonObject`), which arrives with `Fallout.Common` as the
transitive `Fallout.Utilities.Net` — no new `PackageReference` anywhere, and nothing added to
`src/` or `tests/`.

## Layout

```
.claude-plugin/             marketplace.json + plugin.json — the repository as its own Claude Code
                            plugin marketplace, source "./" (PluginManifestTests)
.claude/skills/             The shipped Agent Skill (one canonical copy; AgentSkillTests keeps its
                            tool references in step with the inventory)
.claude/CLAUDE.md           Imports AGENTS.md for Claude Code — under .claude/, because the root is
                            the plugin's root (see *The plugin and its marketplace*)
.mcp/server.json            MCP server manifest (D17), packed into the NuGet package at
                            /.mcp/server.json; its version must match CHANGELOG.md (test-enforced)
src/Bitbucket.Mcp/          One production project (D1); AssemblyName bitbucket-mcp
  Program.cs                Entry point — hands argv straight to the CLI dispatcher
  McpServerSetup.cs         DI wiring, JSON options (D6), tool registration with the argument-name
                            guard (D24), stdio run loop
  ServerVersion.cs          Product name + version, read from the assembly (build stamps it)
  Cli/                      login / logout / status / help (D15). The ONLY place stdout is legal
  Configuration/            BitbucketMcpOptions.FromEnvironment() — the complete env-var surface
  Authentication/           ICredentialProvider and both implementations, OAuth token client,
                            refresh state machine, TokenStore, loopback listener, browser launcher
  Http/                     BitbucketApiClient, the handler chain (auth + retry), BitbucketCursor
                            (SSRF-validated), BitbucketRequestBuilder, FieldSets, Page<T>,
                            BitbucketApiException, InvalidCursorException
  Http/Models/              Wire DTOs and BitbucketWireJsonContext (snake_case; never chained
                            into the MCP JSON options)
  Diffs/                    UnifiedDiffParser, DiffTruncator, InlineAnchorResolver
  Pipelines/                LogReducer (tail/head/search + ANSI stripping), LogExcerpt, LogReadMode
                            — the Diffs/ analogue, and the only novel logic in the pipeline surface
  Tools/                    PullRequestReadTools, PullRequestWriteTools, PipelineReadTools,
                            ToolDefaults, ToolErrors, ToolArguments (D24), ResultMapper
                            (+ ResultMapper.Pipelines), ServerInstructions
  Tools/Models/             Result records and BitbucketToolJsonContext (camelCase)
tests/Bitbucket.Mcp.Tests/  The single test project; internals visible via InternalsVisibleTo
.github/workflows/          build.yml and publish.yml are GENERATED from the two [GitHubActions]
                            attributes (hard rule 2); release.yml is hand-written by design
build/                      The Fallout orchestrator (Build.cs, Build.Publish.cs,
                            Build.CI.GitHubActions.cs, Configuration.cs, ReleaseNotes.cs,
                            ReleaseNotesParser.cs, SemVersion.cs, Extensions/) — `build/` is a
                            resolver convention, and `.gitignore` must never untrack it
```

Everything a user can configure is an environment variable read in `Configuration/`; everything a
user can see is either a tool result shaped in `Tools/Models/` or an error composed in
`Tools/ToolErrors.cs`. Those three files are where a behaviour change becomes user-visible, so
they are the ones to keep `README.md` in step with.

The **scope lists are duplicated** and nothing enforces their agreement: `ToolErrors.cs` holds
`RequiredScopes`, `RequiredApiTokenScopes`, `PipelineScope` and `PipelineApiTokenScope`, and
`README.md` restates all of them in *Tokens* and again in *Troubleshooting*. They have to move
together. The failure mode worth knowing is that an Atlassian API token created with the plain
*Create API token* button carries **no** Bitbucket scope at all and answers
`401 {"error": "API Token provided has no Bitbucket scopes."}` — a string where the documented
envelope has an object, which is why `ToolErrors.Detail` falls back to the raw body rather than
dropping the one sentence that explains the failure.

## Build

The orchestrator is [Fallout](https://fallout.build) 10.5.0 (stable channel), the maintained
hard fork of NUKE. 10.5.0 is also the first that starts when the build project targets `net11.0`
(Fallout-build/Fallout#715) — keep that in mind before D23 is revisited.

**10.5.0 is not stale, and 11.0.x is not an upgrade.** Fallout runs two channels: `11.0.x` is
*edge* and `10.x` is *stable*, which is newer and a feature superset despite sorting lower. The
edge CLI ships under a different package id (`Fallout.Cli`), which is why `fallout.globaltool` has
no 11.x and nothing is behind. Moving `build/` to 11.0.18 would reintroduce
`System.Security.Cryptography.Xml` 10.0.6 (10.5.0 carries the patched 10.0.13), fail to compile
because `GitHubActionsAttribute.Env` — which `NUGET_USER` depends on — does not exist there, and
regenerate both workflows backwards to `checkout@v6` / `setup-dotnet@v4` with the `*Action`
override properties gone. Do not "update" it. The CLI is pinned in `.config/dotnet-tools.json` as `fallout.globaltool`
(command `fallout`) and resolves `build/_build.csproj` by convention.

```powershell
.\build.ps1 Test          # restore, compile, run tests
.\build.ps1 SmokeTest     # AOT publish + real stdio JSON-RPC handshake against the binary
.\build.ps1 Pack          # the NuGet tool package (D17) into artifacts/packages
```

`PublishAot` is switched **off when no `RuntimeIdentifier` is set**. `dotnet pack` of a tool
package packs `$(PublishDir)`, so it runs a publish of the server project; left at `true` that
publish fails with *RuntimeIdentifier is required for native compilation* (D9 deliberately keeps
RIDs out of the csproj). The AOT publish always passes `-r`, so keying the property off
`RuntimeIdentifier` gives the release path AOT and the pack path a portable IL tool from the same
project file.

`CHANGELOG.md` is the **version authority**: its top section is parsed in `OnBuildInitialized`
and passed to the build as the version. The file is never mutated by the build, and the first
line must parse as a version header (`# 1.0.0`) — do not add a `# Changelog` title, it would
abort the build.

## Testing

```powershell
.\build.ps1 Test                                             # the whole suite, via the orchestrator
dotnet test --project tests\Bitbucket.Mcp.Tests               # the same tests, without the build
dotnet test --project tests\Bitbucket.Mcp.Tests --filter "FullyQualifiedName~Cursor"
```

NUnit 5 on Microsoft.Testing.Platform (D11); `global.json` selects MTP, which is why the project is
named with `--project`. No mocking or assertion
libraries: HTTP is faked with the hand-rolled `StubHttpMessageHandler`, and fixtures are embedded
resources. Because `JsonSerializerIsReflectionEnabledByDefault=false` is set in the test csproj as
well (D7), a type missing from a `JsonSerializerContext` fails under `dotnet test` rather than
only after an AOT publish.

Some rules are too easy to break silently to be left to review, so tests enforce them by
reflection or by scanning the source tree. Do not delete one to make a change pass:

- **Every paginated field set contains `next`.** Any constant in `Http/FieldSets.cs` whose value
  mentions `values.` is a paginated response, and an inclusive `fields=` list that omits `next`
  makes Bitbucket return page one with no continuation link — pagination then stops after one
  page, silently, and only against a repository large enough to have a second one.
- **The tool inventory matches the design table.** A reflection test walks `[McpServerTool]`
  methods and asserts the full set of names, and each tool's `ReadOnly` / `Destructive` /
  `Idempotent` / `OpenWorld` flags, against *Tool table* above — plus a `[Description]` on every
  tool method and every non-injected parameter. `Destructive` defaults to *true* in the SDK, so a
  new non-destructive write tool fails this test until it says so explicitly. A new tool has to be
  added in four places: that table, `ToolInventoryTests.ExpectedToolNames`, `Build.cs`'s
  `ExpectedToolNames` (which `SmokeTest` checks against a live `tools/list`) and the shipped skill
  (next bullet).
- **The generated schemas are checked property by property.** `ToolSchemaTests` is the only place
  that asserts no injected parameter (`client` / `options` / `cancellationToken`) leaks into an
  input schema, that every input *and* output property is camelCase and carries a description, that
  every tool has an object output schema, that every paginated result carries `nextCursor`, and
  D6's resolver ordering (ours first, SDK second, chain read-only). Its per-tool `[TestCase]` row
  is a second inventory — of exact parameter names, in declaration order — so a reordered parameter
  fails here and nowhere else.
- **`NoStdoutWritesTest`.** Scans the production sources for `Console.Write*` and fails on any
  outside `src/Bitbucket.Mcp/Cli/` (hard rule 3). In server mode stdout is the protocol channel.
- **The shipped skill names the tools that exist, and all of them.** `AgentSkillTests` reads
  `.claude/skills/bitbucket-pull-requests/SKILL.md` (the same root walk `NoStdoutWritesTest` uses)
  and cross-checks every backticked tool-shaped identifier against the reflected inventory, in both
  directions: a name no tool answers to fails, and so does a tool the skill never mentions. Tool
  *parameter* names are excluded by reflection, not by a list, so `mergeStrategy` does not read as a
  missing tool. Nothing else loads that file, so without this it would keep advertising an older
  surface — the inventory grew from ten tools to sixteen during development and to twenty-two since, and a skill written
  before that would still have read as correct. It is therefore a **fourth** place a new tool has to
  be added, after the *Tool table*, `ToolInventoryTests` and `Build.cs`. The frontmatter is checked
  too: `name` must equal the directory name and satisfy the Agent Skills spec's charset, and
  `description` must be present and within 1024 characters.
- **The plugin manifest never maps a credential onto its plain variable name** (D22).
  `PluginManifestTests` asserts that every `env` key carries the `CLAUDE_PLUGIN_OPTION_` prefix and
  that no plain `BITBUCKET_*` name appears as one. Reverting that mapping is a one-word edit whose
  entire effect is invisible — the plugin still installs, still starts, and silently authenticates
  as something other than what the user configured.
- **The plugin manifests still describe a working install.** `PluginManifestTests` asserts that the
  marketplace lists this repository as its one plugin with `source: "./"`, that the declared skill
  path still has a `SKILL.md` behind it (a moved skill would otherwise ship a plugin with no skill),
  that the plugin `version` and the `dnx` pin are both `CHANGELOG.md`'s version, and that the
  credential surface matches `.mcp/server.json` placeholder for placeholder. Nothing in the build
  reads these manifests back, exactly as with the server manifest.
- **`.mcp/server.json` agrees with the version authority.** `McpServerManifestTests` reads the
  checked-in manifest, `CHANGELOG.md` and the server csproj from the repository root (the same
  root walk `NoStdoutWritesTest` uses) and asserts the manifest's `version` and its NuGet entry's
  `version` both equal the changelog's top version, and that its `identifier` equals `<PackageId>`.
  Nothing in the build reads the manifest back, so without this a released package would keep
  advertising an old version indefinitely.

`SmokeTest` is the end-to-end check the unit tests cannot be: it publishes the Native AOT binary,
spawns it, and drives a real `initialize` / `notifications/initialized` / `tools/list` exchange
over stdio, asserting `serverInfo.name` and all twenty-two tool names. CI runs `Test` and `SmokeTest`
on every push and pull request.

## Release engineering

Releases are cut by pushing a `v*` tag, which starts **two independent workflows**:
`release.yml` (binaries + the GitHub Release) and `publish.yml` (nuget.org, D17 — *The NuGet leg*
below). They do not depend on each other, deliberately: the AOT binaries are the product, so a
nuget.org outage or a policy mismatch must not be able to hold up the GitHub Release, and neither
must the reverse.

`.github/workflows/release.yml` is hand-written (the `[GitHubActions]` generator has no matrix
support) and runs five publish legs — one per RID, each on its own hardware — followed by a
`release` job that assembles the GitHub Release:

| RID | Runner |
|---|---|
| `linux-x64` | `ubuntu-24.04` |
| `linux-arm64` | `ubuntu-24.04-arm` |
| `win-x64` | `windows-latest` |
| `win-arm64` | `windows-11-arm` |
| `osx-arm64` | `macos-latest` |

Every leg runs `dotnet fallout SmokeTest --runtime <rid>`, and `SmokeTest` depends on `PublishAot`,
so each leg compiles its own binary, speaks JSON-RPC to it *on the architecture it targets*, and
only then uploads the archive. **Never ship a binary that has not answered a handshake on its own
architecture** — that is the whole reason the arm64 legs are not cross-compiled. `fail-fast` is off
so one broken runtime cannot mask the state of the other four.

The Linux legs also check the **glibc floor**. A Native AOT binary links against the build host's
glibc, and the released binaries need `GLIBC_2.34` at most (read from the v1.2.1 binary's ELF
version needs, 2026-11-18). `linux-x64` is therefore pinned to `ubuntu-24.04` rather than
`ubuntu-latest`, which moved to 26.04, and a step after the smoke test fails the leg if `objdump -T`
shows a newer `GLIBC_` version. When 24.04 is retired, that step is what turns red.

`workflow_dispatch` runs the same five legs as a **dry run** — build, smoke test, glibc check — and
skips the `release` job, which only runs for a `v` tag. Run it on a branch before tagging anything
that changes the toolchain.

`CHANGELOG.md` stays the version authority here too: `CreateGitHubRelease` refuses to publish
unless `GITHUB_REF_NAME` equals `v{version parsed from CHANGELOG.md}`. To release, land the
changelog entry first, then tag that commit.

Three properties of the release path were verified end to end by a throwaway `v0.0.1-test` tag
(branch, tag and prerelease deleted afterwards) rather than reasoned about:

- **R1 — arm64 runner availability: resolved, no fallback needed.** On this public repo both
  `ubuntu-24.04-arm` and `windows-11-arm` were assigned within seconds and completed the full
  AOT publish plus handshake (linux-arm64 1m19s, win-arm64 2m6s; the x64 and osx legs 1m–2m15s).
  Neither image needed extra native-toolchain setup. If that ever regresses, the documented
  fallback is to cross-compile the affected RID from the x64 runner of the same OS with its smoke
  step skipped — and to mark it as unverified in both `release.yml` and here, because an
  unverified binary must not go out silently.
- **R2 — the tar.gz exec bit: `CompressionExtensions.TarGZipTo` cannot be used.** Up to Fallout 10.4.0
  it archived through SharpZipLib's `TarEntry.CreateEntryFromFile`, which hard-codes *every* entry's
  mode to `0700` instead of reading it off disk; 10.5.0 moved it to SharpCompress, which nobody has
  verified here. That ships `LICENSE` and `README.md` executable and the
  binary unreadable to anyone but the extracting user. `PublishAot` therefore invokes the `tar`
  CLI (`ProcessTasks.StartProcess`), which is present on the GitHub runners and in Git Bash. The
  released archives were downloaded and inspected: `-rwxr-xr-x` on `bitbucket-mcp`,
  `-rw-r--r--` on `LICENSE` and `README.md`, flat at the archive root, on GNU tar (linux-x64,
  linux-arm64) and bsdtar (osx-arm64) alike. Windows RIDs keep using `ZipTo` — a zip carries no
  Unix mode and the payload is a `.exe`.
- **The release body**: `ICreateGitHubRelease` composes it with
  `ChangelogTasks.ExtractChangelogSectionNotes`, which only recognises `## ` headings and ends a
  section at the first line that is not a bullet. `CHANGELOG.md` uses `#` headings (what
  `ReleaseNotesParser` wants) and wraps its bullets over several lines, so pointed straight at it
  that helper returns nothing and the release ships empty. `Build.WriteReleaseNotes` reflows the
  newest section into `artifacts/release-notes.md` — one single-line bullet per entry — and
  `IHasChangelog.ChangelogFile` points there. Keep changelog entries as `- ` bullets with wrapped
  continuation lines and this keeps working.

`Prerelease` follows the version string (`Version.Contains('-')`), so a `1.0.0-rc.1` or
`0.0.1-test` tag publishes as a prerelease and never displaces the latest stable release.

### The NuGet leg

`.github/workflows/publish.yml` is **generated** from the second `[GitHubActions]` attribute in
`build/Build.CI.GitHubActions.cs` (hard rule 2 — never hand-edit it; any build run regenerates it,
or `dotnet fallout --generate-configuration GitHubActions_publish --host GitHubActions`). One
Ubuntu job runs `dotnet fallout Compile Test Pack Publish` with
`permissions: { contents: read, id-token: write }`, `environment: nuget` and
`env: NUGET_USER: lahma`.

Three shapes are load-bearing and were chosen, not defaulted:

- **Its own file.** A nuget.org trusted publishing policy is scoped by repository + workflow *file
  name* (+ environment) and the nuget.org UI has no branch or tag filter, so whichever file the
  policy names can mint a publishing key on *every* run of that file. `publish.yml` only ever runs
  on a `v*.*.*` tag, and `build.yml` — which runs on every push and PR — is not the file named in
  the policy and therefore cannot mint anything.
- **No `paths:` filter.** A path filter alongside `tags:` evaluates over the tag commit's diff and
  can silently skip the release run.
- **One job.** More jobs would mean more OIDC exchanges racing the one-key-per-30-s rate limit, and
  the target is Linux-only anyway.

`Publish` is `.OnlyWhenDynamic(() => ShouldPublish())` on a tag build and refuses in ordered steps
otherwise: a non-tag invocation is a logged *skip* (there is no preview feed — the only push this
repository makes is a tagged release), and once past the gate it asserts, in order, that it is on
GitHub Actions, that `NUGET_USER` is set, and that `GITHUB_REF_NAME` equals
`v{version from CHANGELOG.md}` — the same guard `CreateGitHubRelease` applies, for the same reason.
It then mints the key immediately before pushing, because the key lives 15–60 minutes and one OIDC
token mints exactly one key.

`IsTaggedBuild` reads `GITHUB_REF_NAME` on GitHub Actions (the ref the run was triggered for is the
authoritative answer, and it is what the guard compares against anyway) and falls back to a `v*`
tag on HEAD off CI, so the whole ladder is exercisable locally:

```powershell
dotnet fallout Publish --skip                      # skips, and says why
$env:GITHUB_ACTIONS='true'; $env:NUGET_USER='lahma'; $env:GITHUB_REF_NAME='v1.0.0'
dotnet fallout Publish --skip                      # must fail with "GitHub OIDC is unavailable"
```

That last failure is the success signal: it proves the tagged path routed all the way to the token
exchange. Clear the three variables afterwards.

Three things about the exchange are worth not rediscovering:

- **The POST to `https://www.nuget.org/api/v2/token` must carry a `User-Agent`.** The endpoint sits
  behind Azure Front Door, which answers `400 {"error":"A User-Agent header is required."}` to a
  request without one, *before* looking at the token or the policy — and a bare `HttpClient` sends
  none. Verified 2026-08-09 against the live endpoint with the exact request body the build sends:
  no UA → that 400; with a UA → `401 {"error":"The bearer token could not be parsed as a JSON web
  token."}`, which is the expected answer to a dummy bearer and proves the request shape is
  accepted. So **a 400 means the request never reached the policy** — stop looking at nuget.org
  settings.
- The OIDC audience is `https://www.nuget.org` and the response field is `apiKey` (the original
  design document's `aud: nuget` and `api_key` do not match production; the build accepts
  `api_key` as a fallback anyway). The minted key is `add-mask`ed, and Fallout already marks
  `DotNetNuGetPushSettings.ApiKey` as secret.
- `NUGET_USER` is the nuget.org **profile name of whoever created the trusted publishing policy**
  (`lahma`) — not an email, not an organisation. It is public information and is deliberately a
  plain workflow-level `env:` value (from `Env = ["NUGET_USER: lahma"]` on the attribute), not a
  secret: a secret would be masked out of exactly the error message that names it. Getting it wrong
  is the most common 401.

Setting up the policy on nuget.org is manual and must exist **before** the first tag: Trusted
Publishing → policy owner `lahma`, Repository Owner `lahma`, Repository `bitbucket-mcp`, Workflow
File `publish.yml` (filename only, **not** `release.yml` and not the `.github/workflows/` path),
Environment `nuget`. A policy covers every package the selected owner owns; there is no per-package
scoping.
