# bitbucket-mcp

A self-owned [Model Context Protocol](https://modelcontextprotocol.io) server for Bitbucket
Cloud, covering the full pull-request lifecycle — create, review, comment, approve, merge —
including the gaps in Atlassian's own Bitbucket tools (update PR, decline, request changes,
unapprove, diffstat, inline comments). It is written in C# on .NET 10 and ships as a Native AOT
single binary per platform, with a deliberately tiny and fully auditable dependency tree
(the MCP SDK plus a handful of Microsoft packages) so the whole supply chain can be reviewed
by one person.

## Status

**Under construction.** The full README — installation, the OAuth consumer walkthrough,
the environment-variable reference, client configuration snippets and the tool table —
lands with the first release.

## License

MIT. See [LICENSE](LICENSE).
