using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

using Bitbucket.Mcp.Authentication;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace Bitbucket.Mcp.Tests.Authentication;

/// <summary>
/// The loopback callback listener, exercised over a real socket on a scratch port.
/// </summary>
/// <remarks>
/// Everything this class reads arrives on a listening TCP port that any local process can reach, so
/// the tests below are as much about what it <em>refuses</em> as about the happy path: a forged
/// <c>state</c>, a stray browser preconnect, a probe that is not HTTP at all. In every one of those
/// cases the listener must answer (or drop) the connection and keep waiting — treating a bad request
/// as the answer would be a one-line denial of service on every sign-in.
/// </remarks>
public sealed class LoopbackCallbackListenerTests
{
    private const string ExpectedState = "state-9f2b7c41";

    [Fact]
    public async Task CallbackWithTheExpectedStateCompletesTheFlow()
    {
        var (listener, port) = AuthTestSupport.StartListener();

        using (listener)
        {
            var wait = listener.WaitForCallbackAsync(ExpectedState, TestContext.Current.CancellationToken);

            var (status, body) = await AuthTestSupport.GetAsync(
                CallbackUrl(port, $"code=the-authorization-code&state={ExpectedState}"),
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Contains("Authorization complete", body, StringComparison.Ordinal);

            var result = await wait;

            Assert.Equal("the-authorization-code", result.Code);
            Assert.Null(result.Error);
        }
    }

    /// <summary>
    /// A mismatched <c>state</c> is rejected without ending the sign-in, so the browser's real
    /// callback still lands afterwards.
    /// </summary>
    [Fact]
    public async Task StateMismatchIsRejectedAndTheRealCallbackStillSucceeds()
    {
        var (listener, port) = AuthTestSupport.StartListener();

        using (listener)
        {
            var wait = listener.WaitForCallbackAsync(ExpectedState, TestContext.Current.CancellationToken);

            var (status, _) = await AuthTestSupport.GetAsync(
                CallbackUrl(port, "code=forged&state=not-the-expected-state"),
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.False(wait.IsCompleted, "A forged state must not end the sign-in.");

            var (goodStatus, _) = await AuthTestSupport.GetAsync(
                CallbackUrl(port, $"code=the-real-code&state={ExpectedState}"),
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, goodStatus);

            var result = await wait;

            Assert.Equal("the-real-code", result.Code);
        }
    }

    [Fact]
    public async Task AuthorizationServerErrorIsSurfacedToTheCaller()
    {
        var (listener, port) = AuthTestSupport.StartListener();

        using (listener)
        {
            var wait = listener.WaitForCallbackAsync(ExpectedState, TestContext.Current.CancellationToken);

            var (status, body) = await AuthTestSupport.GetAsync(
                CallbackUrl(port, $"error=access_denied&error_description=The+user+said+no&state={ExpectedState}"),
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.Contains("The user said no", body, StringComparison.Ordinal);

            var result = await wait;

            Assert.Null(result.Code);
            Assert.Equal("access_denied", result.Error);
            Assert.Equal("The user said no", result.ErrorDescription);
        }
    }

    [Fact]
    public async Task CallbackCarryingNeitherCodeNorErrorIsRejectedAndIgnored()
    {
        var (listener, port) = AuthTestSupport.StartListener();

        using (listener)
        {
            var wait = listener.WaitForCallbackAsync(ExpectedState, TestContext.Current.CancellationToken);

            var (status, _) = await AuthTestSupport.GetAsync(
                CallbackUrl(port, $"state={ExpectedState}"),
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.False(wait.IsCompleted);

            await CompleteAndAssertAsync(port, wait);
        }
    }

    /// <summary>Browsers ask for <c>/favicon.ico</c>; that is not the callback and must not end it.</summary>
    [Fact]
    public async Task RequestToAnotherPathIsAnsweredWithNotFoundAndIgnored()
    {
        var (listener, port) = AuthTestSupport.StartListener();

        using (listener)
        {
            var wait = listener.WaitForCallbackAsync(ExpectedState, TestContext.Current.CancellationToken);

            var (status, _) = await AuthTestSupport.GetAsync(
                $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/favicon.ico",
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.NotFound, status);
            Assert.False(wait.IsCompleted);

            await CompleteAndAssertAsync(port, wait);
        }
    }

    [Fact]
    public async Task ConnectionThatIsNotHttpIsDroppedSilentlyAndIgnored()
    {
        var (listener, port) = AuthTestSupport.StartListener();

        using (listener)
        {
            var wait = listener.WaitForCallbackAsync(ExpectedState, TestContext.Current.CancellationToken);

            var response = await SendRawAsync(port, " not http at all\r\n\r\n", TestContext.Current.CancellationToken);

            Assert.Equal(string.Empty, response);
            Assert.False(wait.IsCompleted);

            await CompleteAndAssertAsync(port, wait);
        }
    }

    /// <summary>
    /// Only a GET can be the callback. Anything else is answered with nothing at all, so a local
    /// process cannot use the port as an echo service.
    /// </summary>
    [Fact]
    public async Task PostIsDroppedSilentlyAndIgnored()
    {
        var (listener, port) = AuthTestSupport.StartListener();

        using (listener)
        {
            var wait = listener.WaitForCallbackAsync(ExpectedState, TestContext.Current.CancellationToken);

            var response = await SendRawAsync(
                port,
                $"POST /callback?code=posted&state={ExpectedState} HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Length: 0\r\n\r\n",
                TestContext.Current.CancellationToken);

            Assert.Equal(string.Empty, response);
            Assert.False(wait.IsCompleted);

            await CompleteAndAssertAsync(port, wait);
        }
    }

    /// <summary>A request head larger than the 8 KiB cap is abandoned rather than buffered.</summary>
    [Fact]
    public async Task OversizedRequestHeadIsAbandonedAndIgnored()
    {
        var (listener, port) = AuthTestSupport.StartListener();

        using (listener)
        {
            var wait = listener.WaitForCallbackAsync(ExpectedState, TestContext.Current.CancellationToken);

            var padding = new string('x', 9 * 1024);

            var response = await SendRawAsync(
                port,
                $"GET /callback?code=huge&state={ExpectedState}&padding={padding} HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n",
                TestContext.Current.CancellationToken);

            Assert.Equal(string.Empty, response);
            Assert.False(wait.IsCompleted);

            await CompleteAndAssertAsync(port, wait);
        }
    }

    [Fact]
    public async Task CancellationUnblocksTheWait()
    {
        var (listener, _) = AuthTestSupport.StartListener();

        using (listener)
        {
            using var cancellation = new CancellationTokenSource();

            var wait = listener.WaitForCallbackAsync(ExpectedState, cancellation.Token);

            await cancellation.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        }
    }

    [Fact]
    public async Task WaitingBeforeStartingIsARefusal()
    {
        using var listener = new LoopbackCallbackListener(
            AuthTestSupport.FreeScratchPort(),
            LoopbackCallbackListener.DefaultCallbackPath,
            NullLogger.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => listener.WaitForCallbackAsync(ExpectedState, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DisposedListenerRefusesToWait()
    {
        var (listener, _) = AuthTestSupport.StartListener();

        listener.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => listener.WaitForCallbackAsync(ExpectedState, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The IPv4 bind is mandatory — it is what the default redirect URI names — while <c>[::1]</c>
    /// is best effort, for the host where the consumer's callback URL says <c>localhost</c> and that
    /// resolves to IPv6 first (risk R3). Where the second bind succeeded, the flow must complete
    /// over it too.
    /// </summary>
    [Fact]
    public async Task CallbackArrivingOnIpv6LoopbackAlsoCompletesTheFlow()
    {
        var (listener, port) = AuthTestSupport.StartListener();

        using (listener)
        {
            if (!listener.IsListeningOnIPv6)
            {
                Assert.Skip("This host has no IPv6 loopback; the IPv4 socket carries the flow.");
                return;
            }

            var wait = listener.WaitForCallbackAsync(ExpectedState, TestContext.Current.CancellationToken);

            var (status, _) = await AuthTestSupport.GetAsync(
                $"http://[::1]:{port.ToString(CultureInfo.InvariantCulture)}{LoopbackCallbackListener.DefaultCallbackPath}"
                    + $"?code=ipv6-code&state={ExpectedState}",
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, status);

            var result = await wait;

            Assert.Equal("ipv6-code", result.Code);
        }
    }

    private static string CallbackUrl(int port, string query) =>
        $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}{LoopbackCallbackListener.DefaultCallbackPath}?{query}";

    /// <summary>Sends the real callback and asserts that the wait completed with its code.</summary>
    private static async Task CompleteAndAssertAsync(int port, Task<LoopbackCallbackResult> wait)
    {
        var (status, _) = await AuthTestSupport.GetAsync(
            CallbackUrl(port, $"code=the-real-code&state={ExpectedState}"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, status);

        var result = await wait;

        Assert.Equal("the-real-code", result.Code);
    }

    /// <summary>
    /// Writes raw bytes and reads until the peer closes, so "the listener said nothing" is an
    /// observation rather than a timeout.
    /// </summary>
    private static async Task<string> SendRawAsync(int port, string request, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken);

            var stream = client.GetStream();

            await stream.WriteAsync(Encoding.ASCII.GetBytes(request), cancellationToken);
            await stream.FlushAsync(cancellationToken);

            using var reader = new StreamReader(stream, Encoding.UTF8);

            return await reader.ReadToEndAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            // The listener closed the connection under us, which is the same verdict as an empty
            // response: it did not treat the request as a callback.
            return string.Empty;
        }
    }
}
