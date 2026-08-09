# bitbucket-mcp

A self-owned [Model Context Protocol](https://modelcontextprotocol.io) server for **Bitbucket
Cloud**, covering the full pull-request lifecycle in ten tools — list, read, diff, comment,
review, create, update, merge, decline — including the gaps in Atlassian's own Bitbucket tools
(update, decline, request changes, unapprove, diffstat, inline comments). It is written in C# on
.NET 10 and ships as a Native AOT binary per platform: one self-contained executable, about 18 MB
on win-x64 (6 MB in the release archive), starting in roughly ten milliseconds. The point of the
project is a supply chain one person can actually audit — the whole runtime dependency tree is
four packages, all from Microsoft or the official MCP organisation:
[`ModelContextProtocol`](https://www.nuget.org/packages/ModelContextProtocol) (pinned exactly to
2.1.0), `Microsoft.Extensions.DependencyInjection`, `Microsoft.Extensions.Logging.Console` and
`System.Security.Cryptography.ProtectedData`. Tool names follow Atlassian's camelCase verbNoun
convention, so it sits alongside the official Atlassian MCP server without a naming clash. MIT
licensed.

Bitbucket Data Center is explicitly out of scope.

## Tools

| Tool | What it does | Annotations |
|---|---|---|
| `listPullRequests` | Lists a repository's pull requests, most recently updated first — a summary per pull request (id, title, state, author, branches). Defaults to open ones only. | read-only, idempotent |
| `getPullRequest` | Reads one pull request in full: title, description, state, branches, reviewers and participants with their approvals. This is where reviewer UUIDs come from. | read-only, idempotent |
| `getPullRequestDiff` | Fetches a pull request's changes: `mode="diffstat"` (default) lists the changed files, `mode="diff"` with `paths` returns the unified diff. Truncation is always marked inline. | read-only, idempotent |
| `getPullRequestComments` | Lists a pull request's comments — general and inline — oldest first, with deleted comments filtered out. | read-only, idempotent |
| `createPullRequest` | Opens a new pull request. Title and source branch are required; reviewers are account UUIDs. | write, **not** destructive |
| `updatePullRequest` | Changes an existing pull request's title, description, destination branch or reviewer list. `reviewers` replaces the whole list. | write, destructive |
| `addPullRequestComment` | Posts a comment: general, a reply, or inline on a line of the diff — anchored by `codeSnippet` copied verbatim out of the diff, or by `line` plus `lineType`. | write, **not** destructive |
| `setPullRequestReviewStatus` | Sets the authenticated user's own review state: `APPROVED`, `CHANGES_REQUESTED` or `UNAPPROVED` (clears both flags). | write, **not** destructive, idempotent |
| `mergePullRequest` | Merges a pull request into its destination branch, with an optional merge strategy. | write, destructive |
| `declinePullRequest` | Declines a pull request, closing it without merging. | write, destructive |

Every tool is annotated open-world (it talks to a live Bitbucket workspace) and returns structured
content. `Destructive` defaults to *true* in the MCP SDK, so the three write tools that only ever
add something say otherwise explicitly — a client should prompt before a merge, not before a
comment.

## Install

Download the archive for your platform from
[GitHub Releases](https://github.com/lahma/bitbucket-mcp/releases) and extract it. Each archive is
named `bitbucket-mcp-{version}-{rid}` and contains the executable, `LICENSE` and this `README.md`.

| Platform | RID | Archive |
|---|---|---|
| Windows x64 | `win-x64` | `bitbucket-mcp-{version}-win-x64.zip` |
| Windows ARM64 | `win-arm64` | `bitbucket-mcp-{version}-win-arm64.zip` |
| Linux x64 | `linux-x64` | `bitbucket-mcp-{version}-linux-x64.tar.gz` |
| Linux ARM64 | `linux-arm64` | `bitbucket-mcp-{version}-linux-arm64.tar.gz` |
| macOS Apple silicon | `osx-arm64` | `bitbucket-mcp-{version}-osx-arm64.tar.gz` |

```bash
tar -xzf bitbucket-mcp-1.0.0-linux-x64.tar.gz
chmod +x bitbucket-mcp
./bitbucket-mcp --version
```

There is no runtime to install: the binary is self-contained. Put it wherever you like and note
the absolute path — that is what the MCP client configuration needs.

With no arguments the binary speaks MCP over stdio. It also has three CLI modes:

```
bitbucket-mcp login      Authenticate with Bitbucket via the OAuth browser flow.
bitbucket-mcp logout     Delete the cached OAuth tokens.
bitbucket-mcp status     Show the current authentication status.
```

## Authentication

Three mechanisms, in this precedence order. The first one that is configured wins, and the rest
are ignored:

1. `BITBUCKET_ACCESS_TOKEN` — sent as `Bearer`.
2. `BITBUCKET_EMAIL` + `BITBUCKET_API_TOKEN` — sent as `Basic base64(email:token)`.
3. `BITBUCKET_OAUTH_KEY` + `BITBUCKET_OAUTH_SECRET` — the OAuth 2.0 browser flow.

`bitbucket-mcp status` prints which one is in effect without printing any of the values.

Bitbucket **app passwords were removed on 2026-07-28** and are not implemented here. If a guide
tells you to create one, it is out of date.

### OAuth consumer setup

Recommended: the tokens it produces are refreshed silently, so nothing has to be rotated by hand.

OAuth consumers live in **workspace settings**. Not in your personal or account settings, and not
on admin.atlassian.com — those are the two places people look first, and the page is in neither.
Every plan has it, Free included. The direct URL:

```
https://bitbucket.org/{workspace}/workspace/settings/api
```

`{workspace}` is the workspace **slug** — the first segment of a repository URL,
`bitbucket.org/{workspace}/{repository}` — not the workspace's display name.

To navigate there instead: click your **avatar** and pick the workspace the repositories live in,
under *Recent workspaces* or *All workspaces* → the **Settings** cog in the top navigation →
**Workspace settings**, which is the entry under the *Bitbucket Administration* heading in that
menu, not the personal settings above it → **Apps and features** in the sidebar → **OAuth
consumers**.

Then:

1. **Add consumer**.
2. Fill in:
   - **Name**: anything, for example `bitbucket-mcp`.
   - **Callback URL**: `http://127.0.0.1:33418/callback` — exactly this string. Bitbucket compares
     it character for character against the redirect the server sends, and a mismatch is the most
     common first-run failure. (If port 33418 is taken, see `BITBUCKET_OAUTH_CALLBACK_PORT` below
     and register the port you pick.)
   - Tick **This is a private consumer**. The server authenticates with the consumer secret at the
     token endpoint, which is what a private consumer means.
3. Under **Permissions**, tick:
   - **Account**: Read
   - **Repositories**: Read and Write
   - **Pull requests**: Read and Write
4. **Save**, then expand the new consumer to read its **Key** and **Secret**.
5. Put them in the environment:

   ```bash
   export BITBUCKET_OAUTH_KEY=...
   export BITBUCKET_OAUTH_SECRET=...
   ```

   ```powershell
   $env:BITBUCKET_OAUTH_KEY = '...'
   $env:BITBUCKET_OAUTH_SECRET = '...'
   ```

6. Sign in once:

   ```
   bitbucket-mcp login
   ```

   A browser opens on Bitbucket's authorization page; one click sends the code back to the
   loopback listener, and the tokens are cached on disk. The command prints the granted scopes,
   the access-token expiry and the cache location.

After that, `bitbucket-mcp status` reports the cached grant and the server keeps it alive on its
own: the access token is renewed before the expiry Bitbucket reports for it, and Bitbucket's
single-use refresh tokens are rotated and persisted atomically, serialised across concurrent
processes. `bitbucket-mcp logout` deletes the cache (it does not revoke the grant at Bitbucket's
end — delete the consumer for that).

If an MCP client starts the server before anyone has signed in, the first tool call opens the
browser itself and blocks up to `BITBUCKET_MCP_AUTH_TIMEOUT_SECONDS`. Running `login` up front is
what keeps that from happening mid-conversation.

#### Can't see OAuth consumers?

Three things account for almost every case, in the order they are worth checking:

1. **You are in personal settings, not workspace settings.** The avatar menu offers both, one under
   each heading, and OAuth consumers have not lived under the personal one for years. *Apps and
   features* → *OAuth consumers* only exists under **Workspace settings**. Going straight to
   `https://bitbucket.org/{workspace}/workspace/settings/api` sidesteps the choice.

2. **The workspace is a legacy personal workspace** — one that was auto-created for you and is
   named after your username. In those, *Apps and features* is shown **only to the workspace
   owner**: a user with the Admin role does not see it either, which is what makes this one
   confusing on a shared workspace. Sign in as the owner, or create the consumer in a regular
   workspace. Atlassian documents it as
   [Apps and features settings are missing for admins in personal workspace](https://support.atlassian.com/bitbucket-cloud/kb/apps-and-features-settings-are-missing-for-admins-in-personal-workspace/)
   (BCLOUD-20342).

3. **You have no workspace at all.** Personal workspaces are no longer created automatically at
   signup, and an account without one has no workspace settings to open. Create a workspace at
   admin.atlassian.com → **Atlassian apps** → **Add app** → **Bitbucket**, which since early 2026 is
   where Bitbucket workspaces come from.

If none of that helps, skip OAuth: an [Atlassian API token](#tokens) supports every operation this
server performs, writes included, and needs no consumer.

### Tokens

For headless machines, CI, or when you would rather not create a consumer.

**Workspace, project or repository access token** — created under the workspace's, project's or
repository's **Settings** → **Access tokens**, with the *Pull requests: Write* and *Repositories:
Write* scopes:

```bash
export BITBUCKET_ACCESS_TOKEN=...
```

**Atlassian API token** — created at
[id.atlassian.com/manage-profile/security/api-tokens](https://id.atlassian.com/manage-profile/security/api-tokens).
Use the **Create API token with scopes** button. The plain *Create API token* button next to it
produces an unscoped token, and an unscoped token does not work against the Bitbucket API at all.
Choose **Bitbucket** as the app, then grant all four of these scopes:

- `read:repository:bitbucket`
- `write:repository:bitbucket`
- `read:pullrequest:bitbucket`
- `write:pullrequest:bitbucket`

The `:bitbucket` suffix is part of the scope id, not a description of it. The scopes do **not**
imply one another: creating, updating, commenting on, approving, merging or declining a pull
request needs `read:pullrequest:bitbucket` *and* `write:pullrequest:bitbucket`, and a write scope
granted on its own answers 403. Scopes cannot be edited afterwards — a token with the wrong set has
to be replaced.

Pair the token with the account's email. It is a **Basic** credential —
`base64(email:token)` — which is exactly what these two variables produce:

```bash
export BITBUCKET_EMAIL=you@example.com
export BITBUCKET_API_TOKEN=...
```

Do **not** put an API token in `BITBUCKET_ACCESS_TOKEN`: that variable is sent as `Bearer`, and
Bitbucket answers 401 *"Token is invalid, expired, or not supported for this endpoint"*. `Bearer` is
for OAuth access tokens and for workspace, project and repository access tokens. With the four
scopes above and Basic auth, an API token drives every operation this server performs, writes
included.

Tokens are read from the environment only. Nothing is cached, and `bitbucket-mcp login` is neither
needed nor used in this mode.

## Client configuration

MCP clients launch the binary with an environment block and talk to it over stdio. Use the
absolute path to the executable.

### Claude Code

```bash
claude mcp add bitbucket \
  --env BITBUCKET_OAUTH_KEY=... \
  --env BITBUCKET_OAUTH_SECRET=... \
  --env BITBUCKET_DEFAULT_WORKSPACE=my-workspace \
  -- /usr/local/bin/bitbucket-mcp
```

```powershell
claude mcp add bitbucket --env BITBUCKET_OAUTH_KEY=... --env BITBUCKET_OAUTH_SECRET=... -- C:\tools\bitbucket-mcp\bitbucket-mcp.exe
```

### VS Code

`.vscode/mcp.json` in the workspace (or the user-level `mcp.json`):

```json
{
  "servers": {
    "bitbucket": {
      "type": "stdio",
      "command": "C:\\tools\\bitbucket-mcp\\bitbucket-mcp.exe",
      "env": {
        "BITBUCKET_OAUTH_KEY": "...",
        "BITBUCKET_OAUTH_SECRET": "...",
        "BITBUCKET_DEFAULT_WORKSPACE": "my-workspace"
      }
    }
  }
}
```

### Claude Desktop

`claude_desktop_config.json` (`%APPDATA%\Claude\` on Windows,
`~/Library/Application Support/Claude/` on macOS):

```json
{
  "mcpServers": {
    "bitbucket": {
      "command": "/usr/local/bin/bitbucket-mcp",
      "env": {
        "BITBUCKET_OAUTH_KEY": "...",
        "BITBUCKET_OAUTH_SECRET": "..."
      }
    }
  }
}
```

Setting `BITBUCKET_DEFAULT_WORKSPACE` is worth it if you mostly work in one workspace: the
`workspace` parameter becomes optional on every tool, which is one less thing for the model to get
wrong. It is the workspace **slug** — the first URL segment of
`bitbucket.org/{workspace}/{repository}` — not its display name.

## Environment variables

Configuration is environment variables only; there are no config files and no configuration
providers. Every value is read once at startup, and a malformed or out-of-range value falls back
to the documented default rather than failing the process (in server mode there would be nowhere
to report it — stdout is the protocol channel).

| Variable | Default | Meaning |
|---|---|---|
| `BITBUCKET_ACCESS_TOKEN` | — | Bearer token (workspace, project or repository access token). Highest precedence; bypasses OAuth entirely. |
| `BITBUCKET_EMAIL` | — | Atlassian account email, paired with `BITBUCKET_API_TOKEN`. |
| `BITBUCKET_API_TOKEN` | — | Atlassian API token. With the email, sent as `Basic base64(email:token)`. Second precedence. |
| `BITBUCKET_OAUTH_KEY` | — | OAuth consumer key (browser flow). |
| `BITBUCKET_OAUTH_SECRET` | — | OAuth consumer secret (browser flow). |
| `BITBUCKET_OAUTH_CALLBACK_HOST` | `127.0.0.1` | Host in the redirect URI. Must match the consumer's registered callback URL. |
| `BITBUCKET_OAUTH_CALLBACK_PORT` | `33418` | Port in the redirect URI, 1–65535. Must match the consumer's registered callback URL. |
| `BITBUCKET_DEFAULT_WORKSPACE` | — | Default for the `workspace` tool parameter. The URL slug, not the display name. |
| `BITBUCKET_MCP_TOKEN_FILE` | per-OS (see [Security](#security)) | Full path of the OAuth token cache file, overriding the per-OS default. |
| `BITBUCKET_MCP_NO_BROWSER` | `0` | `1` never launches a browser; the authorize URL is logged instead. Accepts `1/true/yes/on` and `0/false/no/off`. |
| `BITBUCKET_MCP_AUTH_TIMEOUT_SECONDS` | `180` | Bound, in seconds (1–3600), on a browser sign-in started from inside a tool call. |
| `BITBUCKET_MCP_LOG_LEVEL` | `Information` | Minimum level for the stderr logger: `Trace`, `Debug`, `Information`, `Warning`, `Error`, `Critical`, `None`. |
| `BITBUCKET_MCP_MAX_LINES_PER_FILE` | `400` | Diff lines returned per file before truncation (1–100000). Overridable per call with `maxLinesPerFile`. |
| `BITBUCKET_MCP_MAX_DIFF_LINES` | `4000` | Diff lines returned per response before truncation (1–1000000). |

## Usage

The workflow the server is designed around is **diffstat first**. A whole-pull-request diff is
both what Bitbucket refuses to build on large pull requests (HTTP 555) and what burns the model's
context when it succeeds, so the diff tool defaults to listing files and fetches content only for
the paths you name.

A review, end to end:

1. **Find the pull request.**

   ```text
   listPullRequests { "repository": "my-repo", "state": "OPEN" }
   ```

   Returns a summary per pull request plus a `nextCursor` when there are more pages.

2. **Read it.**

   ```text
   getPullRequest { "repository": "my-repo", "pullRequestId": 42 }
   ```

   Description, state, branches, reviewers and their approvals. This is also where reviewer
   **UUIDs** come from — `{01234567-89ab-cdef-0123-456789abcdef}`, in braces. Bitbucket rejects
   names, nicknames and email addresses as reviewers, so they are never guessed here.

3. **See what changed.**

   ```text
   getPullRequestDiff { "repository": "my-repo", "pullRequestId": 42, "mode": "diffstat" }
   ```

   A list of files with per-file added/removed counts, paginated.

4. **Fetch only the files worth reading.**

   ```text
   getPullRequestDiff {
     "repository": "my-repo", "pullRequestId": 42, "mode": "diff",
     "paths": ["src/Api/Client.cs", "src/Api/Retry.cs"]
   }
   ```

   Paths are spelled exactly as diffstat reported them. The response carries `truncated` and a
   `hint` naming the call that shows the rest; every cut also leaves a visible marker inside the
   diff text. A truncated diff is never presented as a whole one.

5. **Comment on a line.**

   ```text
   addPullRequestComment {
     "repository": "my-repo", "pullRequestId": 42,
     "path": "src/Api/Retry.cs",
     "codeSnippet": "        await Task.Delay(delay, cancellationToken);",
     "content": "This ignores Retry-After."
   }
   ```

   `codeSnippet` is the line's text copied verbatim out of the diff. It is resolved against the
   file's diff to derive the anchor, which is far more reliable than counting lines; an ambiguous
   or missing snippet comes back as an error listing the candidate lines rather than a comment on
   the wrong line. `line` plus `lineType` (`ADDED`, `REMOVED`, `CONTEXT`) is the fallback.

6. **Approve.**

   ```text
   setPullRequestReviewStatus {
     "repository": "my-repo", "pullRequestId": 42,
     "status": "APPROVED", "comment": "Looks good."
   }
   ```

   Affects only the authenticated user's own stance. `UNAPPROVED` withdraws both an approval and a
   change request.

Two conventions worth repeating, because they are what the server's `initialize` instructions
spend their budget on:

- **Cursors are opaque.** Pass a result's `nextCursor` back as `cursor` verbatim. It is a
  base64url-encoded, validated Bitbucket URL, not something to edit or construct. Every filter is
  already encoded in it, so the other arguments are ignored when a cursor is passed.
- **Reviewers are UUIDs**, in braced form, read from `getPullRequest`.

## Troubleshooting

Start with `bitbucket-mcp status`: it prints which credential would win, the exact callback URL
the server will use, the token cache path and what is in it — and none of the values.

**403 Forbidden on a pull-request write.** It is not an endpoint limitation. Every Bitbucket
pull-request endpoint this server calls — create, update, comment, approve, request changes, merge,
decline — accepts an Atlassian API token, and the old "this endpoint does not support token-based
authentication" advice no longer applies. Check three things, in this order:

1. **Both scopes, not just the write one.** The scopes do not imply each other, so a token holding
   only `write:pullrequest:bitbucket` is a 403 on every pull-request write. The set is
   `read:repository:bitbucket`, `write:repository:bitbucket`, `read:pullrequest:bitbucket` and
   `write:pullrequest:bitbucket`; a token's scopes cannot be changed after it is created, so a
   wrong set means a new token. The OAuth equivalent is the consumer's permissions — *Account:
   Read*, *Repositories: Read and Write*, *Pull requests: Read and Write*, which is
   `pullrequest`, `pullrequest:write`, `repository` and `repository:write` on the wire. After
   widening a consumer's permissions, run `bitbucket-mcp logout` then `bitbucket-mcp login`: the
   cached grant still carries the old scopes.
2. **Basic, not Bearer.** An API token goes in `BITBUCKET_EMAIL` + `BITBUCKET_API_TOKEN`, which the
   server sends as `Basic base64(email:token)`. The same token in `BITBUCKET_ACCESS_TOKEN` is sent
   as `Bearer` and Bitbucket rejects it. `bitbucket-mcp status` prints which variable is in effect.
3. **The account's own access.** Scopes cannot grant more than the account has: the user the
   credential belongs to needs write access to the repository. A 403 that survives the first two
   checks is usually this one.

**HTTP 555, "diff too large".** Bitbucket refuses to build diffs beyond roughly 8,000 changed
lines or 200 files, and retrying never helps. Call `getPullRequestDiff` with `mode="diffstat"`,
then again with `mode="diff"` and `paths=[...]` naming the files you need. This is why `diffstat`
is the default mode.

**404 on a repository you can see in the browser.** `workspace` and `repository` are the two URL
*slugs* of `bitbucket.org/{workspace}/{repository}`, not display names. Bitbucket also answers 404
rather than 403 for a private repository the credential cannot reach, so check the token's access
if the slugs are right.

**429 Too Many Requests.** Bitbucket's limits are roughly 1,000–10,000 requests per hour. The
client already retries 429/408/502/503/504 with exponential backoff and honours `Retry-After`; a
429 that reaches you survived that. The error quotes the wait Bitbucket asked for when it sent one,
and otherwise says to wait about a minute. Ask for smaller pages, and fetch diffs per file rather
than per pull request.

**Port 33418 is already in use.** Set `BITBUCKET_OAUTH_CALLBACK_PORT` to a free port **and**
change the consumer's registered callback URL to match — Bitbucket compares the redirect URI as a
string. `bitbucket-mcp status` prints the URL the server will use, which is the one to register.

**The browser does not open.** The authorize URL is written to stderr; open it manually and the
listener still picks up the callback. On a headless or remote machine set
`BITBUCKET_MCP_NO_BROWSER=1` so the server stops trying, and authenticate with
`BITBUCKET_ACCESS_TOKEN` instead — or run `bitbucket-mcp login` on a machine with a browser and
copy the token cache over (it is portable except on Windows, where it is encrypted to the user
account).

**Signed in, but the server still asks for authentication.** Either a token variable is set and
takes precedence over OAuth (`status` says which), or the cache was created with a different
consumer key — the cache records a fingerprint of the key it was obtained with and ignores itself
when it does not match. `bitbucket-mcp logout` followed by `bitbucket-mcp login` resets it.

**OAuth stopped working after a few months away.** A Bitbucket refresh token that goes **three
months unused** expires, and once it has, there is nothing left to renew silently — the grant has
to be established again. Run `bitbucket-mcp login`. Nothing else needs changing: the consumer, its
permissions and the environment variables are all still valid. Normal use never hits this, because
every silent renewal counts as a use.

**Nothing works and you want to see why.** `BITBUCKET_MCP_LOG_LEVEL=Debug`. All logging goes to
stderr; MCP clients usually surface it in a server log pane.

## Security

- **The token cache is protected at rest.** On Windows it is DPAPI-encrypted to the current user
  account; everywhere else the file is created `0600` inside a `0700` directory, with the mode set
  before a single byte is written. Writes are atomic (temp file, flush to device, rename), and a
  cache that fails to decode is treated as absent rather than fatal. Locations:

  | OS | Path |
  |---|---|
  | Windows | `%LOCALAPPDATA%\bitbucket-mcp\tokens.json` |
  | macOS | `~/Library/Application Support/bitbucket-mcp/tokens.json` |
  | Linux | `${XDG_STATE_HOME:-~/.local/state}/bitbucket-mcp/tokens.json` |

  `BITBUCKET_MCP_TOKEN_FILE` overrides it, `bitbucket-mcp status` prints it, and
  `bitbucket-mcp logout` deletes it.

- **Secrets are never logged or printed.** Not the tokens, not the consumer secret, not even the
  consumer key — `status` reports which variables are set, never their contents, because a status
  readout has a habit of ending up in a bug report.

- **The OAuth callback is CSRF-protected.** The `state` parameter is 128 bits from the CSPRNG and
  is compared in constant time; anything can connect to a loopback port, and the state value is
  what distinguishes Bitbucket's redirect from a local process guessing at one. The listener binds
  before the browser is launched, caps the request it reads, and ignores stray connections.

- **Pagination cursors are validated against SSRF.** A cursor arrives as a tool argument from a
  model whose context is full of attacker-influenced text (pull request descriptions, comments,
  diff hunks). A decoded cursor must be `https`, on exactly `api.bitbucket.org`, on the default
  port, with no embedded credentials, under `/2.0/` — otherwise it is not a cursor. Nothing else
  is ever fetched with a live `Authorization` header attached.

- **Credentials are per-request, and a redirect only keeps them inside Bitbucket.**
  `SocketsHttpHandler` strips the `Authorization` header on *every* automatic redirect — including
  a same-host one, which is exactly what Bitbucket's diff and diffstat endpoints answer with — so
  automatic redirects are switched off and the pipeline follows them itself: `GET`/`HEAD` only, at
  most five hops, and the credential is re-attached only when the target is `https` on
  `api.bitbucket.org`. A redirect anywhere else is still followed, but as an anonymous request. The
  header is never set on the `HttpClient` itself, so nothing can leak by default.

- **The supply chain is four runtime packages**, all from Microsoft or the official MCP
  organisation, centrally pinned in `Directory.Packages.props` with transitive pinning on, and the
  MCP SDK pinned to an exact version. Adding one requires a recorded decision in
  [AGENTS.md](AGENTS.md). Builds are deterministic and SourceLink-enabled, so a release binary can
  be traced back to the commit it came from.

## Building from source

Needs the .NET 10 SDK (the exact version is pinned in `global.json`).

```bash
git clone https://github.com/lahma/bitbucket-mcp.git
cd bitbucket-mcp

./build.sh Test           # restore, compile, run the tests
./build.sh SmokeTest      # AOT publish + a real stdio JSON-RPC handshake against the binary
```

```powershell
.\build.ps1 Test
.\build.ps1 SmokeTest
```

The orchestrator is [Fallout](https://fallout.build); `build.ps1` / `build.sh` bootstrap the CLI
from `.config/dotnet-tools.json`, so nothing needs installing globally. To publish a binary for a
specific platform:

```
dotnet fallout PublishAot --runtime linux-arm64
```

The executable lands in `artifacts/publish/{rid}/` and the release archive in
`artifacts/archives/`. `CHANGELOG.md` is the version authority — the build parses its top section
and stamps that version into the binary.

## Contributing

Read [AGENTS.md](AGENTS.md) first. It records the design decisions, the package budget, and the
handful of hard rules that are easy to break by accident (no `Console.Write` outside `Cli/`, never
hand-edit the generated `build.yml`, never a bare `build` rule in `.gitignore`).

## License

MIT. See [LICENSE](LICENSE).
