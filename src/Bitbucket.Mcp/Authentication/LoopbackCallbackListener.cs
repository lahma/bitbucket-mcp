// SECURITY-SENSITIVE FILE. Everything this class reads arrives on a listening TCP socket and is
// therefore untrusted input: the request line, the request target, and every query parameter in it.
// Anything on the machine can connect to a loopback port, so nothing here may assume the peer is a
// browser, that it speaks HTTP, or that it is the browser we sent to Bitbucket. Keep it dependency
// free, keep it short, and keep it readable — this is a file that gets audited rather than skimmed.

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Logging;

namespace Bitbucket.Mcp.Authentication;

/// <summary>
/// The other end of the OAuth redirect: a minimal HTTP listener on the loopback interface that waits
/// for Bitbucket to send the browser to <c>http://127.0.0.1:{port}/callback?code=…&amp;state=…</c>.
/// </summary>
/// <remarks>
/// <para>
/// A raw <see cref="TcpListener"/> rather than <c>HttpListener</c> (D12). This has to answer exactly
/// one request shape, and doing it by hand costs about eighty lines while avoiding
/// <c>HttpListener</c>'s platform behaviour — an HTTP.sys URL ACL on Windows, an entirely different
/// managed implementation elsewhere.
/// </para>
/// <para>
/// It binds <c>127.0.0.1</c> and, best effort, <c>[::1]</c> on the same port, because the callback
/// URL registered on the consumer may name <c>localhost</c> (risk R3) and which family that resolves
/// to is not ours to decide. The first connection carrying a valid callback wins, whichever socket it
/// arrived on.
/// </para>
/// <para>
/// Three details are deliberate and load bearing:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Stray connections are ignored, not fatal.</b> Browsers open speculative connections that send
/// nothing, close early, or ask for <c>/favicon.ico</c>. Each is answered (or dropped) and the loop
/// carries on. Connections are handled concurrently so that one silent socket cannot delay the real
/// callback behind it.
/// </description></item>
/// <item><description>
/// <b>A <c>state</c> mismatch does not end the flow.</b> Anything local can hit this port; treating
/// a forged or stale hit as the answer would hand an attacker a trivial denial of service on every
/// sign-in. A mismatch gets a 400 and the listener keeps waiting for the real callback.
/// </description></item>
/// <item><description>
/// <b>There is no timeout here.</b> The caller owns the bound (<c>BITBUCKET_MCP_AUTH_TIMEOUT_SECONDS</c>
/// plus the tool call's own token) and passes it in.
/// </description></item>
/// </list>
/// </remarks>
internal sealed class LoopbackCallbackListener : IDisposable
{
    /// <summary>The path Bitbucket redirects to; the rest of the redirect URI comes from options.</summary>
    internal const string DefaultCallbackPath = "/callback";

    /// <summary>Cap on the request head we will buffer. A callback is a few hundred bytes.</summary>
    private const int MaxRequestBytes = 8 * 1024;

    /// <summary>
    /// How long one connection may take to send a complete request head. Bounded so a socket that
    /// connects and then says nothing is dropped rather than held open for the whole sign-in.
    /// </summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly int _port;
    private readonly string _expectedPath;
    private readonly ILogger _logger;
    private readonly List<TcpListener> _listeners = [];

    private bool _disposed;

    /// <param name="port">The loopback port to bind, from <c>BITBUCKET_OAUTH_CALLBACK_PORT</c>.</param>
    /// <param name="expectedPath">
    /// The path half of the registered callback URL, compared byte for byte. Requests to any other
    /// path get a 404 and are otherwise ignored.
    /// </param>
    /// <param name="logger">Logger. Everything it writes goes to stderr.</param>
    internal LoopbackCallbackListener(int port, string expectedPath, ILogger logger)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedPath);
        ArgumentNullException.ThrowIfNull(logger);

        _port = port;
        _expectedPath = expectedPath;
        _logger = logger;
    }

    /// <summary>Whether the <c>[::1]</c> socket could also be bound. Diagnostics only.</summary>
    internal bool IsListeningOnIPv6 { get; private set; }

    /// <summary>
    /// Binds the loopback sockets. Called <em>before</em> the browser is launched, so that a
    /// redirect cannot arrive at a port nobody is listening on.
    /// </summary>
    /// <exception cref="SocketException">
    /// The IPv4 loopback port could not be bound — almost always another process already on it.
    /// </exception>
    internal void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // IPv4 is mandatory: the default redirect URI is http://127.0.0.1:{port}/callback.
        var ipv4 = new TcpListener(IPAddress.Loopback, _port);
        ipv4.Start();
        _listeners.Add(ipv4);

        // [::1] is best effort. On a host where `localhost` resolves to IPv6 first, this is the
        // socket the browser will actually reach; on a host without IPv6 the bind simply fails and
        // IPv4 carries the flow.
        try
        {
            var ipv6 = new TcpListener(IPAddress.IPv6Loopback, _port);
            ipv6.Start();
            _listeners.Add(ipv6);
            IsListeningOnIPv6 = true;
        }
        catch (SocketException ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Could not also listen on [::1]:{Port}; using IPv4 loopback only.", _port);
            }
        }
    }

    /// <summary>
    /// Waits for the first request that is a genuine callback: right path, matching <c>state</c>,
    /// and either a code or an error from the authorization server.
    /// </summary>
    /// <param name="expectedState">The <c>state</c> value sent in the authorization request.</param>
    /// <param name="cancellationToken">
    /// The caller's bound on the wait. There is no other timeout.
    /// </param>
    /// <exception cref="OperationCanceledException">The wait was cancelled or timed out.</exception>
    internal async Task<LoopbackCallbackResult> WaitForCallbackAsync(string expectedState, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(expectedState);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_listeners.Count == 0)
        {
            throw new InvalidOperationException($"{nameof(Start)} must be called before waiting for a callback.");
        }

        var completion = new TaskCompletionSource<LoopbackCallbackResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var registration = cancellationToken.Register(
            static (state, token) => ((TaskCompletionSource<LoopbackCallbackResult>) state!).TrySetCanceled(token),
            completion);

        foreach (var listener in _listeners)
        {
            _ = AcceptLoopAsync(listener, expectedState, completion, cancellationToken);
        }

        return await completion.Task.ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Stopping the listeners is what unblocks the accept loops when the caller gives up; in-flight
    /// connection handlers then fail on a disposed socket and swallow it.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var listener in _listeners)
        {
            listener.Dispose();
        }

        _listeners.Clear();
    }

    /// <summary>Accepts until cancelled or disposed, handing every connection to its own task.</summary>
    private async Task AcceptLoopAsync(
        TcpListener listener,
        string expectedState,
        TaskCompletionSource<LoopbackCallbackResult> completion,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && !completion.Task.IsCompleted)
        {
            TcpClient client;

            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            // Concurrently, so that a browser's preconnect socket sitting silent for its ten-second
            // budget cannot queue up behind it the request we are actually waiting for.
            _ = HandleConnectionAsync(client, expectedState, completion, cancellationToken);
        }
    }

    /// <summary>
    /// Reads one request, answers it, and completes <paramref name="completion"/> only for a request
    /// that really is the callback. Never throws.
    /// </summary>
    private async Task HandleConnectionAsync(
        TcpClient client,
        string expectedState,
        TaskCompletionSource<LoopbackCallbackResult> completion,
        CancellationToken cancellationToken)
    {
        try
        {
            using (client)
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                deadline.CancelAfter(RequestTimeout);

                var stream = client.GetStream();
                var requestLine = await ReadRequestLineAsync(stream, deadline.Token).ConfigureAwait(false);

                // No parseable request line: a preconnect socket, a probe, or something that is not
                // HTTP at all. Say nothing and let the accept loop carry on.
                if (requestLine is null || ParseRequestTarget(requestLine) is not { } target)
                {
                    return;
                }

                var separator = target.IndexOf('?', StringComparison.Ordinal);
                var path = separator < 0 ? target : target[..separator];
                var query = separator < 0 ? string.Empty : target[(separator + 1)..];

                if (!string.Equals(path, _expectedPath, StringComparison.Ordinal))
                {
                    // /favicon.ico and friends.
                    await RespondAsync(stream, "404 Not Found", "Not found.", deadline.Token).ConfigureAwait(false);
                    return;
                }

                // The state check comes first, and a mismatch deliberately does NOT complete the
                // flow: any local process can hit this port, and letting a forged or stale request
                // consume the sign-in would be a one-line denial of service. Constant-time compare
                // so the response cannot be used to recover the expected value byte by byte.
                if (!IsExpectedState(GetQueryValue(query, "state"), expectedState))
                {
                    _logger.LogWarning("Ignoring a callback request whose state parameter did not match; still waiting.");

                    await RespondAsync(
                        stream,
                        "400 Bad Request",
                        "This request did not come from the sign-in that is currently in progress.",
                        deadline.Token).ConfigureAwait(false);

                    return;
                }

                if (GetQueryValue(query, "error") is { Length: > 0 } error)
                {
                    var description = GetQueryValue(query, "error_description");

                    // Untrusted text on its way into HTML — encoded, never interpolated raw.
                    await RespondAsync(
                        stream,
                        "400 Bad Request",
                        "Authorization failed: " + WebUtility.HtmlEncode(description ?? error) + " You can close this tab.",
                        deadline.Token).ConfigureAwait(false);

                    completion.TrySetResult(new LoopbackCallbackResult(Code: null, error, description));
                    return;
                }

                if (GetQueryValue(query, "code") is not { Length: > 0 } code)
                {
                    await RespondAsync(
                        stream,
                        "400 Bad Request",
                        "This callback carried neither an authorization code nor an error.",
                        deadline.Token).ConfigureAwait(false);

                    return;
                }

                await RespondAsync(
                    stream,
                    "200 OK",
                    "Authorization complete — you can close this tab and return to your terminal.",
                    deadline.Token).ConfigureAwait(false);

                completion.TrySetResult(new LoopbackCallbackResult(code, Error: null, ErrorDescription: null));
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException
            or OperationCanceledException or InvalidOperationException)
        {
            // A peer that hung up, a socket closed under us by Dispose, or a connection that used up
            // its ten seconds. All of them are "not the callback"; the loop continues.
            _logger.LogTrace(ex, "Discarded a connection to the OAuth callback listener.");
        }
    }

    /// <summary>
    /// Reads until the blank line that ends the request head, and returns the request line alone, or
    /// <see langword="null"/> if the peer closed early or overran <see cref="MaxRequestBytes"/>.
    /// </summary>
    /// <remarks>
    /// The whole head is consumed rather than just the first line so that the response is written to
    /// a client that has finished talking. ASCII decoding is intentional: a request target is ASCII
    /// by construction, and any other byte becomes a character that fails the parse below.
    /// </remarks>
    private static async Task<string?> ReadRequestLineAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxRequestBytes];
        var count = 0;

        while (count < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(count), cancellationToken).ConfigureAwait(false);

            if (read == 0)
            {
                return null;
            }

            count += read;

            var received = buffer.AsSpan(0, count);

            if (received.IndexOf("\r\n\r\n"u8) >= 0)
            {
                return Encoding.ASCII.GetString(received[..received.IndexOf("\r\n"u8)]);
            }
        }

        return null;
    }

    /// <summary>
    /// Pulls the request target out of <c>GET /callback?… HTTP/1.1</c>, or returns
    /// <see langword="null"/> for anything that is not a GET of an absolute path.
    /// </summary>
    private static string? ParseRequestTarget(string requestLine)
    {
        var methodEnd = requestLine.IndexOf(' ', StringComparison.Ordinal);

        if (methodEnd <= 0 || !requestLine.AsSpan(0, methodEnd).SequenceEqual("GET"))
        {
            return null;
        }

        var targetEnd = requestLine.IndexOf(' ', methodEnd + 1);

        if (targetEnd < 0)
        {
            return null;
        }

        var target = requestLine[(methodEnd + 1)..targetEnd];

        return target.StartsWith('/') ? target : null;
    }

    /// <summary>
    /// The decoded value of <paramref name="name"/> in a <c>&amp;</c>-separated query, or
    /// <see langword="null"/>. First occurrence wins, so a duplicate appended by an attacker cannot
    /// override the parameter the browser actually carried.
    /// </summary>
    private static string? GetQueryValue(string query, string name)
    {
        foreach (var range in query.AsSpan().Split('&'))
        {
            var pair = query.AsSpan()[range];
            var equals = pair.IndexOf('=');

            if (equals < 0 || !pair[..equals].SequenceEqual(name))
            {
                continue;
            }

            // UrlDecode, not UnescapeDataString: a query encodes a space as '+' as well as '%20'.
            return WebUtility.UrlDecode(pair[(equals + 1)..].ToString());
        }

        return null;
    }

    /// <summary>Constant-time comparison of the returned <c>state</c> against the expected one.</summary>
    private static bool IsExpectedState(string? actual, string expected) =>
        actual is not null
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(actual), Encoding.UTF8.GetBytes(expected));

    /// <summary>
    /// Writes a complete, minimal HTTP/1.1 response: no external assets, no scripts, one sentence.
    /// </summary>
    /// <param name="stream">The connection.</param>
    /// <param name="status">Status code and reason phrase, for example <c>200 OK</c>.</param>
    /// <param name="message">The body text. Already HTML-encoded if it came from the network.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    private static async Task RespondAsync(NetworkStream stream, string status, string message, CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(
            $"<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>bitbucket-mcp</title></head>"
            + $"<body><p>{message}</p></body></html>");

        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status}\r\n"
            + "Content-Type: text/html; charset=utf-8\r\n"
            + $"Content-Length: {body.Length}\r\n"
            + "Connection: close\r\n\r\n");

        await stream.WriteAsync(head, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// What the callback carried: an authorization code, or the authorization server's refusal.
/// </summary>
/// <param name="Code">The authorization code, when the user approved.</param>
/// <param name="Error">The RFC 6749 error code, when they did not (<c>access_denied</c>, …).</param>
/// <param name="ErrorDescription">The server's elaboration on <paramref name="Error"/>, if any.</param>
internal sealed record LoopbackCallbackResult(string? Code, string? Error, string? ErrorDescription);
