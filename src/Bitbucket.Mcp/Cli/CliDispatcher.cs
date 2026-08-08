using Bitbucket.Mcp.Configuration;

namespace Bitbucket.Mcp.Cli;

/// <summary>
/// Hand-rolled argv dispatch (D15). No <c>System.CommandLine</c>: the surface is four verbs, and
/// the package budget is a hard constraint.
/// </summary>
/// <remarks>
/// This namespace is the only place in the server that may write to stdout — in server mode
/// stdout <em>is</em> the MCP protocol channel.
/// </remarks>
internal static class CliDispatcher
{
    /// <summary>Command completed successfully.</summary>
    internal const int ExitSuccess = 0;

    /// <summary>Command ran but failed.</summary>
    internal const int ExitFailure = 1;

    /// <summary>The command line could not be understood.</summary>
    internal const int ExitUsage = 2;

    internal static string UsageText { get; } =
        $"""
         {ServerVersion.Name} {ServerVersion.Value} - Model Context Protocol server for Bitbucket Cloud.

         Usage:
           {ServerVersion.Name} [serve]     Run the MCP server over stdio (default when no arguments are given).
           {ServerVersion.Name} login       Authenticate with Bitbucket via the OAuth browser flow.
           {ServerVersion.Name} logout      Delete the cached OAuth tokens.
           {ServerVersion.Name} status      Show the current authentication status.

         Options:
           -h, --help                     Show this help text.
           -v, --version                  Show the version.

         Configuration is environment variables only. The ones authentication needs:
           BITBUCKET_ACCESS_TOKEN         Bearer token; highest precedence, bypasses OAuth.
           BITBUCKET_EMAIL                With BITBUCKET_API_TOKEN, an Atlassian API token (Basic).
           BITBUCKET_API_TOKEN
           BITBUCKET_OAUTH_KEY            OAuth consumer key and secret for the browser flow.
           BITBUCKET_OAUTH_SECRET
           BITBUCKET_OAUTH_CALLBACK_HOST  Callback URL the consumer is registered with
           BITBUCKET_OAUTH_CALLBACK_PORT    (default http://127.0.0.1:33418/callback).
           BITBUCKET_MCP_TOKEN_FILE       Override the token cache location.
           BITBUCKET_MCP_NO_BROWSER       Set to 1 to never launch a browser.

         Run `{ServerVersion.Name} status` to see which of these are in effect; the README documents
         the full set.
         """;

    /// <summary>Dispatches <paramref name="args"/> and returns the process exit code.</summary>
    internal static async Task<int> RunAsync(string[] args)
    {
        // No arguments is the common case: MCP clients launch the binary with none at all.
        var command = args.Length == 0 ? "serve" : args[0];

        switch (command)
        {
            case "serve":
                return await McpServerSetup.RunStdioAsync().ConfigureAwait(false);

            case "version":
            case "--version":
            case "-v":
                Console.Out.WriteLine(ServerVersion.Value);
                return ExitSuccess;

            case "help":
            case "--help":
            case "-h":
                Console.Out.WriteLine(UsageText);
                return ExitSuccess;

            // The three CLI modes (D15). Each reads the environment itself, exactly as the server
            // does, so that what `status` reports is what `serve` would use.
            case "login":
                return await LoginCommand.RunAsync(BitbucketMcpOptions.FromEnvironment()).ConfigureAwait(false);

            case "logout":
                return await LogoutCommand.RunAsync(BitbucketMcpOptions.FromEnvironment()).ConfigureAwait(false);

            case "status":
                return await StatusCommand.RunAsync(BitbucketMcpOptions.FromEnvironment()).ConfigureAwait(false);

            default:
                Console.Error.WriteLine($"{ServerVersion.Name}: unknown argument '{command}'.");
                Console.Error.WriteLine();
                Console.Error.WriteLine(UsageText);
                return ExitUsage;
        }
    }
}
