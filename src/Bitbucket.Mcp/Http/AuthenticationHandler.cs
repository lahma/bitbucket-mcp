using System.Net;
using System.Net.Http.Headers;

using Bitbucket.Mcp.Authentication;
using Bitbucket.Mcp.Http.Models;

using Microsoft.Extensions.Logging;

namespace Bitbucket.Mcp.Http;

/// <summary>
/// The outermost handler: attaches the <c>Authorization</c> header to every request, gives a
/// rejected credential exactly one chance to be replaced, and follows redirects itself so that the
/// credential survives the hop.
/// </summary>
/// <remarks>
/// <para>
/// <b>Redirects are followed here rather than by the transport</b> (D16). Bitbucket answers
/// <c>GET …/diff</c> and <c>…/diffstat</c> with a <c>302</c> to another path on
/// <c>api.bitbucket.org</c>, and that target still requires the credential — but
/// <see cref="SocketsHttpHandler"/> strips the <c>Authorization</c> header on <em>every</em>
/// automatic redirect, same-origin ones included, and does not re-apply a default header to the
/// redirected request either. Verified on 2026-08-09 against live Bitbucket and against a local
/// echo server: the second request went out unauthenticated and Bitbucket answered <c>404</c> with
/// its private-repository message. The transport therefore sets <c>AllowAutoRedirect = false</c>
/// and the hops happen below, where the header can be attached again.
/// </para>
/// <para>
/// <b>The credential is re-attached only for <c>https://api.bitbucket.org</c>.</b> A redirect
/// anywhere else is still followed, but without the header: the target of a redirect is chosen by
/// the server, and handing a live Bitbucket credential to whatever host it names is the one mistake
/// this must not make. For the same reason the header is set per request and never on
/// <see cref="HttpClient.DefaultRequestHeaders"/>.
/// </para>
/// <para>
/// Only <c>GET</c> and <c>HEAD</c> are followed. Those are the only requests Bitbucket redirects,
/// and re-issuing a request that carried a body is how a write silently happens twice.
/// </para>
/// <para>
/// It sits outside <see cref="RetryHandler"/> so that a credential refreshed here is used by every
/// subsequent retry, so that each redirect hop gets a retry budget of its own, and so that the 401
/// path is not itself subject to backoff: a 401 is not transient and is answered once, immediately,
/// with a fresh header.
/// </para>
/// </remarks>
internal sealed class AuthenticationHandler : DelegatingHandler
{
    /// <summary>
    /// How many redirects are followed before the request is failed. Bitbucket uses exactly one;
    /// five leaves room for a load-balancer bounce without letting a redirect loop spin.
    /// </summary>
    internal const int MaxRedirects = 5;

    /// <summary>The only host a redirect may carry the credential to.</summary>
    private const string ApiHost = "api.bitbucket.org";

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

        var credential = await _credentialProvider.GetAuthenticationHeaderAsync(cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = credential;

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        // A body that cannot be sent twice rules out the retry; the 401 is reported as it stands.
        if (response.StatusCode == HttpStatusCode.Unauthorized && RetryHandler.IsResendable(request.Content))
        {
            _logger.LogDebug("Bitbucket rejected the credential with 401; discarding it and retrying once.");

            // Nothing has read the body yet, so this discards a complete response, not a partial one.
            response.Dispose();

            await _credentialProvider.InvalidateAsync(cancellationToken).ConfigureAwait(false);

            credential = await _credentialProvider.GetAuthenticationHeaderAsync(cancellationToken).ConfigureAwait(false);
            request.Headers.Authorization = credential;

            // Exactly one retry: if the freshly acquired credential is also rejected, the credential
            // is wrong rather than stale, and looping would only turn a clear 401 into a slow one.
            response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        return await FollowRedirectsAsync(request, response, credential, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Follows up to <see cref="MaxRedirects"/> hops, re-attaching the credential on each one that
    /// stays on the Bitbucket API host.
    /// </summary>
    /// <remarks>
    /// The credential from the initial send is reused rather than re-fetched per hop. A token that
    /// expires between two requests of the same redirect chain is vanishingly rare, and the 401 path
    /// above already covers it on the next call — asking the provider again per hop would only add a
    /// refresh opportunity nothing needs.
    /// </remarks>
    private async Task<HttpResponseMessage> FollowRedirectsAsync(
        HttpRequestMessage request,
        HttpResponseMessage response,
        AuthenticationHeaderValue credential,
        CancellationToken cancellationToken)
    {
        // Never a request with content: replaying one after a redirect is how a write happens twice.
        // The absolute-URI check is a formality — HttpClient resolves the request against its base
        // address before the chain runs — but a relative Location has to be resolved against
        // something.
        if ((request.Method != HttpMethod.Get && request.Method != HttpMethod.Head)
            || request.RequestUri is not { IsAbsoluteUri: true } current)
        {
            return response;
        }

        for (var hops = 0; IsRedirect(response.StatusCode); hops++)
        {
            var location = response.Headers.Location;

            // A redirect status with no usable Location is not something to guess at: the response
            // travels up as it is, and the client turns the status into an error.
            if (location is null || location.OriginalString.Length == 0)
            {
                return response;
            }

            // Bitbucket sends this one relative (`/2.0/repositories/…`), which RFC 9110 allows.
            var target = new Uri(current, location);

            if (hops == MaxRedirects)
            {
                var status = response.StatusCode;
                response.Dispose();

                throw TooManyRedirects(request, status, target);
            }

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    "Bitbucket redirected with HTTP {StatusCode} to {Target}; following it {WithOrWithout} the credential.",
                    (int) response.StatusCode,
                    target,
                    CarriesCredential(target) ? "with" : "without");
            }

            // Nothing has read the body of a redirect, so this discards a complete response.
            response.Dispose();

            using var redirected = CreateRedirect(request, target, credential);

            current = target;
            response = await base.SendAsync(redirected, cancellationToken).ConfigureAwait(false);
        }

        return response;
    }

    /// <summary>
    /// Builds the request for one hop.
    /// </summary>
    /// <remarks>
    /// An <see cref="HttpRequestMessage"/> cannot be sent twice, and nothing copies anything for us
    /// any more: the headers travel across by hand — including the ones <see cref="HttpClient"/>
    /// merged in from its defaults before the chain ran, which it will not merge again into a
    /// request the chain sends itself.
    /// </remarks>
    private static HttpRequestMessage CreateRedirect(
        HttpRequestMessage request,
        Uri target,
        AuthenticationHeaderValue credential)
    {
        var redirected = new HttpRequestMessage(request.Method, target)
        {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy,
        };

        foreach (var header in request.Headers)
        {
            // Authorization is re-attached below — or deliberately not attached at all.
            if (!string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase))
            {
                _ = redirected.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        // The retry counter rides along, so a failure after a redirect still reports how many
        // attempts the request really cost.
        foreach (var option in (IDictionary<string, object?>) request.Options)
        {
            ((IDictionary<string, object?>) redirected.Options)[option.Key] = option.Value;
        }

        if (CarriesCredential(target))
        {
            redirected.Headers.Authorization = credential;
        }

        return redirected;
    }

    /// <summary>
    /// Whether a redirect target may be given the credential: <c>https</c>, on exactly the Bitbucket
    /// API host, and nothing else.
    /// </summary>
    /// <remarks>
    /// Deliberately not <see cref="BitbucketCursor.IsBitbucketApiUrl"/>: that also pins the
    /// <c>/2.0/</c> prefix, which is the right guard for a URL the model supplied and the wrong one
    /// for a URL Bitbucket chose. All that matters here is that the credential cannot leave
    /// Bitbucket.
    /// </remarks>
    private static bool CarriesCredential(Uri target) =>
        string.Equals(target.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && string.Equals(target.Host, ApiHost, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The redirect statuses that are followed. <c>303</c> would normally rewrite the method to
    /// <c>GET</c>, which is a no-op here: only <c>GET</c> and <c>HEAD</c> ever reach this point.
    /// </summary>
    private static bool IsRedirect(HttpStatusCode statusCode) => statusCode
        is HttpStatusCode.MovedPermanently      // 301
        or HttpStatusCode.Found                 // 302
        or HttpStatusCode.SeeOther              // 303
        or HttpStatusCode.TemporaryRedirect     // 307
        or HttpStatusCode.PermanentRedirect;    // 308

    private static BitbucketApiException TooManyRedirects(HttpRequestMessage request, HttpStatusCode statusCode, Uri target)
    {
        request.Options.TryGetValue(RetryHandler.RetryAttemptsKey, out var attempts);

        return new BitbucketApiException(
            statusCode,
            new ErrorEnvelopeDto
            {
                Type = "error",
                Error = new ErrorDetailDto
                {
                    Message = $"Too many redirects (more than {MaxRedirects}): the last one pointed at {target}.",
                },
            },
            rawBody: null,
            attempts?.Value ?? 0);
    }
}
