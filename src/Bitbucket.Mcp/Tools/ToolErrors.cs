using System.Globalization;
using System.Net;
using System.Text;

using Bitbucket.Mcp.Authentication;
using Bitbucket.Mcp.Diffs;
using Bitbucket.Mcp.Http;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol;

namespace Bitbucket.Mcp.Tools;

/// <summary>
/// What the tool call was doing, so a failure can name the thing that failed.
/// </summary>
/// <param name="Tool">The tool's MCP name, for logs.</param>
/// <param name="Workspace">The resolved workspace slug.</param>
/// <param name="Repository">The repository slug.</param>
/// <param name="PullRequestId">The pull request number, when the call had one.</param>
internal readonly record struct ToolCallContext(
    string Tool,
    string? Workspace = null,
    string? Repository = null,
    int? PullRequestId = null);

/// <summary>
/// The single place an exception becomes something the model can act on.
/// </summary>
/// <remarks>
/// <para>
/// Every tool body runs inside <see cref="ExecuteAsync"/>, so <b>only <see cref="McpException"/>
/// ever escapes a tool method</b> — anything else would reach the client as the SDK's generic "An
/// error occurred" and throw away the one chance to tell the caller what to do differently.
/// </para>
/// <para>
/// The messages are written for a model mid-task, not for a bug report: each one says what
/// happened, names the parameter or environment variable to change, and where a retry is plausible
/// says which call to make next. Status codes are translated rather than quoted, because "403" on
/// its own is indistinguishable from a dozen different mistakes.
/// </para>
/// </remarks>
internal static class ToolErrors
{
    /// <summary>The scopes the server's operations need, in Bitbucket's own spelling.</summary>
    private const string RequiredScopes = "pullrequest, pullrequest:write, repository, repository:write";

    /// <summary>
    /// Bitbucket's non-standard status for "the diff is too big to generate". Not in
    /// <see cref="HttpStatusCode"/>, so it is matched numerically.
    /// </summary>
    private const int DiffTooLargeStatus = 555;

    /// <summary>
    /// Where the unexpected-exception branch logs its stack trace. Assigned once from
    /// <c>McpServerSetup</c>; a null logger keeps the funnel usable from tests, where nothing has
    /// wired up logging.
    /// </summary>
    private static ILogger _logger = NullLogger.Instance;

    /// <summary>Points the funnel's diagnostic logging at the server's stderr logger.</summary>
    internal static void UseLoggerFactory(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _logger = loggerFactory.CreateLogger(typeof(ToolErrors).Namespace!);
    }

    /// <summary>
    /// Runs a tool body, converting anything it throws into an <see cref="McpException"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="OperationCanceledException"/> is rethrown untouched: the client cancelled, and
    /// the SDK's cancellation path — not an error result — is the correct answer. An
    /// <see cref="McpException"/> from argument validation is already the finished product and
    /// passes through unchanged.
    /// </remarks>
    internal static async Task<T> ExecuteAsync<T>(ToolCallContext context, Func<Task<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (McpException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw ToMcpException(exception, context);
        }
    }

    /// <summary>Maps one exception to the error the caller sees.</summary>
    internal static McpException ToMcpException(Exception exception, ToolCallContext context)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            AuthenticationRequiredException authentication => new McpException(
                Authentication(authentication), authentication),

            BitbucketApiException api => new McpException(Api(api, context), api),

            InvalidCursorException cursor => new McpException(
                "Invalid cursor. Omit cursor to start from the first page and pass nextCursor back verbatim.",
                cursor),

            InlineAnchorException anchor => new McpException(anchor.Message, anchor),

            _ => Unexpected(exception, context),
        };
    }

    // -------------------------------------------------------------------------------------------
    // Authentication
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Composes the sign-in message: a first line saying what is wrong, then every route out of it
    /// in order of how little work it is.
    /// </summary>
    private static string Authentication(AuthenticationRequiredException exception)
    {
        var message = new StringBuilder(ReasonLine(exception.Reason));

        if (!string.IsNullOrWhiteSpace(exception.AuthorizeUrl))
        {
            message.Append("\n\nOpen this URL in a browser to authorize, then retry the call:\n  ")
                .Append(exception.AuthorizeUrl);
        }

        message
            .Append("\n\nOr sign in once from a terminal on this machine:\n  bitbucket-mcp login")
            .Append("\nThe token is cached, and this server picks it up on the next call — no restart needed.")
            .Append("\n\nOr skip OAuth entirely by setting one of these in the environment the MCP client ")
            .Append("launches this server with, then restarting it:")
            .Append("\n  BITBUCKET_ACCESS_TOKEN — a workspace or repository access token (sent as Bearer)")
            .Append("\n  BITBUCKET_EMAIL + BITBUCKET_API_TOKEN — an Atlassian account email and API token")
            .Append("\n(Bitbucket app passwords were removed on 2026-07-28 and are not an option.)");

        return message.ToString();
    }

    private static string ReasonLine(AuthenticationRequiredReason reason) => reason switch
    {
        AuthenticationRequiredReason.NotConfigured =>
            "Bitbucket authentication is not configured, so this server cannot call the API yet.",

        AuthenticationRequiredReason.NoCachedToken =>
            "You are not signed in to Bitbucket: OAuth is configured but there is no cached token.",

        AuthenticationRequiredReason.RefreshFailed =>
            "Your Bitbucket sign-in expired and could not be renewed — the cached refresh token was rejected " +
            "(Bitbucket's refresh tokens are single-use, so this also happens if one was lost mid-rotation). " +
            "Signing in again fixes it.",

        AuthenticationRequiredReason.BrowserUnavailable =>
            "Bitbucket needs an interactive sign-in, but no browser could be opened here " +
            "(BITBUCKET_MCP_NO_BROWSER is set, or launching one failed).",

        AuthenticationRequiredReason.InteractiveTimeout =>
            "The Bitbucket sign-in was started but nobody completed it in time " +
            "(BITBUCKET_MCP_AUTH_TIMEOUT_SECONDS bounds the wait).",

        AuthenticationRequiredReason.InteractiveFailed =>
            "The Bitbucket sign-in did not complete — the browser callback never arrived, or it failed " +
            "verification.",

        _ => "Bitbucket authentication is required.",
    };

    // -------------------------------------------------------------------------------------------
    // Bitbucket API failures
    // -------------------------------------------------------------------------------------------

    private static string Api(BitbucketApiException exception, ToolCallContext context)
    {
        var status = (int) exception.StatusCode;
        var detail = Detail(exception);

        return status switch
        {
            400 => BadRequest(exception, detail),
            401 => Unauthorized(detail),
            403 => Forbidden(detail, context),
            404 => NotFound(detail, context),
            409 => Conflict(detail),
            429 => RateLimited(exception, detail),
            DiffTooLargeStatus => DiffTooLarge(detail),
            202 => Queued(detail),
            >= 500 => ServerError(exception, status, detail),
            _ => Other(status, detail),
        };
    }

    private static string BadRequest(BitbucketApiException exception, string? detail)
    {
        var message = new StringBuilder("Bitbucket rejected the request as invalid (400).");
        Append(message, detail);

        var fields = exception.Error?.Error?.Fields;

        if (fields is { Count: > 0 })
        {
            message.Append("\nField errors:");

            foreach (var (field, errors) in fields)
            {
                message.Append("\n  ").Append(field).Append(": ").Append(string.Join(" ", errors));
            }
        }

        message.Append("\nFix the named arguments and call again; retrying unchanged will fail the same way.");
        return message.ToString();
    }

    private static string Unauthorized(string? detail)
    {
        var message = new StringBuilder("Bitbucket rejected the credentials (401 Unauthorized).");
        Append(message, detail);

        message
            .Append("\nThe token is missing, expired or revoked. Run `bitbucket-mcp login` to sign in again, ")
            .Append("or replace BITBUCKET_ACCESS_TOKEN / BITBUCKET_API_TOKEN in this server's environment.")
            .Append("\nBitbucket has required auth headers since 2026-05: a token in a URL or a body is a 401.");

        return message.ToString();
    }

    private static string Forbidden(string? detail, ToolCallContext context)
    {
        var message = new StringBuilder("Bitbucket refused this operation (403 Forbidden).");
        Append(message, detail);

        message.Append("\nEither the token lacks a scope or the account lacks permission on ").Append(Target(context))
            .Append(". This server's operations need these consumer scopes: ").Append(RequiredScopes).Append('.')
            .Append("\nKnown Bitbucket bug: some pull request writes answer 403 with \"this endpoint does not ")
            .Append("support token-based authentication\" even when the scopes are correct. Scoped Atlassian API ")
            .Append("tokens hit it; OAuth does not — run `bitbucket-mcp login` and retry to work around it.");

        return message.ToString();
    }

    private static string NotFound(string? detail, ToolCallContext context)
    {
        var message = new StringBuilder("Bitbucket has no such resource (404 Not Found).");
        Append(message, detail);

        message.Append("\nThis call looked for ").Append(Target(context)).Append('.')
            .Append("\nworkspace and repository are the two URL segments of ")
            .Append("bitbucket.org/{workspace}/{repository} — the slugs, not the display names. A pull request ")
            .Append("number belongs to one repository, so the same number in a different repository is a ")
            .Append("different pull request (or none).")
            .Append("\nBitbucket also answers 404 — not 403 — for a private repository the credential cannot ")
            .Append("see, so check the token's access if the slugs are right.");

        return message.ToString();
    }

    private static string Conflict(string? detail)
    {
        var message = new StringBuilder("Bitbucket reported a conflict (409).");
        Append(message, detail);

        message
            .Append("\nFor a merge this normally means the branches conflict, the pull request is already ")
            .Append("merged or declined, or the destination branch moved since it was opened. Re-read it with ")
            .Append("getPullRequest, resolve the conflict in git and push, then merge again. Retrying the same ")
            .Append("merge without changing anything will conflict again.");

        return message.ToString();
    }

    private static string RateLimited(BitbucketApiException exception, string? detail)
    {
        var message = new StringBuilder("Bitbucket rate-limited this request (429 Too Many Requests).");
        Append(message, detail);

        if (exception.RetryAttempts > 0)
        {
            message.Append(CultureInfo.InvariantCulture,
                $"\nIt was already retried {exception.RetryAttempts} time(s) with backoff, honouring Retry-After, and was still throttled.");
        }

        message
            .Append("\nWait about a minute before calling again, and cut the request rate: ask for fewer items ")
            .Append("per page, and fetch diffs per file (mode=\"diffstat\" then paths=[...]) instead of whole ")
            .Append("pull requests.");

        return message.ToString();
    }

    private static string DiffTooLarge(string? detail)
    {
        var message = new StringBuilder("Bitbucket refused to build this diff because it is too large (555).");
        Append(message, detail);

        message
            .Append("\nDiff too large; use mode=diffstat then paths=[...].")
            .Append("\nCall getPullRequestDiff with mode=\"diffstat\" to list the changed files, then call it ")
            .Append("again with mode=\"diff\" and paths=[\"...\"] naming only the files you need. The threshold ")
            .Append("is around 8,000 changed lines or 200 files, and no amount of retrying moves it.");

        return message.ToString();
    }

    /// <summary>
    /// A merge Bitbucket queued instead of performing. The client already composed the advice (it
    /// is the only place the task handle is visible), so this branch exists to keep the status out
    /// of the generic 2xx-shaped hole rather than to add anything.
    /// </summary>
    private static string Queued(string? detail) =>
        detail ?? "Bitbucket queued this merge as a background task instead of merging synchronously. " +
        "This server does not poll merge tasks: check the pull request in the Bitbucket UI, and only " +
        "re-run the merge if it did not complete.";

    private static string ServerError(BitbucketApiException exception, int status, string? detail)
    {
        var message = new StringBuilder();
        message.Append(CultureInfo.InvariantCulture, $"Bitbucket failed on its own side (HTTP {status}).");
        Append(message, detail);

        message.Append(exception.RetryAttempts > 0
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"\nThe request was already retried {exception.RetryAttempts} time(s) with backoff, so this is not a single unlucky call.")
            : "\nThis status is retried automatically when it is transient, so it arrived on the first attempt.");

        message
            .Append("\nNothing in the request needs changing. Try again in a few minutes, and check ")
            .Append("https://bitbucket.status.atlassian.com/ if it persists.");

        return message.ToString();
    }

    private static string Other(int status, string? detail)
    {
        var message = new StringBuilder();
        message.Append(CultureInfo.InvariantCulture, $"Bitbucket returned an unexpected HTTP {status}.");
        Append(message, detail);
        return message.ToString();
    }

    /// <summary>Bitbucket's own words about the failure, when it supplied any.</summary>
    private static string? Detail(BitbucketApiException exception)
    {
        var error = exception.Error?.Error;

        if (!string.IsNullOrWhiteSpace(error?.Message))
        {
            return Combine(error.Message.Trim(), error.Detail);
        }

        return string.IsNullOrWhiteSpace(error?.Detail) ? null : error.Detail.Trim();
    }

    private static string Combine(string message, string? detail) =>
        string.IsNullOrWhiteSpace(detail) || message.Contains(detail.Trim(), StringComparison.Ordinal)
            ? message
            : message + " " + detail.Trim();

    private static void Append(StringBuilder message, string? detail)
    {
        if (!string.IsNullOrWhiteSpace(detail))
        {
            message.Append("\nBitbucket said: ").Append(detail);
        }
    }

    /// <summary>Names the thing the call was addressing, for the 403 and 404 messages.</summary>
    private static string Target(ToolCallContext context)
    {
        var workspace = string.IsNullOrWhiteSpace(context.Workspace) ? "?" : context.Workspace;
        var repository = string.IsNullOrWhiteSpace(context.Repository) ? "?" : context.Repository;

        return context.PullRequestId is { } id
            ? string.Create(CultureInfo.InvariantCulture, $"pull request #{id} in {workspace}/{repository}")
            : string.Create(CultureInfo.InvariantCulture, $"repository {workspace}/{repository}");
    }

    // -------------------------------------------------------------------------------------------
    // Everything else
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// The catch-all. The type name goes in the message because it is the one detail that makes an
    /// unanticipated failure reportable; the stack trace goes to stderr at Debug, where it can be
    /// turned on with <c>BITBUCKET_MCP_LOG_LEVEL=Debug</c> without polluting the model's context.
    /// </summary>
    private static McpException Unexpected(Exception exception, ToolCallContext context)
    {
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                exception,
                "Tool {Tool} failed unexpectedly for {Workspace}/{Repository} #{PullRequestId}",
                context.Tool,
                context.Workspace,
                context.Repository,
                context.PullRequestId);
        }

        return new McpException(
            $"Unexpected error: {exception.GetType().Name}: {exception.Message}", exception);
    }
}
