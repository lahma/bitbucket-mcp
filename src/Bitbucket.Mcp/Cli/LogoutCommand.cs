using Bitbucket.Mcp.Authentication;
using Bitbucket.Mcp.Configuration;

namespace Bitbucket.Mcp.Cli;

/// <summary>
/// <c>bitbucket-mcp logout</c> — deletes the cached OAuth tokens.
/// </summary>
/// <remarks>
/// <para>
/// Idempotent, and it says which of the two things happened: deleting a cache that was not there is
/// a success, but a user who expected one deleted deserves to know the file was already absent (very
/// often because <c>BITBUCKET_MCP_TOKEN_FILE</c> points somewhere else than they assume).
/// </para>
/// <para>
/// It does not revoke the grant at Bitbucket's end — there is no revocation endpoint in the OAuth 2.0
/// surface Bitbucket Cloud exposes. Removing the consumer under workspace settings is what actually
/// invalidates it, and the message says so.
/// </para>
/// </remarks>
internal static class LogoutCommand
{
    internal static async Task<int> RunAsync(BitbucketMcpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        using var loggerFactory = CliRuntime.CreateLoggerFactory(options);

        var tokenStore = new TokenStore(options, loggerFactory, TimeProvider.System);

        var deleted = await tokenStore.DeleteAsync(CancellationToken.None).ConfigureAwait(false);

        Console.Out.WriteLine(deleted
            ? $"Deleted the cached OAuth tokens ({tokenStore.FilePath})."
            : $"No cached OAuth tokens to delete ({tokenStore.FilePath}).");

        if (deleted)
        {
            Console.Out.WriteLine(
                "The grant itself still exists at Bitbucket; remove the OAuth consumer under workspace settings to "
                + "revoke it.");
        }

        return CliDispatcher.ExitSuccess;
    }
}
