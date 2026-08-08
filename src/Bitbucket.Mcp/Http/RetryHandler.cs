using System.Globalization;
using System.Net;

using Microsoft.Extensions.Logging;

namespace Bitbucket.Mcp.Http;

/// <summary>
/// A mutable counter a caller can attach to a request (via
/// <see cref="RetryHandler.RetryAttemptsKey"/>) to learn afterwards how many retries the request
/// cost.
/// </summary>
/// <remarks>
/// It exists because the retry decision lives in the handler while the exception that has to
/// report it (<see cref="BitbucketApiException.RetryAttempts"/>) is thrown by the client, two
/// layers up. Passing it through <see cref="HttpRequestMessage.Options"/> keeps the handler
/// stateless — one shared handler serves every concurrent request, so nothing per-request may be
/// stored on the handler itself.
/// </remarks>
internal sealed class RetryAttemptCounter
{
    /// <summary>How many times the request was reissued. Zero means it succeeded or failed first try.</summary>
    internal int Value { get; private set; }

    /// <summary>Records one more retry.</summary>
    internal void Increment() => Value++;
}

/// <summary>
/// Retries the handful of Bitbucket failures that are genuinely worth retrying, and nothing else
/// (D4 — hand-rolled, because the resilience package would drag in Polly and six more
/// dependencies).
/// </summary>
/// <remarks>
/// <para>
/// Retried: <c>408</c>, <c>429</c>, <c>502</c>, <c>503</c>, <c>504</c>, plus transport failures
/// (<see cref="HttpRequestException"/>, <see cref="IOException"/>). Everything else — every other
/// 4xx, Bitbucket's <c>555</c>, a malformed body — is a deterministic answer, and reissuing it only
/// wastes rate-limit budget.
/// </para>
/// <para>
/// Two conditions bound the retry. A request may only be reissued if its content can be sent again
/// (see <see cref="IsResendable"/>); and the response body is never read here, so a retry can never
/// follow a partially consumed stream — the response is disposed before the next attempt.
/// </para>
/// <para>
/// <c>Retry-After</c> wins over the backoff schedule when Bitbucket sends one, but only up to
/// <see cref="MaxRetryAfter"/>. A longer wait than that is not something to sit on inside a tool
/// call: the response is returned as-is so the caller can tell the user how long the rate limit
/// has left.
/// </para>
/// </remarks>
internal sealed class RetryHandler : DelegatingHandler
{
    /// <summary>Total attempts including the first, so at most three retries.</summary>
    internal const int MaxAttempts = 4;

    /// <summary>Below this many remaining requests the rate-limit headers are logged as a warning.</summary>
    internal const int LowRateLimitRemaining = 50;

    /// <summary>Longest <c>Retry-After</c> that is worth waiting for; beyond it the caller is told instead.</summary>
    internal static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Option slot for a <see cref="RetryAttemptCounter"/>. Attaching one is optional; when absent
    /// the handler simply does not report.
    /// </summary>
    internal static readonly HttpRequestOptionsKey<RetryAttemptCounter> RetryAttemptsKey =
        new("bitbucket-mcp.retry-attempts");

    /// <summary>First backoff step; doubled per attempt.</summary>
    private static readonly TimeSpan BaseDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>Ceiling on the exponential backoff, before jitter.</summary>
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(20);

    /// <summary>Fraction of the computed delay the jitter may add or subtract.</summary>
    private const double JitterFraction = 0.25;

    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;

    /// <param name="logger">Where the rate-limit and retry lines go. Never stdout.</param>
    /// <param name="timeProvider">
    /// Clock and delay source. Injected so tests can exercise the backoff schedule without spending
    /// real seconds; <see langword="null"/> means <see cref="TimeProvider.System"/>.
    /// </param>
    internal RetryHandler(ILogger logger, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Whether a request carrying this content can be sent a second time.
    /// </summary>
    /// <remarks>
    /// Buffered content types can be replayed from memory; a stream-backed body generally cannot,
    /// because the first attempt has already drained it. This is also what
    /// <see cref="AuthenticationHandler"/> consults before its one 401 retry — the definition of
    /// "re-sendable" belongs to whoever re-sends, and both handlers share it.
    /// </remarks>
    internal static bool IsResendable(HttpContent? content) =>
        content is null or ByteArrayContent or ReadOnlyMemoryContent;

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        request.Options.TryGetValue(RetryAttemptsKey, out var counter);

        var resendable = IsResendable(request.Content);

        for (var attempt = 0; ; attempt++)
        {
            // Decided before sending so that the last attempt's failure propagates from its own
            // catch clause with the original stack trace instead of being captured and rethrown.
            var lastAttempt = !resendable || attempt + 1 >= MaxAttempts;

            HttpResponseMessage response;

            try
            {
                response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (!lastAttempt && !cancellationToken.IsCancellationRequested)
            {
                await DelayAfterTransportFailureAsync(ex, attempt, counter, cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch (IOException ex) when (!lastAttempt && !cancellationToken.IsCancellationRequested)
            {
                await DelayAfterTransportFailureAsync(ex, attempt, counter, cancellationToken).ConfigureAwait(false);
                continue;
            }

            LogRateLimitHeaders(response);

            if (lastAttempt || !IsRetryableStatus(response.StatusCode))
            {
                return response;
            }

            var delay = NextDelay(response, attempt);

            if (delay is null)
            {
                // Retry-After is longer than we are willing to block a tool call for.
                return response;
            }

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    "Bitbucket returned HTTP {StatusCode}; retrying attempt {Attempt} of {MaxAttempts} in {Delay}.",
                    (int) response.StatusCode,
                    attempt + 2,
                    MaxAttempts,
                    delay.GetValueOrDefault());
            }

            // Nothing has read the body, so disposing here cannot discard a partially consumed
            // stream — which is exactly why a retry is safe at this point.
            response.Dispose();
            counter?.Increment();

            await Task.Delay(delay.GetValueOrDefault(), _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task DelayAfterTransportFailureAsync(
        Exception failure,
        int attempt,
        RetryAttemptCounter? counter,
        CancellationToken cancellationToken)
    {
        var delay = Backoff(attempt);

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                failure,
                "Bitbucket request failed before a response arrived; retrying attempt {Attempt} of {MaxAttempts} in {Delay}.",
                attempt + 2,
                MaxAttempts,
                delay);
        }

        counter?.Increment();

        await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The statuses that are worth a second try; everything else is a real answer.</summary>
    private static bool IsRetryableStatus(HttpStatusCode statusCode) => statusCode
        is HttpStatusCode.RequestTimeout        // 408
        or HttpStatusCode.TooManyRequests       // 429
        or HttpStatusCode.BadGateway            // 502
        or HttpStatusCode.ServiceUnavailable    // 503
        or HttpStatusCode.GatewayTimeout;       // 504

    /// <summary>
    /// How long to wait before the next attempt, or <see langword="null"/> to give up now because
    /// Bitbucket asked for longer than <see cref="MaxRetryAfter"/>.
    /// </summary>
    private TimeSpan? NextDelay(HttpResponseMessage response, int attempt)
    {
        if (!TryGetRetryAfter(response, _timeProvider, out var retryAfter))
        {
            return Backoff(attempt);
        }

        if (retryAfter > MaxRetryAfter)
        {
            return null;
        }

        return retryAfter < TimeSpan.Zero ? TimeSpan.Zero : retryAfter;
    }

    /// <summary>
    /// Reads <c>Retry-After</c> in either documented form: delta-seconds, or an HTTP-date that has
    /// to be turned into a delta against the current time.
    /// </summary>
    /// <remarks>
    /// Shared rather than duplicated: <see cref="BitbucketApiClient"/> reads the same header off the
    /// final response so a 429 can tell the caller how long to wait, and two parsers of one header
    /// would eventually disagree about the HTTP-date form.
    /// </remarks>
    /// <param name="response">The response to read the header from.</param>
    /// <param name="timeProvider">The clock an HTTP-date is measured against.</param>
    /// <param name="value">The wait Bitbucket asked for, never negative.</param>
    internal static bool TryGetRetryAfter(HttpResponseMessage response, TimeProvider timeProvider, out TimeSpan value)
    {
        value = default;

        var header = response.Headers.RetryAfter;

        if (header is null)
        {
            return false;
        }

        if (header.Delta is { } delta)
        {
            value = delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
            return true;
        }

        if (header.Date is { } date)
        {
            var remaining = date - timeProvider.GetUtcNow();
            value = remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
            return true;
        }

        return false;
    }

    /// <summary>
    /// <c>Retry-After</c> as whole seconds, or <see langword="null"/> when the response carries none
    /// this handler understands.
    /// </summary>
    /// <remarks>
    /// Rounded up, so "wait 1.4 s" never becomes advice to come back in one; clamped at zero,
    /// because an HTTP-date already in the past means "now".
    /// </remarks>
    internal static int? RetryAfterSeconds(HttpResponseMessage response, TimeProvider timeProvider)
    {
        if (!TryGetRetryAfter(response, timeProvider, out var value))
        {
            return null;
        }

        var seconds = Math.Ceiling(value.TotalSeconds);
        return seconds >= int.MaxValue ? int.MaxValue : (int) Math.Max(seconds, 0);
    }

    /// <summary>
    /// <c>min(2^attempt · 500 ms, 20 s)</c> with ±25 % jitter, so that a burst of parallel tool
    /// calls that all hit the same 429 does not come back in lockstep.
    /// </summary>
    private static TimeSpan Backoff(int attempt)
    {
        var scaled = BaseDelay * Math.Pow(2, Math.Min(attempt, 16));

        if (scaled > MaxBackoff)
        {
            scaled = MaxBackoff;
        }

        var jitter = 1 + (((Random.Shared.NextDouble() * 2) - 1) * JitterFraction);
        return scaled * jitter;
    }

    /// <summary>
    /// Surfaces Bitbucket's rate-limit budget. Debug while there is headroom, warning once it is
    /// nearly gone — by which point the useful thing is knowing *why* the tools got slow.
    /// </summary>
    private void LogRateLimitHeaders(HttpResponseMessage response)
    {
        if (!TryGetHeader(response, "X-RateLimit-Remaining", out var remainingText)
            || !int.TryParse(remainingText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var remaining))
        {
            return;
        }

        var reset = TryGetHeader(response, "X-RateLimit-Reset", out var resetText) ? resetText : "(unknown)";

        if (remaining < LowRateLimitRemaining)
        {
            _logger.LogWarning(
                "Bitbucket rate limit is nearly exhausted: {Remaining} requests left, window resets at {Reset}.",
                remaining,
                reset);
        }
        else if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "Bitbucket rate limit: {Remaining} requests left, window resets at {Reset}.",
                remaining,
                reset);
        }
    }

    private static bool TryGetHeader(HttpResponseMessage response, string name, out string value)
    {
        if (response.Headers.TryGetValues(name, out var values))
        {
            foreach (var candidate in values)
            {
                value = candidate;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }
}
