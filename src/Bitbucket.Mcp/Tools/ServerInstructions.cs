namespace Bitbucket.Mcp.Tools;

/// <summary>
/// The instructions sent to the client during <c>initialize</c>.
/// </summary>
/// <remarks>
/// This text is the only chance to teach the conventions that no per-tool description can enforce:
/// which identifiers Bitbucket accepts, that a diff is fetched in two steps, and that cursors are
/// opaque. It is read once per session and every line costs context, so it stays about ten lines —
/// anything longer belongs in a parameter description, where it is read only when relevant.
/// </remarks>
internal static class ServerInstructions
{
    /// <summary>The instruction text.</summary>
    internal const string Text = """
        Tools for Bitbucket Cloud pull requests: read, review, comment, merge.

        - workspace and repository are the two URL segments of bitbucket.org/{workspace}/{repository} — slugs, never display names. workspace may be omitted when BITBUCKET_DEFAULT_WORKSPACE is set.
        - Diffs come in two steps. Call getPullRequestDiff with mode="diffstat" to see which files changed, then call it again with mode="diff" and paths=["..."] for the files you actually need. A whole-pull-request diff fails outright on large pull requests, so never ask for one speculatively.
        - Truncation is always marked inside the diff text and flagged by `truncated`; re-run with paths and a larger maxLinesPerFile to see the rest.
        - Paging: pass a result's nextCursor back verbatim as cursor. Cursors are opaque — never edit, decode or invent one.
        - Inline comments: prefer codeSnippet, the exact text of the line copied out of the diff, over line numbers. addPullRequestComment resolves it against the diff and reports ambiguity rather than guessing; omit path for a comment on the pull request itself.
        - Reviewers are Bitbucket account UUIDs in braced form ({...}), never names or emails. Read them from getPullRequest.
        - Review state: setPullRequestReviewStatus takes APPROVED, CHANGES_REQUESTED or UNAPPROVED; UNAPPROVED clears both flags.
        - createPullRequest, updatePullRequest, mergePullRequest and declinePullRequest take effect immediately on the real repository. Confirm the pull request with getPullRequest before merging or declining it.
        - First run needs a sign-in: `bitbucket-mcp login`, or BITBUCKET_ACCESS_TOKEN (or BITBUCKET_EMAIL + BITBUCKET_API_TOKEN) in this server's environment.
        """;
}
