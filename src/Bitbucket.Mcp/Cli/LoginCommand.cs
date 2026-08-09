using Bitbucket.Mcp.Authentication;
using Bitbucket.Mcp.Configuration;

namespace Bitbucket.Mcp.Cli;

/// <summary>
/// <c>bitbucket-mcp login</c> — the documented first-run step: run the OAuth browser flow once, in a
/// terminal, and leave a refreshable token cache behind for the server to use.
/// </summary>
/// <remarks>
/// <para>
/// It builds the same object graph the server builds, minus the MCP server itself, and calls the
/// same <see cref="OAuthCredentialProvider.SignInAsync"/>. Nothing about the token exchange or the
/// cache is reimplemented here — a second code path for persisting a single-use refresh token is the
/// last thing this project needs.
/// </para>
/// <para>
/// Doing this once up front is why the mid-tool-call browser wait is rare: by the time an MCP client
/// launches the server, a valid grant is already on disk.
/// </para>
/// </remarks>
internal static class LoginCommand
{
    internal static async Task<int> RunAsync(BitbucketMcpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var oauthConfigured = !string.IsNullOrEmpty(options.OAuthKey) && !string.IsNullOrEmpty(options.OAuthSecret);
        var staticCredential = CredentialProviderFactory.CreateStatic(options);

        if (!oauthConfigured)
        {
            return staticCredential is null
                ? ReportMissingConsumer(options)
                : ReportNothingToDo(staticCredential.Describe());
        }

        if (options.NoBrowser)
        {
            // Caught here rather than three layers down, so the message names the command the user
            // actually typed instead of describing a refresh state machine they never asked about.
            Console.Error.WriteLine(
                $"{ServerVersion.Name}: BITBUCKET_MCP_NO_BROWSER is set, so `login` cannot open the browser it needs.");
            Console.Error.WriteLine(
                "Unset it and run `bitbucket-mcp login` again, or authenticate with BITBUCKET_ACCESS_TOKEN (or "
                + "BITBUCKET_EMAIL together with BITBUCKET_API_TOKEN) instead.");

            return CliDispatcher.ExitFailure;
        }

        if (staticCredential is not null)
        {
            // Not an error: signing in is still useful (the cached grant renews itself, where an
            // environment token has to be rotated by hand), but the server would ignore the result
            // while the variable is set.
            Console.Out.WriteLine(
                $"Note: {staticCredential.Describe()} takes precedence over OAuth, so the server will not use these "
                + "tokens until that variable is unset.");
            Console.Out.WriteLine();
        }

        using var loggerFactory = CliRuntime.CreateLoggerFactory(options);

        var tokenStore = new TokenStore(options, loggerFactory, TimeProvider.System);
        using var tokenClient = new OAuthTokenClient(options, loggerFactory, TimeProvider.System);

        var interactive = new InteractiveAuthenticator(options, loggerFactory);

        using var credentials = new OAuthCredentialProvider(
            options,
            tokenStore,
            tokenClient,
            interactive,
            TimeProvider.System,
            loggerFactory);

        using var interrupt = CliRuntime.CreateInterruptTokenSource(
            static () => Console.Error.WriteLine("Cancelling the sign-in."));

        Console.Out.WriteLine($"Waiting for authorization at {InteractiveAuthenticator.BuildRedirectUri(options)} ...");

        try
        {
            var tokens = await credentials.SignInAsync(interrupt.Token).ConfigureAwait(false);

            Console.Out.WriteLine();
            Console.Out.WriteLine("Signed in to Bitbucket.");
            Console.Out.WriteLine($"  Scopes:       {tokens.Scopes ?? "(not reported by Bitbucket)"}");
            Console.Out.WriteLine($"  Access token: {CliRuntime.FormatExpiry(tokens.ExpiresAtUtc, DateTimeOffset.UtcNow)}");
            Console.Out.WriteLine($"  Token file:   {tokenStore.FilePath}");

            if (string.IsNullOrEmpty(tokens.RefreshToken))
            {
                Console.Out.WriteLine();
                Console.Out.WriteLine(
                    "Bitbucket returned no refresh token, so this grant cannot be renewed silently. Check that the "
                    + "OAuth consumer is not marked \"This is a private consumer\" with an implicit grant.");
            }

            return CliDispatcher.ExitSuccess;
        }
        catch (AuthenticationRequiredException ex)
        {
            Console.Error.WriteLine($"{ServerVersion.Name}: sign-in failed. {ex.Message}");

            if (ex.AuthorizeUrl is { Length: > 0 } url)
            {
                Console.Error.WriteLine($"Authorization URL: {url}");
            }

            return CliDispatcher.ExitFailure;
        }
        catch (HttpRequestException ex)
        {
            Console.Error.WriteLine($"{ServerVersion.Name}: could not reach Bitbucket. {ex.Message}");

            return CliDispatcher.ExitFailure;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine($"{ServerVersion.Name}: sign-in cancelled.");

            return CliDispatcher.ExitFailure;
        }
    }

    /// <summary>
    /// The first-run failure: no consumer credentials at all. Says exactly which variables to set and
    /// where the values come from — this is the message that has to substitute for documentation.
    /// </summary>
    private static int ReportMissingConsumer(BitbucketMcpOptions options)
    {
        Console.Error.WriteLine($"{ServerVersion.Name}: no OAuth consumer is configured, so there is nothing to sign in with.");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Set both of these environment variables and run `bitbucket-mcp login` again:");
        Console.Error.WriteLine("  BITBUCKET_OAUTH_KEY      the key of your Bitbucket OAuth consumer");
        Console.Error.WriteLine("  BITBUCKET_OAUTH_SECRET   its secret");
        Console.Error.WriteLine();
        Console.Error.WriteLine(
            "Create the consumer in your *workspace* settings - not your personal/account settings, and not on "
            + "admin.atlassian.com:");
        Console.Error.WriteLine("  https://bitbucket.org/{workspace}/workspace/settings/api");
        Console.Error.WriteLine("  (Apps and features > OAuth consumers > Add consumer; {workspace} is the URL slug.)");
        Console.Error.WriteLine($"  Callback URL   {InteractiveAuthenticator.BuildRedirectUri(options)}");
        Console.Error.WriteLine("  Permissions    Account: Read, Repositories: Read+Write, Pull requests: Read+Write");
        Console.Error.WriteLine("See the \"OAuth consumer setup\" section of the README for the walkthrough.");
        Console.Error.WriteLine();
        Console.Error.WriteLine(
            "Alternatively, skip OAuth entirely: set BITBUCKET_ACCESS_TOKEN, or BITBUCKET_EMAIL together with "
            + "BITBUCKET_API_TOKEN.");

        return CliDispatcher.ExitFailure;
    }

    /// <summary>The user already has a working credential and no consumer. Nothing failed.</summary>
    private static int ReportNothingToDo(string description)
    {
        Console.Out.WriteLine($"Already configured: {description}.");
        Console.Out.WriteLine("An OAuth sign-in is not needed - that credential takes precedence over OAuth.");
        Console.Out.WriteLine(
            "To use the OAuth browser flow instead, set BITBUCKET_OAUTH_KEY and BITBUCKET_OAUTH_SECRET (and unset the "
            + "variable above), then run `bitbucket-mcp login`.");

        return CliDispatcher.ExitSuccess;
    }
}
