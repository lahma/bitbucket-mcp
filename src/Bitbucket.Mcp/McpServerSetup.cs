using System.Runtime.InteropServices;
using System.Text.Json;

using Bitbucket.Mcp.Authentication;
using Bitbucket.Mcp.Configuration;
using Bitbucket.Mcp.Http;
using Bitbucket.Mcp.Tools;
using Bitbucket.Mcp.Tools.Models;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Bitbucket.Mcp;

/// <summary>
/// Builds and runs the stdio MCP server.
/// </summary>
/// <remarks>
/// Nothing in this file — or anything it starts — may write to stdout: stdout is the JSON-RPC
/// channel and a stray write corrupts the protocol stream. Logging goes to stderr.
/// </remarks>
internal static class McpServerSetup
{
    /// <summary>
    /// Runs the server until stdin closes or the process is asked to shut down, then returns the
    /// process exit code.
    /// </summary>
    internal static async Task<int> RunStdioAsync()
    {
        using var shutdown = new CancellationTokenSource();

        // Cooperative shutdown: cancel the server loop rather than letting the runtime tear the
        // process down mid-write (D3 - there is no generic host to own lifetime for us).
        using var sigInt = RegisterShutdownSignal(PosixSignal.SIGINT, shutdown);
        using var sigTerm = RegisterShutdownSignal(PosixSignal.SIGTERM, shutdown);

        // Environment variables only (D3). This is the one and only read; everything downstream
        // takes the resulting options object.
        var options = BitbucketMcpOptions.FromEnvironment();

        // D3: a bare ServiceCollection, not Host.CreateApplicationBuilder - no configuration
        // providers, metrics or lifetime machinery in the cold-start path.
        var services = new ServiceCollection();

        services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(options.LogLevel);
            logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        });

        // Every registration below is an explicit factory rather than AddSingleton<T>(): the
        // constructors are internal (which the container's reflection-based selection does not
        // see), and writing the graph out by hand keeps it reflection-free for AOT and readable as
        // the wiring diagram it is.
        //
        // None of these run at startup. They are all constructed on the first tool call, and
        // constructing them still touches neither disk nor network - authentication happens inside
        // GetAuthenticationHeaderAsync, where a failure can be reported to the caller.
        services.AddSingleton(options);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(sp => new TokenStore(
            sp.GetRequiredService<BitbucketMcpOptions>(),
            sp.GetRequiredService<ILoggerFactory>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new OAuthTokenClient(
            sp.GetRequiredService<BitbucketMcpOptions>(),
            sp.GetRequiredService<ILoggerFactory>(),
            sp.GetRequiredService<TimeProvider>()));

        // The browser flow: loopback listener plus browser launcher. Constructing it binds no
        // socket and launches nothing - that happens inside AuthorizeAsync, which only the refresh
        // state machine calls, and only after every non-interactive option has been exhausted.
        services.AddSingleton<IInteractiveAuthenticator>(sp => new InteractiveAuthenticator(
            sp.GetRequiredService<BitbucketMcpOptions>(),
            sp.GetRequiredService<ILoggerFactory>()));

        services.AddSingleton(CredentialProviderFactory.Create);
        services.AddSingleton(sp => new BitbucketApiClient(
            sp.GetRequiredService<ICredentialProvider>(),
            sp.GetRequiredService<ILoggerFactory>()));

        var jsonOptions = CreateToolSerializerOptions();

        services
            .AddMcpServer(serverOptions =>
            {
                serverOptions.ServerInfo = new Implementation
                {
                    Name = ServerVersion.Name,
                    Version = ServerVersion.Value,
                };

                // Sent to the client at initialize: the conventions no single tool description can
                // carry (slugs, diffstat-first, opaque cursors, reviewer UUIDs).
                serverOptions.ServerInstructions = ServerInstructions.Text;
            })
            .WithStdioServerTransport()
            // One WithTools<T>(jsonOptions) per tool class - never WithToolsFromAssembly (IL2026).
            // These also populate the tool collection, which is what makes the server advertise the
            // `tools` capability and answer `tools/list`.
            .WithTools<PullRequestReadTools>(jsonOptions)
            .WithTools<PullRequestWriteTools>(jsonOptions);

        await using var provider = services.BuildServiceProvider();

        // The error funnel's only dependency. Resolved here rather than passed into every tool
        // method, so that no tool signature carries a parameter the schema then has to exclude.
        ToolErrors.UseLoggerFactory(provider.GetRequiredService<ILoggerFactory>());

        // The SDK registers McpServer as a singleton; running it directly is the SDK's own AOT
        // test-app shape (D3).
        var server = provider.GetRequiredService<McpServer>();

        try
        {
            await server.RunAsync(shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            // Signalled shutdown is a normal exit.
        }

        return Cli.CliDispatcher.ExitSuccess;
    }

    /// <summary>
    /// Builds the tool-facing serializer options that every <c>WithTools&lt;T&gt;</c> registration —
    /// and therefore every generated tool schema — is created with.
    /// </summary>
    /// <remarks>
    /// Ours goes FIRST in the chain (D6) so that JIT and AOT resolve identically; the SDK resolver
    /// stays second for MCP protocol types, which our context returns null for. The chain is cleared
    /// first because copying the SDK's options copies its chain too, and a duplicate entry ahead of
    /// ours would decide the tie.
    /// <para>
    /// Factored out of <see cref="RunStdioAsync"/> so the schema tests can generate schemas with the
    /// exact options the server ships, rather than a hand-rolled copy that could drift out of step.
    /// </para>
    /// </remarks>
    internal static JsonSerializerOptions CreateToolSerializerOptions()
    {
        var jsonOptions = new JsonSerializerOptions(McpJsonUtilities.DefaultOptions);
        jsonOptions.TypeInfoResolverChain.Clear();
        jsonOptions.TypeInfoResolverChain.Add(BitbucketToolJsonContext.Default);
        jsonOptions.TypeInfoResolverChain.Add(McpJsonUtilities.DefaultOptions.TypeInfoResolver!);
        jsonOptions.MakeReadOnly();

        return jsonOptions;
    }

    private static PosixSignalRegistration? RegisterShutdownSignal(PosixSignal signal, CancellationTokenSource shutdown)
    {
        try
        {
            return PosixSignalRegistration.Create(signal, context =>
            {
                // Suppress the default action (immediate termination) and unwind the server loop.
                context.Cancel = true;

                try
                {
                    shutdown.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // Already shutting down.
                }
            });
        }
        catch (PlatformNotSupportedException)
        {
            return null;
        }
    }
}
