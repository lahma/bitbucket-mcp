using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using Bitbucket.Mcp.Authentication;
using Bitbucket.Mcp.Http.Models;

using Microsoft.Extensions.Logging;

namespace Bitbucket.Mcp.Http;

/// <summary>
/// The only thing in this server that talks to Bitbucket. One instance, one
/// <see cref="HttpClient"/>, one hand-built handler chain (D5 — no
/// <c>IHttpClientFactory</c>, which exists to manage many named clients we do not have).
/// </summary>
/// <remarks>
/// <para>
/// The chain is <see cref="AuthenticationHandler"/> → <see cref="RetryHandler"/> →
/// <see cref="SocketsHttpHandler"/>, built here rather than by DI so that the ordering — which is
/// load-bearing for the 401 refresh and for redirect following, both of which happen in the
/// outermost handler — is visible in one place.
/// </para>
/// <para>
/// Every response is deserialised straight from the response stream through
/// <see cref="BitbucketWireJsonContext"/>, and every request body is serialised to UTF-8 bytes
/// through the same context. No intermediate JSON strings exist anywhere on the success path; the
/// one place a body becomes a string is a failed request, where the raw text is what makes the
/// error diagnosable.
/// </para>
/// <para>
/// Failures: a non-2xx response becomes a <see cref="BitbucketApiException"/> carrying the status
/// (including Bitbucket's non-standard <c>555</c> for an oversized diff), the parsed error envelope
/// when the body was JSON, the raw body capped at 16 KiB, and how many retries the request already
/// cost. Transport failures that outlived the retry budget propagate as
/// <see cref="HttpRequestException"/>, and a missing or unusable credential as
/// <see cref="AuthenticationRequiredException"/> — both unwrapped, so the tool-layer funnel can
/// tell them apart.
/// </para>
/// </remarks>
internal sealed class BitbucketApiClient : IDisposable
{
    /// <summary>The Bitbucket Cloud REST API 2.0 root. Every relative URL is resolved against it.</summary>
    internal static readonly Uri DefaultBaseAddress = new("https://api.bitbucket.org/2.0/");

    /// <summary>
    /// Whole-request timeout. Generous because a large diffstat behind a redirect is genuinely slow;
    /// the connect phase is bounded far more tightly by the transport.
    /// </summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(100);

    /// <summary>Bitbucket's hard ceiling on <c>pagelen</c>.</summary>
    private const int MaxPageSize = 100;

    private const string ProjectUrl = "https://github.com/lahma/bitbucket-mcp";

    /// <summary>
    /// The merge response's field list: the pull request detail plus <c>task_status</c>. An
    /// inclusive <c>fields=</c> list returns only what it names, and a queued merge (<c>202</c>)
    /// answers with nothing but <c>task_status</c> — omitting it would leave the "merge was queued"
    /// error with nothing to quote.
    /// </summary>
    private const string MergeFields = FieldSets.PullRequestDetail + ",task_status";

    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;

    /// <summary>
    /// The same clock <see cref="RetryHandler"/> uses, kept because an HTTP-date <c>Retry-After</c>
    /// on the response that finally failed is only a number of seconds relative to "now".
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// The production constructor: builds its own transport. This is the one the container binds —
    /// the test constructor below cannot be satisfied from the service collection because
    /// <see cref="HttpMessageHandler"/> is not registered.
    /// </summary>
    internal BitbucketApiClient(ICredentialProvider credentialProvider, ILoggerFactory loggerFactory)
        : this(credentialProvider, loggerFactory, CreateTransport(), baseAddress: null, timeProvider: null)
    {
    }

    /// <summary>
    /// The test constructor: takes the innermost handler and, optionally, a different base address
    /// and clock.
    /// </summary>
    /// <param name="credentialProvider">Supplies the <c>Authorization</c> header per request.</param>
    /// <param name="loggerFactory">Source of the handlers' loggers.</param>
    /// <param name="transport">
    /// The innermost handler. Disposed with this client. In production this is a
    /// <see cref="SocketsHttpHandler"/>; in tests, a stub.
    /// </param>
    /// <param name="baseAddress">
    /// Overrides <see cref="DefaultBaseAddress"/>. Note that pagination cursors are validated
    /// against the real API host regardless (see <see cref="BitbucketCursor"/>), so fixtures should
    /// still use <c>https://api.bitbucket.org/2.0/…</c> in their <c>next</c> links.
    /// </param>
    /// <param name="timeProvider">Clock and delay source for <see cref="RetryHandler"/>.</param>
    internal BitbucketApiClient(
        ICredentialProvider credentialProvider,
        ILoggerFactory loggerFactory,
        HttpMessageHandler transport,
        Uri? baseAddress = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(credentialProvider);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(transport);

        _logger = loggerFactory.CreateLogger<BitbucketApiClient>();
        _timeProvider = timeProvider ?? TimeProvider.System;

        var retry = new RetryHandler(loggerFactory.CreateLogger<RetryHandler>(), timeProvider)
        {
            InnerHandler = transport,
        };

        var authentication = new AuthenticationHandler(credentialProvider, loggerFactory.CreateLogger<AuthenticationHandler>())
        {
            InnerHandler = retry,
        };

        _httpClient = new HttpClient(authentication, disposeHandler: true)
        {
            BaseAddress = baseAddress ?? DefaultBaseAddress,
            Timeout = RequestTimeout,
        };

        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // TryParseAdd rather than Add: a malformed User-Agent is not worth failing every request
        // over, and the version string comes from an assembly attribute we do not fully control.
        _ = _httpClient.DefaultRequestHeaders.UserAgent.TryParseAdd(
            $"{ServerVersion.Name}/{ServerVersion.Value} (+{ProjectUrl})");

        // Deliberately absent: DefaultRequestHeaders.Authorization. See AuthenticationHandler.
    }

    /// <summary>
    /// Lists pull requests, newest activity first unless <paramref name="sort"/> says otherwise.
    /// </summary>
    /// <param name="workspace">Workspace URL segment.</param>
    /// <param name="repositorySlug">Repository slug.</param>
    /// <param name="states">
    /// States to include (<c>OPEN</c>, <c>MERGED</c>, <c>DECLINED</c>, <c>SUPERSEDED</c>). Empty or
    /// <see langword="null"/> leaves Bitbucket's default, which is open pull requests only.
    /// </param>
    /// <param name="author">
    /// Restricts to one author. A value in Bitbucket's braced UUID form is matched against
    /// <c>author.uuid</c>; anything else against <c>author.nickname</c>.
    /// </param>
    /// <param name="query">An extra BBQL fragment, combined with the rest using <c>AND</c>.</param>
    /// <param name="sort">A sort field, for example <c>-updated_on</c>.</param>
    /// <param name="pageSize">Items per page; clamped to 1–100.</param>
    /// <param name="cursor">
    /// A cursor from a previous page. When present every other filter argument is ignored — the
    /// cursor already encodes the original query.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <exception cref="InvalidCursorException"><paramref name="cursor"/> did not decode to an API URL.</exception>
    internal async Task<Page<PullRequestSummaryDto>> ListPullRequestsAsync(
        string workspace,
        string repositorySlug,
        IReadOnlyList<string>? states = null,
        string? author = null,
        string? query = null,
        string? sort = null,
        int? pageSize = null,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        var url = cursor is not null
            ? DecodeCursor(cursor)
            : BitbucketRequestBuilder.Repository(workspace, repositorySlug)
                .Segment("pullrequests")
                .Query("fields", FieldSets.PullRequestList)
                .Query("q", BuildPullRequestQuery(states, author, query))
                .Query("sort", sort)
                .Query("pagelen", ClampPageSize(pageSize))
                .Build();

        return await GetPageAsync(url, BitbucketWireJsonContext.Default.PullRequestSummaryPage, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Fetches one pull request in full.</summary>
    internal async Task<PullRequestDto> GetPullRequestAsync(
        string workspace,
        string repositorySlug,
        int pullRequestId,
        CancellationToken cancellationToken = default)
    {
        var url = PullRequest(workspace, repositorySlug, pullRequestId)
            .Query("fields", FieldSets.PullRequestDetail)
            .Build();

        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        return await SendJsonAsync(request, BitbucketWireJsonContext.Default.PullRequestDto, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Lists the files a pull request touches, with per-file line counts. Always the first call of
    /// a diff workflow: it is paginated, cheap, and still works on pull requests whose full diff
    /// answers <c>555</c>.
    /// </summary>
    /// <exception cref="InvalidCursorException"><paramref name="cursor"/> did not decode to an API URL.</exception>
    internal async Task<Page<DiffStatEntryDto>> GetDiffStatAsync(
        string workspace,
        string repositorySlug,
        int pullRequestId,
        int? pageSize = null,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        var url = cursor is not null
            ? DecodeCursor(cursor)
            : PullRequest(workspace, repositorySlug, pullRequestId)
                .Segment("diffstat")
                .Query("fields", FieldSets.DiffStat)
                .Query("pagelen", ClampPageSize(pageSize))
                .Build();

        return await GetPageAsync(url, BitbucketWireJsonContext.Default.DiffStatEntryPage, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Fetches the raw unified diff, optionally narrowed to specific files.
    /// </summary>
    /// <param name="workspace">Workspace URL segment.</param>
    /// <param name="repositorySlug">Repository slug.</param>
    /// <param name="pullRequestId">The pull request number.</param>
    /// <param name="paths">
    /// Repository-relative paths, exactly as diffstat reported them. Each becomes its own
    /// <c>path=</c> parameter. Empty or <see langword="null"/> asks for the whole diff, which is
    /// what returns <c>555</c> on a large pull request — hence the diffstat-first rule.
    /// </param>
    /// <param name="contextLines">Context lines around each hunk.</param>
    /// <param name="ignoreWhitespace">Whether to ignore whitespace-only changes.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The diff as text; never JSON.</returns>
    internal async Task<string> GetDiffAsync(
        string workspace,
        string repositorySlug,
        int pullRequestId,
        IReadOnlyList<string>? paths = null,
        int? contextLines = null,
        bool? ignoreWhitespace = null,
        CancellationToken cancellationToken = default)
    {
        var url = PullRequest(workspace, repositorySlug, pullRequestId)
            .Segment("diff")
            .QueryEach("path", paths)
            .Query("context", contextLines)
            .Query("ignore_whitespace", ignoreWhitespace)
            .Build();

        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        // Overrides the client-wide JSON default: this endpoint answers with text, after a 302 to
        // another path on the API host. A request-level Accept header suppresses the default one
        // entirely, and AuthenticationHandler carries it across the redirect.
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));

        return await SendTextAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Lists a pull request's comments, general and inline, including deleted ones — Bitbucket
    /// returns those with blanked content to keep threads intact, and the caller filters them.
    /// </summary>
    /// <exception cref="InvalidCursorException"><paramref name="cursor"/> did not decode to an API URL.</exception>
    internal async Task<Page<CommentDto>> GetCommentsAsync(
        string workspace,
        string repositorySlug,
        int pullRequestId,
        int? pageSize = null,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        var url = cursor is not null
            ? DecodeCursor(cursor)
            : PullRequest(workspace, repositorySlug, pullRequestId)
                .Segment("comments")
                .Query("fields", FieldSets.Comments)
                .Query("pagelen", ClampPageSize(pageSize))
                .Build();

        return await GetPageAsync(url, BitbucketWireJsonContext.Default.CommentPage, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Opens a pull request.</summary>
    internal async Task<PullRequestDto> CreatePullRequestAsync(
        string workspace,
        string repositorySlug,
        CreatePullRequestRequest body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);

        var url = BitbucketRequestBuilder.Repository(workspace, repositorySlug)
            .Segment("pullrequests")
            .Query("fields", FieldSets.PullRequestDetail)
            .Build();

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonBody(body, BitbucketWireJsonContext.Default.CreatePullRequestRequest),
        };

        return await SendJsonAsync(request, BitbucketWireJsonContext.Default.PullRequestDto, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Partially updates a pull request. Fields left unset on <paramref name="body"/> are omitted
    /// from the request and keep their current values — except <c>reviewers</c>, which replaces the
    /// whole list when sent.
    /// </summary>
    internal async Task<PullRequestDto> UpdatePullRequestAsync(
        string workspace,
        string repositorySlug,
        int pullRequestId,
        UpdatePullRequestRequest body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);

        var url = PullRequest(workspace, repositorySlug, pullRequestId)
            .Query("fields", FieldSets.PullRequestDetail)
            .Build();

        using var request = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = JsonBody(body, BitbucketWireJsonContext.Default.UpdatePullRequestRequest),
        };

        return await SendJsonAsync(request, BitbucketWireJsonContext.Default.PullRequestDto, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Posts a comment — general, inline, or a reply, depending on <paramref name="body"/>.</summary>
    internal async Task<CommentDto> AddCommentAsync(
        string workspace,
        string repositorySlug,
        int pullRequestId,
        CommentRequest body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);

        var url = PullRequest(workspace, repositorySlug, pullRequestId)
            .Segment("comments")
            .Query("fields", FieldSets.Comment)
            .Build();

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonBody(body, BitbucketWireJsonContext.Default.CommentRequest),
        };

        return await SendJsonAsync(request, BitbucketWireJsonContext.Default.CommentDto, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Approves the pull request as the authenticated user.</summary>
    /// <returns>The caller's own participant entry, which is what this endpoint answers with.</returns>
    internal Task<ParticipantDto> ApproveAsync(
        string workspace,
        string repositorySlug,
        int pullRequestId,
        CancellationToken cancellationToken = default) =>
        PostReviewStateAsync(workspace, repositorySlug, pullRequestId, "approve", cancellationToken);

    /// <summary>Withdraws the authenticated user's approval. Succeeds silently if there was none.</summary>
    internal Task UnapproveAsync(
        string workspace,
        string repositorySlug,
        int pullRequestId,
        CancellationToken cancellationToken = default) =>
        DeleteReviewStateAsync(workspace, repositorySlug, pullRequestId, "approve", cancellationToken);

    /// <summary>Requests changes as the authenticated user.</summary>
    /// <returns>The caller's own participant entry.</returns>
    internal Task<ParticipantDto> RequestChangesAsync(
        string workspace,
        string repositorySlug,
        int pullRequestId,
        CancellationToken cancellationToken = default) =>
        PostReviewStateAsync(workspace, repositorySlug, pullRequestId, "request-changes", cancellationToken);

    /// <summary>Withdraws a change request. Succeeds silently if there was none.</summary>
    internal Task UnrequestChangesAsync(
        string workspace,
        string repositorySlug,
        int pullRequestId,
        CancellationToken cancellationToken = default) =>
        DeleteReviewStateAsync(workspace, repositorySlug, pullRequestId, "request-changes", cancellationToken);

    /// <summary>
    /// Merges the pull request synchronously.
    /// </summary>
    /// <remarks>
    /// Bitbucket may decide to queue a large merge instead, answering <c>202</c> with a task handle.
    /// Polling that task is out of scope (it can take minutes, and a tool call that blocks that long
    /// is worse than an honest error), so a queued merge is reported as a
    /// <see cref="BitbucketApiException"/> with status <c>202</c> telling the user to check the
    /// Bitbucket UI rather than re-running the merge blindly.
    /// </remarks>
    internal async Task<PullRequestDto> MergeAsync(
        string workspace,
        string repositorySlug,
        int pullRequestId,
        MergeRequest body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);

        var url = PullRequest(workspace, repositorySlug, pullRequestId)
            .Segment("merge")
            .Query("fields", MergeFields)
            .Build();

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonBody(body, BitbucketWireJsonContext.Default.MergeRequest),
        };

        var attempts = new RetryAttemptCounter();

        using var response = await SendAsync(request, attempts, cancellationToken).ConfigureAwait(false);
        await ThrowIfNotSuccessAsync(response, attempts.Value, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Accepted)
        {
            throw await CreateQueuedMergeExceptionAsync(response, attempts.Value, cancellationToken).ConfigureAwait(false);
        }

        return await ReadJsonAsync(response, BitbucketWireJsonContext.Default.PullRequestDto, attempts.Value, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Declines the pull request.</summary>
    /// <param name="workspace">Workspace URL segment.</param>
    /// <param name="repositorySlug">Repository slug.</param>
    /// <param name="pullRequestId">The pull request number.</param>
    /// <param name="reason">
    /// Sent best effort as the pull request's <c>reason</c>; Bitbucket documents no request body
    /// for this endpoint, so callers must not depend on it round-tripping.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal async Task<PullRequestDto> DeclineAsync(
        string workspace,
        string repositorySlug,
        int pullRequestId,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var url = PullRequest(workspace, repositorySlug, pullRequestId)
            .Segment("decline")
            .Query("fields", FieldSets.PullRequestDetail)
            .Build();

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonBody(new DeclineRequest { Reason = reason }, BitbucketWireJsonContext.Default.DeclineRequest),
        };

        return await SendJsonAsync(request, BitbucketWireJsonContext.Default.PullRequestDto, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose() => _httpClient.Dispose();

    private static SocketsHttpHandler CreateTransport() => new()
    {
        // Redirect following lives in AuthenticationHandler instead (D16). Bitbucket answers the
        // diff and diffstat endpoints with a 302 to another path on api.bitbucket.org whose target
        // still requires the credential, and SocketsHttpHandler strips the Authorization header on
        // *every* automatic redirect — same-origin ones included. Following them here would send
        // the second request unauthenticated, which Bitbucket answers with a 404.
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,

        // Bounded so a long-lived server eventually notices DNS changes and load-balancer moves.
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),

        // Far tighter than the overall timeout: a connect that has not happened in 15 s will not.
        ConnectTimeout = TimeSpan.FromSeconds(15),
    };

    private static BitbucketRequestBuilder PullRequest(string workspace, string repositorySlug, int pullRequestId) =>
        BitbucketRequestBuilder.Repository(workspace, repositorySlug)
            .Segment("pullrequests")
            .Segment(pullRequestId);

    private async Task<ParticipantDto> PostReviewStateAsync(
        string workspace,
        string repositorySlug,
        int pullRequestId,
        string segment,
        CancellationToken cancellationToken)
    {
        var url = PullRequest(workspace, repositorySlug, pullRequestId)
            .Segment(segment)
            .Query("fields", FieldSets.Participant)
            .Build();

        // No body at all: HttpClient still writes Content-Length: 0 for a POST, which is what the
        // endpoint expects, and a null content is trivially re-sendable on a 401 or a 429.
        using var request = new HttpRequestMessage(HttpMethod.Post, url);

        return await SendJsonAsync(request, BitbucketWireJsonContext.Default.ParticipantDto, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task DeleteReviewStateAsync(
        string workspace,
        string repositorySlug,
        int pullRequestId,
        string segment,
        CancellationToken cancellationToken)
    {
        var url = PullRequest(workspace, repositorySlug, pullRequestId)
            .Segment(segment)
            .Build();

        using var request = new HttpRequestMessage(HttpMethod.Delete, url);

        await SendNoContentAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Composes the BBQL <c>q=</c> filter. Values are quoted and escaped;
    /// <paramref name="extra"/> is the caller's own fragment and is parenthesised so that its
    /// operator precedence cannot silently rearrange the rest.
    /// </summary>
    private static string? BuildPullRequestQuery(IReadOnlyList<string>? states, string? author, string? extra)
    {
        var clauses = new List<string>(3);

        if (states is not null)
        {
            var stateClauses = new List<string>(states.Count);

            foreach (var state in states)
            {
                if (!string.IsNullOrWhiteSpace(state))
                {
                    stateClauses.Add($"state = {Quote(state.Trim().ToUpperInvariant())}");
                }
            }

            if (stateClauses.Count == 1)
            {
                clauses.Add(stateClauses[0]);
            }
            else if (stateClauses.Count > 1)
            {
                clauses.Add($"({string.Join(" OR ", stateClauses)})");
            }
        }

        if (!string.IsNullOrWhiteSpace(author))
        {
            var trimmed = author.Trim();

            // Bitbucket account UUIDs are returned — and matched — in braced form. Anything else is
            // taken as a nickname, which is the only other identifier BBQL exposes here.
            var field = trimmed.StartsWith('{') ? "author.uuid" : "author.nickname";
            clauses.Add($"{field} = {Quote(trimmed)}");
        }

        if (!string.IsNullOrWhiteSpace(extra))
        {
            clauses.Add($"({extra.Trim()})");
        }

        return clauses.Count == 0 ? null : string.Join(" AND ", clauses);
    }

    /// <summary>Wraps a value in BBQL's double quotes, escaping backslashes and quotes.</summary>
    private static string Quote(string value) =>
        $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    private static int? ClampPageSize(int? pageSize) =>
        pageSize is null ? null : Math.Clamp(pageSize.GetValueOrDefault(), 1, MaxPageSize);

    private static string DecodeCursor(string cursor) =>
        BitbucketCursor.TryDecode(cursor, out var url) ? url : throw new InvalidCursorException(cursor);

    private static ByteArrayContent JsonBody<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        // Serialised to bytes rather than streamed: ByteArrayContent can be replayed, and that is
        // precisely what makes a write retryable after a 429 or a 401 credential refresh.
        var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(value, typeInfo));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        return content;
    }

    /// <summary>Synthesises an error envelope for a failure Bitbucket did not describe itself.</summary>
    private static ErrorEnvelopeDto SyntheticError(string message) => new()
    {
        Type = "error",
        Error = new ErrorDetailDto { Message = message },
    };

    private async Task<Page<T>> GetPageAsync<T>(
        string url,
        JsonTypeInfo<PageEnvelope<T>> typeInfo,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        var envelope = await SendJsonAsync(request, typeInfo, cancellationToken).ConfigureAwait(false);

        return new Page<T>(envelope.Values ?? [], BitbucketCursor.Encode(envelope.Next), envelope.Size);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        RetryAttemptCounter attempts,
        CancellationToken cancellationToken)
    {
        request.Options.Set(RetryHandler.RetryAttemptsKey, attempts);

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Bitbucket {Method} {Url}", request.Method.Method, request.RequestUri);
        }

        // ResponseHeadersRead so that a multi-megabyte diff is streamed rather than buffered before
        // the status code is even looked at.
        return await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<T> SendJsonAsync<T>(
        HttpRequestMessage request,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        var attempts = new RetryAttemptCounter();

        using var response = await SendAsync(request, attempts, cancellationToken).ConfigureAwait(false);
        await ThrowIfNotSuccessAsync(response, attempts.Value, cancellationToken).ConfigureAwait(false);

        return await ReadJsonAsync(response, typeInfo, attempts.Value, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> SendTextAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var attempts = new RetryAttemptCounter();

        using var response = await SendAsync(request, attempts, cancellationToken).ConfigureAwait(false);
        await ThrowIfNotSuccessAsync(response, attempts.Value, cancellationToken).ConfigureAwait(false);

        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task SendNoContentAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var attempts = new RetryAttemptCounter();

        using var response = await SendAsync(request, attempts, cancellationToken).ConfigureAwait(false);
        await ThrowIfNotSuccessAsync(response, attempts.Value, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<T> ReadJsonAsync<T>(
        HttpResponseMessage response,
        JsonTypeInfo<T> typeInfo,
        int retryAttempts,
        CancellationToken cancellationToken)
    {
        T? value;

        try
        {
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            value = await JsonSerializer.DeserializeAsync(stream, typeInfo, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new BitbucketApiException(
                response.StatusCode,
                SyntheticError("Bitbucket returned a success status with a body this server could not parse."),
                rawBody: null,
                retryAttempts,
                retryAfterSeconds: null,
                ex);
        }

        return value ?? throw new BitbucketApiException(
            response.StatusCode,
            SyntheticError("Bitbucket returned a success status with an empty body."),
            rawBody: null,
            retryAttempts);
    }

    /// <summary>
    /// Turns a non-2xx response into a <see cref="BitbucketApiException"/>. Bitbucket's <c>555</c>
    /// arrives here like any other status and keeps its numeric value.
    /// </summary>
    /// <remarks>
    /// The <c>Retry-After</c> read here is the one on the response that finally failed — the retry
    /// handler has already honoured (or declined) the earlier ones, so this is the wait the caller
    /// still has ahead of it.
    /// </remarks>
    private async Task ThrowIfNotSuccessAsync(
        HttpResponseMessage response,
        int retryAttempts,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var rawBody = await ReadBodySnippetAsync(response, cancellationToken).ConfigureAwait(false);

        throw new BitbucketApiException(
            response.StatusCode,
            TryParseError(rawBody),
            rawBody,
            retryAttempts,
            RetryHandler.RetryAfterSeconds(response, _timeProvider));
    }

    private static ErrorEnvelopeDto? TryParseError(string rawBody)
    {
        if (string.IsNullOrWhiteSpace(rawBody))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(rawBody, BitbucketWireJsonContext.Default.ErrorEnvelopeDto);
        }
        catch (JsonException)
        {
            // Best effort by contract: a bare 555, an HTML maintenance page or a proxy's error body
            // is not JSON, and the raw text travels on the exception regardless.
            return null;
        }
    }

    private static async Task<BitbucketApiException> CreateQueuedMergeExceptionAsync(
        HttpResponseMessage response,
        int retryAttempts,
        CancellationToken cancellationToken)
    {
        var rawBody = await ReadBodySnippetAsync(response, cancellationToken).ConfigureAwait(false);
        string? taskStatus = null;

        if (!string.IsNullOrWhiteSpace(rawBody))
        {
            try
            {
                taskStatus = JsonSerializer
                    .Deserialize(rawBody, BitbucketWireJsonContext.Default.MergeTaskStatusDto)?.TaskStatus;
            }
            catch (JsonException)
            {
                // The status is a nicety in the message; its absence changes no advice.
            }
        }

        var status = string.IsNullOrWhiteSpace(taskStatus) ? string.Empty : $" (task status {taskStatus})";

        var message =
            $"Bitbucket queued this merge as a background task{status} instead of merging synchronously. " +
            "This server does not poll merge tasks: open the pull request in the Bitbucket UI to see whether " +
            "the merge completed, and only re-run the merge if it did not.";

        return new BitbucketApiException(response.StatusCode, SyntheticError(message), rawBody, retryAttempts);
    }

    /// <summary>
    /// Reads at most <see cref="BitbucketApiException.MaxRawBodyLength"/> bytes of a failed
    /// response. Bounded because this text ends up in a log line and in the model's context, and
    /// unbounded because of a failure is how a bad day becomes an expensive one.
    /// </summary>
    private static async Task<string> ReadBodySnippetAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var buffer = new byte[BitbucketApiException.MaxRawBodyLength];

            var read = await stream
                .ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken)
                .ConfigureAwait(false);

            return Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch (HttpRequestException)
        {
            // The status code is the useful part of a failure; losing the body to a broken
            // connection must not replace it with a different, less informative exception.
            return string.Empty;
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }
}
