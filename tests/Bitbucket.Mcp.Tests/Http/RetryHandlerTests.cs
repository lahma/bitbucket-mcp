using System.Globalization;
using System.Net;
using System.Text;

using Bitbucket.Mcp.Http;

using Microsoft.Extensions.Logging.Abstractions;


namespace Bitbucket.Mcp.Tests.Http;

/// <summary>
/// Covers <see cref="RetryHandler"/> directly, above a stub transport and a
/// <see cref="ManualTimeProvider"/> so no test spends real time waiting.
/// </summary>
/// <remarks>
/// Two properties matter more than the rest. Retrying something that is not transient burns a
/// rate-limit budget the whole server shares, so the "not retried" table is as load-bearing as the
/// "retried" one; and a <c>Retry-After</c> longer than a minute must end the call rather than park
/// a tool invocation on it.
/// </remarks>
public class RetryHandlerTests
{
    private static readonly Uri RequestUri = new("https://api.bitbucket.org/2.0/repositories/acme/widget-api/pullrequests");

    /// <summary>Statuses Bitbucket recovers from on its own if asked again.</summary>
    public static int[] RetryableStatuses =>
    [
        408, // request timeout
        429, // rate limited
        502,
        503,
        504,
    ];

    /// <summary>
    /// Statuses that are the server's real answer. 500 is deliberately here: an unhandled
    /// exception on Bitbucket's side repeats, and 555 is Bitbucket's "diff too large", which is a
    /// property of the pull request.
    /// </summary>
    public static int[] NonRetryableStatuses =>
    [
        400,
        401,
        403,
        404,
        409,
        422,
        500,
        555,
    ];

    [TestCaseSource(nameof(RetryableStatuses))]
    public async Task RetriesTransientStatuses(int status)
    {
        var (client, stub, time, attempts) = CreateClient();

        using (client)
        {
            stub.Enqueue((HttpStatusCode) status);
            stub.EnqueueJson("""{"ok":true}""");

            using var response = await SendAsync(client, attempts);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(stub.Requests.Count, Is.EqualTo(2));
            Assert.That(attempts.Value, Is.EqualTo(1));
            Assert.That(time.Delays, Has.Exactly(1).Items);
        }
    }

    [TestCaseSource(nameof(NonRetryableStatuses))]
    public async Task DoesNotRetryDeterministicStatuses(int status)
    {
        var (client, stub, time, attempts) = CreateClient();

        using (client)
        {
            stub.Enqueue((HttpStatusCode) status);

            using var response = await SendAsync(client, attempts);

            Assert.That((int) response.StatusCode, Is.EqualTo(status));
            Assert.That(stub.Requests, Has.Exactly(1).Items);
            Assert.That(attempts.Value, Is.EqualTo(0));
            Assert.That(time.Delays, Is.Empty);
        }
    }

    [Test]
    public async Task StopsAfterMaxAttemptsAndReportsTheRetriesItSpent()
    {
        var (client, stub, _, attempts) = CreateClient();

        using (client)
        {
            stub.Fallback = _ => StubHttpMessageHandler.CreateResponse(HttpStatusCode.ServiceUnavailable);

            using var response = await SendAsync(client, attempts);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
            Assert.That(stub.Requests.Count, Is.EqualTo(RetryHandler.MaxAttempts));
            Assert.That(attempts.Value, Is.EqualTo(RetryHandler.MaxAttempts - 1));
        }
    }

    [Test]
    public async Task HonoursRetryAfterDeltaSeconds()
    {
        var (client, stub, time, attempts) = CreateClient();

        using (client)
        {
            stub.Enqueue(_ => WithRetryAfter(HttpStatusCode.TooManyRequests, "7"));
            stub.EnqueueJson("""{"ok":true}""");

            using var response = await SendAsync(client, attempts);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            // Exactly the header value, not the backoff schedule: Bitbucket knows when its window
            // resets and we do not.
            Assert.That(Expect.Single(time.Delays), Is.EqualTo(TimeSpan.FromSeconds(7)));
        }
    }

    [Test]
    public async Task HonoursRetryAfterAsAnHttpDate()
    {
        var (client, stub, time, attempts) = CreateClient();

        using (client)
        {
            var now = new DateTimeOffset(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);
            time.SetUtcNow(now);

            var when = now.AddSeconds(9).UtcDateTime.ToString("r", CultureInfo.InvariantCulture);

            stub.Enqueue(_ => WithRetryAfter(HttpStatusCode.ServiceUnavailable, when));
            stub.EnqueueJson("""{"ok":true}""");

            using var response = await SendAsync(client, attempts);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(Expect.Single(time.Delays), Is.EqualTo(TimeSpan.FromSeconds(9)));
        }
    }

    [Test]
    public async Task TreatsARetryAfterDateInThePastAsNoWaitAtAll()
    {
        var (client, stub, time, attempts) = CreateClient();

        using (client)
        {
            var now = new DateTimeOffset(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);
            time.SetUtcNow(now);

            var when = now.AddMinutes(-5).UtcDateTime.ToString("r", CultureInfo.InvariantCulture);

            stub.Enqueue(_ => WithRetryAfter(HttpStatusCode.TooManyRequests, when));
            stub.EnqueueJson("""{"ok":true}""");

            using var response = await SendAsync(client, attempts);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(stub.Requests.Count, Is.EqualTo(2));

            // Task.Delay short-circuits a zero wait, so no timer is ever created.
            Assert.That(time.Delays, Is.Empty);
        }
    }

    [Test]
    public async Task GivesUpImmediatelyWhenRetryAfterExceedsTheCeiling()
    {
        var (client, stub, time, attempts) = CreateClient();

        using (client)
        {
            var seconds = ((int) RetryHandler.MaxRetryAfter.TotalSeconds + 1).ToString(CultureInfo.InvariantCulture);

            stub.Enqueue(_ => WithRetryAfter(HttpStatusCode.TooManyRequests, seconds));

            using var response = await SendAsync(client, attempts);

            // Returned as-is on purpose: the caller can tell the user how long the rate limit has
            // left, which is more useful than a tool call that blocks for minutes.
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
            Assert.That(stub.Requests, Has.Exactly(1).Items);
            Assert.That(time.Delays, Is.Empty);
            Assert.That(attempts.Value, Is.EqualTo(0));
        }
    }

    [Test]
    public async Task WaitsForARetryAfterExactlyAtTheCeiling()
    {
        var (client, stub, time, attempts) = CreateClient();

        using (client)
        {
            var seconds = ((int) RetryHandler.MaxRetryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);

            stub.Enqueue(_ => WithRetryAfter(HttpStatusCode.TooManyRequests, seconds));
            stub.EnqueueJson("""{"ok":true}""");

            using var response = await SendAsync(client, attempts);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(Expect.Single(time.Delays), Is.EqualTo(RetryHandler.MaxRetryAfter));
        }
    }

    [Test]
    public async Task BacksOffExponentiallyWithinTheJitterBand()
    {
        var (client, stub, time, attempts) = CreateClient();

        using (client)
        {
            stub.Fallback = _ => StubHttpMessageHandler.CreateResponse(HttpStatusCode.BadGateway);

            using var response = await SendAsync(client, attempts);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadGateway));

            var delays = time.Delays;
            Assert.That(delays.Count, Is.EqualTo(RetryHandler.MaxAttempts - 1));

            // min(2^n · 500 ms, 20 s) ± 25 %.
            AssertWithinJitter(TimeSpan.FromMilliseconds(500), delays[0]);
            AssertWithinJitter(TimeSpan.FromMilliseconds(1000), delays[1]);
            AssertWithinJitter(TimeSpan.FromMilliseconds(2000), delays[2]);
        }
    }

    [Test]
    public async Task RetriesATransientTransportFailure()
    {
        var (client, stub, time, attempts) = CreateClient();

        using (client)
        {
            stub.Enqueue(_ => throw new HttpRequestException("connection reset"));
            stub.Enqueue(_ => throw new IOException("stream closed"));
            stub.EnqueueJson("""{"ok":true}""");

            using var response = await SendAsync(client, attempts, Body());

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(stub.Requests.Count, Is.EqualTo(3));
            Assert.That(attempts.Value, Is.EqualTo(2));
            Assert.That(time.Delays.Count, Is.EqualTo(2));
        }
    }

    [Test]
    public async Task PropagatesATransportFailureThatOutlivesTheRetryBudget()
    {
        var (client, stub, _, attempts) = CreateClient();

        using (client)
        {
            stub.Fallback = _ => throw new HttpRequestException("connection reset");

            await Assert.ThrowsAsync<HttpRequestException>(() => SendAsync(client, attempts, Body()));

            Assert.That(stub.Requests.Count, Is.EqualTo(RetryHandler.MaxAttempts));
        }
    }

    [Test]
    public async Task NeverRetriesARequestWhoseBodyCannotBeSentAgain()
    {
        var (client, stub, time, attempts) = CreateClient();

        using (client)
        {
            stub.Fallback = _ => StubHttpMessageHandler.CreateResponse(HttpStatusCode.ServiceUnavailable);

            using var content = new StreamContent(new MemoryStream("{}"u8.ToArray()));
            using var response = await SendAsync(client, attempts, content);

            // The first attempt drained the stream; reissuing would send an empty body.
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
            Assert.That(stub.Requests, Has.Exactly(1).Items);
            Assert.That(time.Delays, Is.Empty);
        }
    }

    [Test]
    public async Task NeverRetriesATransportFailureForAnUnsendableBody()
    {
        var (client, stub, _, attempts) = CreateClient();

        using (client)
        {
            stub.Fallback = _ => throw new HttpRequestException("connection reset");

            using var content = new StreamContent(new MemoryStream("{}"u8.ToArray()));

            await Assert.ThrowsAsync<HttpRequestException>(() => SendAsync(client, attempts, content));

            Assert.That(stub.Requests, Has.Exactly(1).Items);
        }
    }

    [Test]
    public async Task WorksWithoutAnAttemptCounterAttached()
    {
        var (client, stub, _, _) = CreateClient();

        using (client)
        {
            stub.Enqueue(HttpStatusCode.ServiceUnavailable);
            stub.EnqueueJson("""{"ok":true}""");

            using var request = new HttpRequestMessage(HttpMethod.Get, RequestUri);
            using var response = await client.SendAsync(request, TestContext.CurrentContext.CancellationToken);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(stub.Requests.Count, Is.EqualTo(2));
        }
    }

    [Test]
    public void KnowsWhichBodiesCanBeSentAgain()
    {
        Assert.That(RetryHandler.IsResendable(null), Is.True);

        using var bytes = new ByteArrayContent("{}"u8.ToArray());
        Assert.That(RetryHandler.IsResendable(bytes), Is.True);

        using var memory = new ReadOnlyMemoryContent("{}"u8.ToArray());
        Assert.That(RetryHandler.IsResendable(memory), Is.True);

        // StringContent derives from ByteArrayContent, so it is buffered and replayable.
        using var text = new StringContent("{}", Encoding.UTF8, "application/json");
        Assert.That(RetryHandler.IsResendable(text), Is.True);

        using var stream = new StreamContent(new MemoryStream());
        Assert.That(RetryHandler.IsResendable(stream), Is.False);
    }

    [Test]
    public void ExposesTheDocumentedRetryBudget()
    {
        Assert.That(RetryHandler.MaxAttempts, Is.EqualTo(4));
        Assert.That(RetryHandler.MaxRetryAfter, Is.EqualTo(TimeSpan.FromSeconds(60)));
        Assert.That(RetryHandler.LowRateLimitRemaining, Is.EqualTo(50));
    }

    [Test]
    public async Task ToleratesRateLimitHeadersOnEveryResponse()
    {
        var (client, stub, _, attempts) = CreateClient();

        using (client)
        {
            stub.Enqueue(_ =>
            {
                var response = StubHttpMessageHandler.CreateResponse(HttpStatusCode.TooManyRequests);
                response.Headers.Add("X-RateLimit-Remaining", "3");
                response.Headers.Add("X-RateLimit-Reset", "1786000000");
                return response;
            });

            stub.Enqueue(_ =>
            {
                var response = StubHttpMessageHandler.CreateResponse(HttpStatusCode.OK, """{"ok":true}""");
                response.Headers.Add("X-RateLimit-Remaining", "not-a-number");
                return response;
            });

            using var response = await SendAsync(client, attempts);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }
    }

    private static void AssertWithinJitter(TimeSpan expected, TimeSpan actual)
    {
        Assert.That(actual, Is.InRange(expected * 0.75, expected * 1.25));
    }

    private static ByteArrayContent Body() => new("{}"u8.ToArray());

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        RetryAttemptCounter attempts,
        HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(content is null ? HttpMethod.Get : HttpMethod.Post, RequestUri)
        {
            Content = content,
        };

        request.Options.Set(RetryHandler.RetryAttemptsKey, attempts);

        return await client.SendAsync(request, TestContext.CurrentContext.CancellationToken);
    }

    private static HttpResponseMessage WithRetryAfter(HttpStatusCode status, string value)
    {
        var response = StubHttpMessageHandler.CreateResponse(status);
        response.Headers.Add("Retry-After", value);
        return response;
    }

    private static (HttpClient Client, StubHttpMessageHandler Stub, ManualTimeProvider Time, RetryAttemptCounter Attempts) CreateClient()
    {
        var stub = new StubHttpMessageHandler();
        var time = new ManualTimeProvider();

        var handler = new RetryHandler(NullLogger.Instance, time)
        {
            InnerHandler = stub,
        };

        return (new HttpClient(handler, disposeHandler: true), stub, time, new RetryAttemptCounter());
    }
}
