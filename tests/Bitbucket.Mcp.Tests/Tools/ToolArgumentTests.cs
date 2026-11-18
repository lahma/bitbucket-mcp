using System.IO.Pipelines;
using System.Text.Json;

using Bitbucket.Mcp.Tools;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Bitbucket.Mcp.Tests.Tools;

/// <summary>
/// A call that names an argument its tool does not take is refused with the name, instead of the
/// SDK dropping it: a mis-cased optional argument would otherwise be ignored without a word, and a
/// mis-cased <c>workspace</c> would quietly fall back to <c>BITBUCKET_DEFAULT_WORKSPACE</c>.
/// </summary>
public class ToolArgumentTests
{
    /// <summary>
    /// The guard must never refuse a name the client was told to send, or it breaks a working call.
    /// Checked against every tool's generated schema, so a parameter added later is covered too.
    /// </summary>
    [Test]
    public void EveryToolAcceptsEveryArgumentItsSchemaNames()
    {
        foreach (var tool in ToolTestHost.Tools)
        {
            var names = tool.ProtocolTool.InputSchema.GetProperty("properties").EnumerateObject().Select(property => property.Name);

            Assert.That(ToolArguments.Refuse(tool.ProtocolTool, names), Is.Null, tool.ProtocolTool.Name);
        }
    }

    [Test]
    public void AMisCasedRequiredArgumentIsRefusedWithTheSpellingMeant()
    {
        var refusal = ToolArguments.Refuse(GetPullRequest, ["Repository", "pullRequestId", "workspace"]);

        Assert.That(refusal, Does.StartWith("getPullRequest has no argument named 'Repository' (did you mean 'repository'?)."));
        Assert.That(refusal, Does.Contain("Argument names are case-sensitive; getPullRequest takes repository, pullRequestId, workspace."));
        Assert.That(refusal, Does.EndWith("Nothing was sent to Bitbucket."));
    }

    /// <summary>The case the SDK alone gets silently wrong: an optional argument it cannot place.</summary>
    [Test]
    public void AMisCasedOptionalArgumentIsRefusedRatherThanIgnored()
    {
        var refusal = ToolArguments.Refuse(GetPullRequest, ["repository", "pullRequestId", "Workspace"]);

        Assert.That(refusal, Does.StartWith("getPullRequest has no argument named 'Workspace' (did you mean 'workspace'?)."));
    }

    [Test]
    public void AnUnknownArgumentIsRefusedWithTheArgumentsTheToolTakes()
    {
        var refusal = ToolArguments.Refuse(GetPullRequest, ["repo_slug", "pullRequestId", "Workspace"]);

        Assert.That(refusal, Does.StartWith("getPullRequest has no argument named 'repo_slug' or 'Workspace' (did you mean 'workspace'?)."));
        Assert.That(refusal, Does.Not.Contain("'repo_slug' (did you mean"));
        Assert.That(refusal, Does.Contain("takes repository, pullRequestId, workspace"));
    }

    /// <summary>No arguments at all is the tool's to answer: it is what says which required one is missing.</summary>
    [Test]
    public void ACallWithNoArgumentsIsLeftToTheTool()
    {
        Assert.That(ToolArguments.Refuse(GetPullRequest, null), Is.Null);
        Assert.That(ToolArguments.Refuse(GetPullRequest, []), Is.Null);
    }

    /// <summary>
    /// Through the SDK, over the server's own wiring: the filter answers before the tool runs, as a
    /// tool error the model can act on, and nothing reaches Bitbucket.
    /// </summary>
    [Test]
    public async Task TheServerRefusesAMisCasedArgumentBeforeTheToolRuns()
    {
        using var handler = new StubHttpMessageHandler();
        await using var server = await InMemoryServer.StartAsync(handler);

        var result = await server.Client.CallToolAsync(
            "getPullRequest",
            new Dictionary<string, object?>
            {
                ["repository"] = Json("\"widgets\""),
                ["pullRequestId"] = Json("42"),
                ["Workspace"] = Json("\"acme\""),
            },
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(result.IsError, Is.True);
        Assert.That(Expect.Single(result.Content.OfType<TextContentBlock>()).Text, Does.Contain("'Workspace' (did you mean 'workspace'?)"));
        Assert.That(handler.Requests, Is.Empty);
    }

    /// <summary>A call spelled the way the schema says still reaches the tool and Bitbucket.</summary>
    [Test]
    public async Task TheServerPassesACorrectlyNamedCallThrough()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(ToolFixtures.PullRequestDetail);
        await using var server = await InMemoryServer.StartAsync(handler);

        var result = await server.Client.CallToolAsync(
            "getPullRequest",
            new Dictionary<string, object?>
            {
                ["repository"] = Json("\"widgets\""),
                ["pullRequestId"] = Json("42"),
                ["workspace"] = Json("\"acme\""),
            },
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(result.IsError, Is.Not.True);
        Assert.That(Expect.Single(handler.Requests).Uri!.AbsolutePath, Does.EndWith("/repositories/acme/widgets/pullrequests/42"));
    }

    /// <summary>
    /// What a client adds beside the arguments — a progress token in <c>_meta</c>, here — travels in
    /// the call's <c>params</c>, not in <c>arguments</c>, so the guard never sees it.
    /// </summary>
    [Test]
    public async Task AProgressTokenIsNotTakenForAnArgument()
    {
        using var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(ToolFixtures.PullRequestDetail);
        await using var server = await InMemoryServer.StartAsync(handler);

        var result = await server.Client.CallToolAsync(
            "getPullRequest",
            new Dictionary<string, object?>
            {
                ["repository"] = Json("\"widgets\""),
                ["pullRequestId"] = Json("42"),
                ["workspace"] = Json("\"acme\""),
            },
            progress: new IgnoredProgress(),
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(result.IsError, Is.Not.True);
        Assert.That(handler.Requests, Has.Exactly(1).Items);
    }

    private static Tool GetPullRequest => ToolTestHost.Find("getPullRequest").ProtocolTool;

    private sealed class IgnoredProgress : IProgress<ProgressNotificationValue>
    {
        public void Report(ProgressNotificationValue value)
        {
        }
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// The production tool registration (<see cref="McpServerSetup.WithBitbucketTools"/>) served over
    /// a pair of in-memory pipes, with a real <see cref="McpClient"/> on the other end.
    /// </summary>
    private sealed class InMemoryServer : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly CancellationTokenSource _stop;
        private readonly Task _running;

        private InMemoryServer(ServiceProvider provider, CancellationTokenSource stop, Task running, McpClient client)
        {
            _provider = provider;
            _stop = stop;
            _running = running;
            Client = client;
        }

        internal McpClient Client { get; }

        internal static async Task<InMemoryServer> StartAsync(StubHttpMessageHandler handler)
        {
            var clientToServer = new Pipe();
            var serverToClient = new Pipe();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(ToolTestHost.CreateOptions());
            services.AddSingleton(_ => ToolTestHost.CreateClient(handler));
            services
                .AddMcpServer()
                .WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream())
                .WithBitbucketTools(McpServerSetup.CreateToolSerializerOptions());

            var provider = services.BuildServiceProvider();
            var stop = new CancellationTokenSource();
            var running = provider.GetRequiredService<McpServer>().RunAsync(stop.Token);

            var client = await McpClient.CreateAsync(
                new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream(), NullLoggerFactory.Instance),
                cancellationToken: TestContext.CurrentContext.CancellationToken);

            return new InMemoryServer(provider, stop, running, client);
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await _stop.CancelAsync();

            try
            {
                await _running;
            }
            catch (OperationCanceledException)
            {
                // The server loop ends by cancellation; that is the normal shutdown.
            }

            _stop.Dispose();
            await _provider.DisposeAsync();
        }
    }
}
