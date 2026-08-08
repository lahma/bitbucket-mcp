using System.Runtime.InteropServices;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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

        // D3: a bare ServiceCollection, not Host.CreateApplicationBuilder - no configuration
        // providers, metrics or lifetime machinery in the cold-start path.
        var services = new ServiceCollection();

        services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Information);
            logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        });

        // T10: options / TokenStore / ICredentialProvider / BitbucketApiClient registrations.

        // T10: the tool-facing serializer options. Ours goes FIRST in the chain (D6) so that JIT
        // and AOT resolve identically; the SDK resolver stays second for MCP protocol types.
        // var jsonOptions = new JsonSerializerOptions(McpJsonUtilities.DefaultOptions);
        // jsonOptions.TypeInfoResolverChain.Clear();
        // jsonOptions.TypeInfoResolverChain.Add(BitbucketToolJsonContext.Default);
        // jsonOptions.TypeInfoResolverChain.Add(McpJsonUtilities.DefaultOptions.TypeInfoResolver!);
        // jsonOptions.MakeReadOnly();

        services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation
                {
                    Name = ServerVersion.Name,
                    Version = ServerVersion.Value,
                };

                // T10: options.ServerInstructions = ServerInstructions.Text;

                // A non-null tool collection is what makes the server advertise the `tools`
                // capability and answer `tools/list`. `??=` so that once WithTools<T>() registers
                // real tools (T10) the collection built from DI wins.
                options.ToolCollection ??= new McpServerPrimitiveCollection<McpServerTool>();
            })
            .WithStdioServerTransport();

        // T10: one WithTools<T>(jsonOptions) per tool class - never WithToolsFromAssembly (IL2026).
        // .WithTools<PullRequestReadTools>(jsonOptions)
        // .WithTools<PullRequestWriteTools>(jsonOptions);

        await using var provider = services.BuildServiceProvider();

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
