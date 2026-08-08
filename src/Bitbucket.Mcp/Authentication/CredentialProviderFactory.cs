using Bitbucket.Mcp.Configuration;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Bitbucket.Mcp.Authentication;

/// <summary>
/// Picks the credential the server will use, from the environment alone.
/// </summary>
/// <remarks>
/// <para>
/// Precedence, highest first:
/// </para>
/// <list type="number">
/// <item><description><c>BITBUCKET_ACCESS_TOKEN</c> — a workspace, project or repository access token, sent as <c>Bearer</c>;</description></item>
/// <item><description><c>BITBUCKET_EMAIL</c> + <c>BITBUCKET_API_TOKEN</c> — an Atlassian API token, sent as <c>Basic</c>;</description></item>
/// <item><description>otherwise the OAuth browser flow.</description></item>
/// </list>
/// <para>
/// The OAuth provider is returned <em>even when no consumer key and secret are configured</em>.
/// That is the point: this method must not decide that authentication is impossible, because it
/// runs while the container is being built and there is nowhere to report a failure — stdout is the
/// protocol channel. A server with no credentials at all still completes the MCP handshake, still
/// lists its tools, and fails on the first tool call with an
/// <see cref="AuthenticationRequiredException"/> that says exactly which variables to set.
/// </para>
/// <para>
/// Nothing here reads the environment directly (the options object has already done that, once) and
/// nothing here touches the disk or the network.
/// </para>
/// </remarks>
internal static class CredentialProviderFactory
{
    /// <summary>
    /// The container entry point: <c>services.AddSingleton&lt;ICredentialProvider&gt;(CredentialProviderFactory.Create)</c>.
    /// </summary>
    internal static ICredentialProvider Create(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = services.GetRequiredService<BitbucketMcpOptions>();

        return (ICredentialProvider?) CreateStatic(options)
            ?? new OAuthCredentialProvider(
                options,
                services.GetRequiredService<TokenStore>(),
                services.GetRequiredService<OAuthTokenClient>(),
                services.GetRequiredService<IInteractiveAuthenticator>(),
                services.GetRequiredService<TimeProvider>(),
                services.GetRequiredService<ILoggerFactory>());
    }

    /// <summary>
    /// The environment-token half of the precedence rules, or <see langword="null"/> when OAuth
    /// applies. Separated out so the precedence itself is testable without a container.
    /// </summary>
    internal static StaticCredentialProvider? CreateStatic(BitbucketMcpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!string.IsNullOrEmpty(options.AccessToken))
        {
            return StaticCredentialProvider.ForBearerToken(options.AccessToken);
        }

        // Both halves or neither: an email without a token cannot authenticate anything, and
        // silently falling back to OAuth would be less confusing than a half-configured Basic
        // header that always 401s.
        if (!string.IsNullOrEmpty(options.Email) && !string.IsNullOrEmpty(options.ApiToken))
        {
            return StaticCredentialProvider.ForApiToken(options.Email, options.ApiToken);
        }

        return null;
    }
}
