using Bitbucket.Mcp.Authentication;
using Bitbucket.Mcp.Configuration;

using Microsoft.Extensions.Logging;

namespace Bitbucket.Mcp.Cli;

/// <summary>
/// <c>bitbucket-mcp status</c> — what the server would do if it started right now: which credential
/// wins, where the token cache is, and what is in it.
/// </summary>
/// <remarks>
/// <para>
/// This is the first thing to run when the server says it cannot authenticate, so it is deliberately
/// exhaustive about configuration and deliberately silent about values. It prints which variables
/// are set, never their contents: no token, no secret, and not even the consumer key — the file it
/// describes is the one an attacker would want, and a status readout has a habit of ending up in a
/// bug report.
/// </para>
/// <para>
/// It always exits 0. "Nothing is configured" is a fact this command reports, not a failure of it,
/// and a status probe that fails the shell is a nuisance in exactly the scripts that would use one.
/// </para>
/// </remarks>
internal static class StatusCommand
{
    private const string NotSet = "(not set)";

    internal static async Task<int> RunAsync(BitbucketMcpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        using var loggerFactory = CliRuntime.CreateLoggerFactory(options);

        var tokenStore = new TokenStore(options, loggerFactory, TimeProvider.System);

        Console.Out.WriteLine($"{ServerVersion.Name} {ServerVersion.Value}");
        Console.Out.WriteLine();
        Console.Out.WriteLine("Configuration");
        Console.Out.WriteLine($"  Auth mode:         {DescribeMode(options)}");
        Console.Out.WriteLine($"  OAuth consumer:    {DescribeConsumer(options)}");
        Console.Out.WriteLine($"  Callback URL:      {InteractiveAuthenticator.BuildRedirectUri(options)}");
        Console.Out.WriteLine($"  Browser sign-in:   {DescribeInteractive(options, loggerFactory)}");
        Console.Out.WriteLine($"  Default workspace: {options.DefaultWorkspace ?? NotSet}");

        Console.Out.WriteLine();
        Console.Out.WriteLine("Token cache");
        Console.Out.WriteLine($"  File:              {tokenStore.FilePath}");

        await WriteCacheAsync(options, tokenStore).ConfigureAwait(false);

        return CliDispatcher.ExitSuccess;
    }

    /// <summary>
    /// The credential the server would pick, using the same precedence
    /// <see cref="CredentialProviderFactory"/> applies at startup: bearer token, then API token, then
    /// OAuth.
    /// </summary>
    private static string DescribeMode(BitbucketMcpOptions options)
    {
        if (CredentialProviderFactory.CreateStatic(options) is { } staticCredential)
        {
            // Describe() names the variable it came from and never any part of the credential.
            return staticCredential.Describe();
        }

        var configured = !string.IsNullOrEmpty(options.OAuthKey) && !string.IsNullOrEmpty(options.OAuthSecret);

        return configured
            ? "OAuth 2.0 browser flow (BITBUCKET_OAUTH_KEY + BITBUCKET_OAUTH_SECRET)"
            : "none - set BITBUCKET_ACCESS_TOKEN, or BITBUCKET_EMAIL + BITBUCKET_API_TOKEN, or BITBUCKET_OAUTH_KEY + BITBUCKET_OAUTH_SECRET";
    }

    private static string DescribeConsumer(BitbucketMcpOptions options) =>
        (string.IsNullOrEmpty(options.OAuthKey), string.IsNullOrEmpty(options.OAuthSecret)) switch
        {
            (false, false) => "key and secret configured",
            (false, true) => "key configured, BITBUCKET_OAUTH_SECRET missing",
            (true, false) => "secret configured, BITBUCKET_OAUTH_KEY missing",
            _ => NotSet,
        };

    /// <summary>
    /// Whether a browser flow could be started, from the authenticator itself so this cannot drift
    /// from what the server will actually do. <c>IsAvailable</c> performs no I/O by contract.
    /// </summary>
    private static string DescribeInteractive(BitbucketMcpOptions options, ILoggerFactory loggerFactory)
    {
        if (new InteractiveAuthenticator(options, loggerFactory).IsAvailable)
        {
            return "available";
        }

        return options.NoBrowser
            ? "disabled (BITBUCKET_MCP_NO_BROWSER is set)"
            : "unavailable (no OAuth consumer configured)";
    }

    /// <summary>
    /// Reports what is in the cache. A cache that cannot be decoded reads back as absent — that is
    /// the token store's documented behaviour, and it is the honest answer here too, because a file
    /// the server will ignore is a file the user does not have.
    /// </summary>
    private static async Task WriteCacheAsync(BitbucketMcpOptions options, TokenStore tokenStore)
    {
        var tokens = await tokenStore.LoadAsync(CancellationToken.None).ConfigureAwait(false);

        if (tokens is null)
        {
            Console.Out.WriteLine("  Cached tokens:     none - run `bitbucket-mcp login`");
            return;
        }

        Console.Out.WriteLine("  Cached tokens:     present");

        Console.Out.WriteLine($"  Obtained with:     {DescribeConsumerMatch(options, tokens)}");

        Console.Out.WriteLine(string.IsNullOrEmpty(tokens.AccessToken)
            ? "  Access token:      absent"
            : $"  Access token:      {CliRuntime.FormatExpiry(tokens.ExpiresAtUtc, DateTimeOffset.UtcNow)}");

        Console.Out.WriteLine($"  Refresh token:     {(string.IsNullOrEmpty(tokens.RefreshToken) ? "absent" : "present")}");
        Console.Out.WriteLine($"  Scopes:            {tokens.Scopes ?? "(not reported by Bitbucket)"}");
    }

    /// <summary>
    /// Whether the cache was obtained with the consumer key that is configured now. Only the
    /// fingerprint is ever compared — the key itself is not stored, by design.
    /// </summary>
    private static string DescribeConsumerMatch(BitbucketMcpOptions options, TokenSet tokens)
    {
        var fingerprint = OAuthCredentialProvider.ComputeFingerprint(options.OAuthKey);

        if (fingerprint is null)
        {
            return "unknown (no BITBUCKET_OAUTH_KEY set to compare against)";
        }

        return string.Equals(tokens.ConsumerKeyFingerprint, fingerprint, StringComparison.Ordinal)
            ? "matches the configured consumer key"
            : "a different consumer key - the cache will be ignored";
    }
}
