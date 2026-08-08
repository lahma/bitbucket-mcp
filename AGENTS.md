# AGENTS.md

Guidance for humans and AI agents working in this repository. `CLAUDE.md` imports this file,
so keep it the single source of truth.

`bitbucket-mcp` is a Model Context Protocol server for **Bitbucket Cloud** (REST API 2.0),
written in C# on .NET 10 and published as a Native AOT single binary per RID. Bitbucket Data
Center is explicitly out of scope. The point of the project is a dependency tree small enough
for one person to audit — treat that as a hard constraint, not a preference.

## Hard rules

1. **No new NuGet packages without a decision recorded in this file.** The package budget below
   is complete. If a package looks necessary, add a row to *Package budget changes* with the
   date, the package, and why nothing already present can do the job — before referencing it.
2. **Never hand-edit `.github/workflows/build.yml`.** It is generated from the `[GitHubActions]`
   attribute in `build/Build.CI.GitHubActions.cs` and regenerating overwrites edits. Change the
   attribute and re-run the build. (`.github/workflows/release.yml` is hand-written by design —
   the generator has no matrix support — and *is* edited directly.)
3. **No `Console.Write*` outside `src/Bitbucket.Mcp/Cli/`.** In server mode stdout *is* the MCP
   protocol channel; a stray write corrupts the JSON-RPC stream. Logging goes to stderr
   (`AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace)`). Only the CLI modes
   (`login` / `logout` / `status`), which never speak the protocol, may write to stdout. A test
   enforces this.
4. **Diffstat first.** Never fetch a whole PR diff speculatively: call `getPullRequestDiff` with
   `mode="diffstat"`, then request specific files via `paths=[...]`. Bitbucket returns **555** on
   large diffs (~8k lines / 200 files) and a full diff burns the model's context either way.
   Truncation must always be visible, with continuation guidance — never silent.
5. **`.gitignore` must never contain a bare `build` rule.** The Fallout build project lives in
   `build/`; a stock Visual Studio .gitignore silently untracks the entire orchestrator.
6. LF line endings everywhere (`.gitattributes` enforces it). `TreatWarningsAsErrors` is on —
   the compiler and analyzers are the lint step.

## Design decisions (D1–D15)

| # | Decision |
|---|---|
| D1 | **One production project** `src/Bitbucket.Mcp` (Exe) + one test project. Testability via `InternalsVisibleTo`. |
| D2 | Binary `bitbucket-mcp` (`AssemblyName`), `RootNamespace Bitbucket.Mcp`. |
| D3 | **Bare `ServiceCollection`**, not `Host.CreateApplicationBuilder` — the SDK registers `McpServer` as a singleton; `provider.GetRequiredService<McpServer>().RunAsync()` (this is the SDK's own AOT test app shape). Drops config providers/metrics/lifetime from cold start. `PosixSignalRegistration` for SIGINT/SIGTERM. |
| D4 | **Hand-rolled retry `DelegatingHandler`** (~90 lines) — `Microsoft.Extensions.Http.Resilience` would pull in Polly (third-party) plus six more packages. |
| D5 | No `IHttpClientFactory` — one singleton `HttpClient` over a hand-built handler chain. |
| D6 | Our `JsonSerializerContext` goes **first** in `TypeInfoResolverChain`, the SDK resolver second (first-match-wins; ours-first guarantees identical JIT and AOT behavior). |
| D7 | `JsonSerializerIsReflectionEnabledByDefault=false` in the server **and** the test csproj — a missing `[JsonSerializable]` then fails in `dotnet test`, not only after an AOT publish. |
| D8 | **Static tool methods**; `BitbucketApiClient` as a plain parameter (DI-bound via `IServiceProviderIsService`, excluded from the schema); avoids per-call instance activation and is directly unit-testable. |
| D9 | No `RuntimeIdentifiers` in the csproj (would pull ILCompiler packs per RID on every restore); the RID list lives in `Build.cs` and the release matrix, and reaches publish via `-r`. |
| D10 | No `PublishSingleFile` (ignored under AOT). |
| D11 | **xunit.v3 + `xunit.runner.visualstudio` + `Microsoft.NET.Test.Sdk`** (VSTest bridge) so the stock Fallout `ITest` component works unmodified. Fallback if it breaks: a custom Test target with `--report-trx`. |
| D12 | OAuth loopback callback via a raw **`TcpListener`** bound to both `127.0.0.1` and `::1` (~60 auditable lines, no `HttpListener` platform quirks). |
| D13 | **Confidential OAuth client** (key + secret, Basic auth at the token endpoint), no PKCE (unconfirmed for Bitbucket Cloud; leave an internal hook). The user creates their own consumer. |
| D14 | Access-token lifetime always taken from `expires_in` (minus 60 s skew) — never hard-coded; Atlassian's docs contradict themselves (1 h vs 2 h). |
| D15 | **`login` / `logout` / `status` CLI modes** on the same binary (argv dispatch, hand-rolled parsing); no args = stdio server. CLI mode may use stdout; server mode never. |

Other locked choices worth restating: Bitbucket **app passwords are dead** (removed 2026-07-28) —
never implement them. Tool names are **camelCase verbNoun** (`createPullRequest`) via
`[McpServerTool(Name = ...)]`. `Destructive` defaults to **true** in the SDK, so non-destructive
writes must set it explicitly `false`. Never call `WithToolsFromAssembly()` (IL2026) — one
`WithTools<T>(jsonOptions)` per tool class.

## Package budget

Complete, as of the initial implementation. Versions are centrally pinned in
`Directory.Packages.props`.

- `src/Bitbucket.Mcp`: `ModelContextProtocol` (pinned exactly `[2.1.0]`),
  `Microsoft.Extensions.DependencyInjection`, `Microsoft.Extensions.Logging.Console`,
  `System.Security.Cryptography.ProtectedData` (DPAPI; Windows-only code path).
- `tests/`: `xunit.v3`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`. No mocking or
  assertion libraries — use the hand-rolled `StubHttpMessageHandler`.
- `build/`: `Fallout.Common` + `Fallout.Components` 10.4.0 (the build project opts out of CPM).

SourceLink needs no package reference — it is in-SDK on .NET 8 and later.

### Package budget changes

_None yet._

## Layout

```
src/Bitbucket.Mcp/          One production project (D1); AssemblyName bitbucket-mcp
  Program.cs                Entry point — hands argv straight to the CLI dispatcher
  McpServerSetup.cs         DI wiring, JSON options (D6), tool registration, stdio run loop
  ServerVersion.cs          Product name + version, read from the assembly (build stamps it)
  Cli/                      login / logout / status / help (D15). The ONLY place stdout is legal
  Configuration/            BitbucketMcpOptions.FromEnvironment() — the complete env-var surface
  Authentication/           ICredentialProvider and both implementations, OAuth token client,
                            refresh state machine, TokenStore, loopback listener, browser launcher
  Http/                     BitbucketApiClient, the handler chain (auth + retry), BitbucketCursor
                            (SSRF-validated), FieldSets, Page<T>, BitbucketApiException
  Http/Models/              Wire DTOs and BitbucketWireJsonContext (snake_case; never chained
                            into the MCP JSON options)
  Diffs/                    UnifiedDiffParser, DiffTruncator, InlineAnchorResolver
  Tools/                    PullRequestReadTools, PullRequestWriteTools, ToolDefaults, ToolErrors,
                            ResultMapper, ServerInstructions
  Tools/Models/             Result records and BitbucketToolJsonContext (camelCase)
tests/Bitbucket.Mcp.Tests/  The single test project; internals visible via InternalsVisibleTo
build/                      The Fallout orchestrator (Build.cs, Build.CI.GitHubActions.cs,
                            ReleaseNotesParser.cs, SemVersion.cs) — `build/` is a resolver
                            convention, and `.gitignore` must never untrack it
```

Everything a user can configure is an environment variable read in `Configuration/`; everything a
user can see is either a tool result shaped in `Tools/Models/` or an error composed in
`Tools/ToolErrors.cs`. Those three files are where a behaviour change becomes user-visible, so
they are the ones to keep `README.md` in step with.

## Build

The orchestrator is [Fallout](https://fallout.build) 10.4.0 (stable channel), the maintained
hard fork of NUKE. The CLI is pinned in `.config/dotnet-tools.json` as `fallout.globaltool`
(command `fallout`) and resolves `build/_build.csproj` by convention.

```powershell
.\build.ps1 Test          # restore, compile, run tests
.\build.ps1 SmokeTest     # AOT publish + real stdio JSON-RPC handshake against the binary
```

`CHANGELOG.md` is the **version authority**: its top section is parsed in `OnBuildInitialized`
and passed to the build as the version. The file is never mutated by the build, and the first
line must parse as a version header (`# 1.0.0`) — do not add a `# Changelog` title, it would
abort the build.

## Testing

```powershell
.\build.ps1 Test                                   # the whole suite, via the orchestrator
dotnet test tests\Bitbucket.Mcp.Tests               # the same tests, without a publish
dotnet test tests\Bitbucket.Mcp.Tests --filter "FullyQualifiedName~Cursor"
```

xunit.v3 with the `xunit.runner.visualstudio` VSTest bridge (D11). No mocking or assertion
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
  `Idempotent` / `OpenWorld` flags, against the table in this file — plus a `[Description]` on
  every tool method and every non-injected parameter. `Destructive` defaults to *true* in the SDK,
  so a new non-destructive write tool fails this test until it says so explicitly.
- **`NoStdoutWritesTest`.** Scans the production sources for `Console.Write*` and fails on any
  outside `src/Bitbucket.Mcp/Cli/` (hard rule 3). In server mode stdout is the protocol channel.

`SmokeTest` is the end-to-end check the unit tests cannot be: it publishes the Native AOT binary,
spawns it, and drives a real `initialize` / `notifications/initialized` / `tools/list` exchange
over stdio, asserting `serverInfo.name` and all ten tool names. CI runs `Test` and `SmokeTest` on
every push and pull request.

## Release engineering

Releases are cut by pushing a `v*` tag. `.github/workflows/release.yml` is hand-written (the
`[GitHubActions]` generator has no matrix support) and runs five publish legs — one per RID, each
on its own hardware — followed by a `release` job that assembles the GitHub Release:

| RID | Runner |
|---|---|
| `linux-x64` | `ubuntu-latest` |
| `linux-arm64` | `ubuntu-24.04-arm` |
| `win-x64` | `windows-latest` |
| `win-arm64` | `windows-11-arm` |
| `osx-arm64` | `macos-latest` |

Every leg runs `dotnet fallout SmokeTest --runtime <rid>`, and `SmokeTest` depends on `PublishAot`,
so each leg compiles its own binary, speaks JSON-RPC to it *on the architecture it targets*, and
only then uploads the archive. **Never ship a binary that has not answered a handshake on its own
architecture** — that is the whole reason the arm64 legs are not cross-compiled. `fail-fast` is off
so one broken runtime cannot mask the state of the other four.

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
- **R2 — the tar.gz exec bit: `CompressionExtensions.TarGZipTo` cannot be used.** It archives
  through SharpZipLib's `TarEntry.CreateEntryFromFile`, which hard-codes *every* entry's mode to
  `0700` instead of reading it off disk. That ships `LICENSE` and `README.md` executable and the
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
