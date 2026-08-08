using System.Net;

using Bitbucket.Mcp.Authentication;

using Microsoft.Extensions.Logging;

namespace Bitbucket.Mcp.Http;

/// <summary>
/// The outermost handler: attaches the <c>Authorization</c> header to every request, and gives a
/// rejected credential exactly one chance to be replaced.
/// </summary>
/// <remarks>
/// <para>
/// <b>The header is set per request and never on <see cref="HttpClient.DefaultRequestHeaders"/>.</b>
/// That is not a style choice. <c>SocketsHttpHandler</c> strips a <em>per-request</em>
/// <c>Authorization</c> header when it follows a redirect to a different host, but a default header
/// is re-applied to the redirected request — and Bitbucket answers <c>GET …/diff</c> and
/// <c>…/diffstat</c> with a <c>302</c> to a pre-signed URL on another host. A default header would
/// therefore hand our live Bitbucket credential to whatever host the redirect names.
/// </para>
/// <para>
/// It sits outside <see cref="RetryHandler"/> so that a credential refreshed here is used by every
/// subsequent retry, and so that the 401 path is not itself subject to backoff: a 401 is not
/// transient and is answered once, immediately, with a fresh header.
/// </para>
/// </remarks>
internal sealed class AuthenticationHandler : DelegatingHandler
{
    private readonly ICredentialProvider _credentialProvider;
    private readonly ILogger _logger;

    /// <param name="credentialProvider">Source of the header; may block on a token refresh or sign-in.</param>
    /// <param name="logger">Where the 401-retry line goes. Never stdout.</param>
    internal AuthenticationHandler(ICredentialProvider credentialProvider, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(credentialProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _credentialProvider = credentialProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        request.Headers.Authorization =
            await _credentialProvider.GetAuthenticationHeaderAsync(cancellationToken).ConfigureAwait(false);

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        // A body that cannot be sent twice rules out the retry; the 401 is reported as it stands.
        if (response.StatusCode != HttpStatusCode.Unauthorized || !RetryHandler.IsResendable(request.Content))
        {
            return response;
        }

        _logger.LogDebug("Bitbucket rejected the credential with 401; discarding it and retrying once.");

        // Nothing has read the body yet, so this discards a complete response, not a partial one.
        response.Dispose();

        await _credentialProvider.InvalidateAsync(cancellationToken).ConfigureAwait(false);

        request.Headers.Authorization =
            await _credentialProvider.GetAuthenticationHeaderAsync(cancellationToken).ConfigureAwait(false);

        // Exactly one retry: if the freshly acquired credential is also rejected, the credential is
        // wrong rather than stale, and looping would only turn a clear 401 into a slow one.
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
