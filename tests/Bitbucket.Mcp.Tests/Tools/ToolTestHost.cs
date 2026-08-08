using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

using Bitbucket.Mcp.Authentication;
using Bitbucket.Mcp.Configuration;
using Bitbucket.Mcp.Http;
using Bitbucket.Mcp.Tools;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Server;

namespace Bitbucket.Mcp.Tests.Tools;

/// <summary>
/// Shared scaffolding for the tool-layer tests: the two tool classes, the ten
/// <see cref="McpServerTool"/> instances built exactly the way the server builds them, and the
/// stubbed collaborators the tool methods take as plain parameters (D8).
/// </summary>
/// <remarks>
/// <para>
/// The tools are constructed through <see cref="McpServerTool.Create(MethodInfo, object?, McpServerToolCreateOptions)"/>
/// with <c>Services</c> and <c>SerializerOptions</c> set the same way
/// <c>WithTools&lt;T&gt;(jsonOptions)</c> sets them, so the schemas and annotations these tests
/// inspect are byte-for-byte the ones an MCP client receives from <c>tools/list</c>. The serializer
/// options come from <see cref="McpServerSetup.CreateToolSerializerOptions"/> — the production
/// factory itself, not a copy that could drift.
/// </para>
/// <para>
/// The service provider only has to contain the types the tool methods expect to be injected: the
/// SDK asks <c>IServiceProviderIsService</c> which parameters to leave out of the schema, so a
/// missing registration would show up as an extra schema property rather than as a failure to bind.
/// </para>
/// </remarks>
internal static class ToolTestHost
{
    /// <summary>The two classes registered with <c>WithTools&lt;T&gt;</c>.</summary>
    internal static IReadOnlyList<Type> ToolTypes { get; } =
        [typeof(PullRequestReadTools), typeof(PullRequestWriteTools)];

    /// <summary>Every <c>[McpServerTool]</c> method, discovered the way the SDK discovers them.</summary>
    internal static IReadOnlyList<MethodInfo> ToolMethods { get; } = DiscoverToolMethods();

    /// <summary>The built tools, ordered by MCP name so failures name a stable tool.</summary>
    internal static IReadOnlyList<McpServerTool> Tools { get; } = BuildTools();

    /// <summary>The production tool-facing serializer options.</summary>
    internal static JsonSerializerOptions SerializerOptions { get; } = McpServerSetup.CreateToolSerializerOptions();

    /// <summary>Finds a built tool by its MCP name.</summary>
    internal static McpServerTool Find(string name) =>
        Tools.Single(tool => string.Equals(tool.ProtocolTool.Name, name, StringComparison.Ordinal));

    /// <summary>Finds a tool method by the MCP name its attribute declares.</summary>
    internal static MethodInfo FindMethod(string name) =>
        ToolMethods.Single(method =>
            string.Equals(method.GetCustomAttribute<McpServerToolAttribute>()!.Name, name, StringComparison.Ordinal));

    /// <summary>
    /// A client over a stub transport. The base address stays the real one so that pagination
    /// cursors — which are validated against <c>api.bitbucket.org</c> — behave as in production.
    /// </summary>
    /// <param name="transport">The innermost handler.</param>
    /// <param name="credentialProvider">Supplies the <c>Authorization</c> header per request.</param>
    /// <param name="timeProvider">
    /// The retry pipeline's clock. Left unset these tests run on the real one, which is only safe
    /// because every throttled case they drive answers <c>Retry-After: 0</c> or gives up without
    /// waiting; a case that would sit in the backoff schedule passes a fake clock instead.
    /// </param>
    internal static BitbucketApiClient CreateClient(
        HttpMessageHandler transport,
        ICredentialProvider? credentialProvider = null,
        TimeProvider? timeProvider = null) =>
        new(credentialProvider ?? new StubCredentialProvider(),
            NullLoggerFactory.Instance,
            transport,
            baseAddress: null,
            timeProvider);

    /// <summary>Options with nothing configured, as if every environment variable were unset.</summary>
    internal static BitbucketMcpOptions CreateOptions(string? defaultWorkspace = "acme") =>
        BitbucketMcpOptions.FromEnvironment(static _ => null) with { DefaultWorkspace = defaultWorkspace };

    private static List<MethodInfo> DiscoverToolMethods()
    {
        var methods = new List<MethodInfo>();

        foreach (var type in ToolTypes)
        {
            // The same binding flags McpServerBuilderExtensions.WithTools<T> uses, so this test's
            // idea of "the tools" cannot be narrower than the server's.
            foreach (var method in type.GetMethods(
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (method.GetCustomAttribute<McpServerToolAttribute>() is not null)
                {
                    methods.Add(method);
                }
            }
        }

        // Reflection order is not contractual; sort so a failure message always names the same tool.
        methods.Sort(static (left, right) => string.CompareOrdinal(left.Name, right.Name));
        return methods;
    }

    private static List<McpServerTool> BuildTools()
    {
        var services = new ServiceCollection();
        services.AddSingleton(BitbucketMcpOptions.FromEnvironment(static _ => null));
        services.AddSingleton(_ => new BitbucketApiClient(
            new StubCredentialProvider(),
            NullLoggerFactory.Instance,
            new StubHttpMessageHandler()));

        var provider = services.BuildServiceProvider();
        var serializerOptions = McpServerSetup.CreateToolSerializerOptions();

        var tools = new List<McpServerTool>(ToolMethods.Count);

        foreach (var method in ToolMethods)
        {
            tools.Add(McpServerTool.Create(
                method,
                target: null,
                new McpServerToolCreateOptions
                {
                    Services = provider,
                    SerializerOptions = serializerOptions,
                }));
        }

        tools.Sort(static (left, right) => string.CompareOrdinal(left.ProtocolTool.Name, right.ProtocolTool.Name));
        return tools;
    }
}

/// <summary>
/// The Bitbucket responses the tool tests are driven with, written out the way the API writes
/// them: <c>fields=</c>-trimmed, snake_case, and with the <c>next</c> link every paginated field
/// set is required to ask for.
/// </summary>
internal static class ToolFixtures
{
    /// <summary>The <c>next</c> URL a page carries, and therefore what a cursor has to decode to.</summary>
    internal const string NextPageUrl =
        "https://api.bitbucket.org/2.0/repositories/acme/widgets/pullrequests?page=2";

    /// <summary>One page of pull requests with another page behind it.</summary>
    internal const string PullRequestPage = $$"""
        {
          "size": 2,
          "next": "{{NextPageUrl}}",
          "values": [
            {
              "id": 42,
              "title": "Clamp the widget size",
              "state": "OPEN",
              "draft": false,
              "created_on": "2026-08-01T09:00:00+00:00",
              "updated_on": "2026-08-02T10:30:00+00:00",
              "comment_count": 3,
              "task_count": 0,
              "author": { "display_name": "Ada Lovelace", "uuid": "{11111111-2222-3333-4444-555555555555}", "nickname": "ada" },
              "source": { "branch": { "name": "feature/clamp" } },
              "destination": { "branch": { "name": "main" } }
            }
          ]
        }
        """;

    /// <summary>One pull request in full, with a reviewer whose stance lives in the participant list.</summary>
    internal const string PullRequestDetail = """
        {
          "id": 42,
          "title": "Clamp the widget size",
          "state": "OPEN",
          "draft": false,
          "description": "Negative sizes crashed the renderer.",
          "created_on": "2026-08-01T09:00:00+00:00",
          "updated_on": "2026-08-02T10:30:00+00:00",
          "comment_count": 3,
          "task_count": 0,
          "close_source_branch": true,
          "author": { "display_name": "Ada Lovelace", "uuid": "{11111111-2222-3333-4444-555555555555}" },
          "source": { "branch": { "name": "feature/clamp" } },
          "destination": { "branch": { "name": "main" } },
          "reviewers": [
            { "display_name": "Grace Hopper", "uuid": "{99999999-8888-7777-6666-555555555555}" }
          ],
          "participants": [
            {
              "role": "REVIEWER",
              "approved": true,
              "state": "approved",
              "user": { "display_name": "Grace Hopper", "uuid": "{99999999-8888-7777-6666-555555555555}" }
            }
          ]
        }
        """;

    /// <summary>A merged pull request, as the merge endpoint answers.</summary>
    internal const string MergedPullRequest = """
        {
          "id": 42,
          "title": "Clamp the widget size",
          "state": "MERGED",
          "merge_commit": { "hash": "abc123def456" }
        }
        """;

    /// <summary>A declined pull request, as the decline endpoint answers.</summary>
    internal const string DeclinedPullRequest = """
        {
          "id": 42,
          "title": "Clamp the widget size",
          "state": "DECLINED",
          "reason": "Superseded by #43"
        }
        """;

    /// <summary>One page of comments, including a deleted one that must not reach the caller.</summary>
    internal const string CommentPage = $$"""
        {
          "size": 3,
          "next": "{{NextPageUrl}}",
          "values": [
            {
              "id": 1001,
              "created_on": "2026-08-01T11:00:00+00:00",
              "deleted": false,
              "content": { "raw": "Nice catch." },
              "user": { "display_name": "Grace Hopper", "uuid": "{99999999-8888-7777-6666-555555555555}" }
            },
            {
              "id": 1002,
              "created_on": "2026-08-01T11:05:00+00:00",
              "deleted": true,
              "content": { "raw": "" },
              "user": { "display_name": "Grace Hopper", "uuid": "{99999999-8888-7777-6666-555555555555}" }
            },
            {
              "id": 1003,
              "created_on": "2026-08-01T11:10:00+00:00",
              "deleted": false,
              "content": { "raw": "Should this clamp the upper bound too?" },
              "user": { "display_name": "Ada Lovelace", "uuid": "{11111111-2222-3333-4444-555555555555}" },
              "parent": { "id": 1001 },
              "inline": { "path": "src/Widget.cs", "to": 13 },
              "resolution": { "created_on": "2026-08-01T12:00:00+00:00" }
            }
          ]
        }
        """;

    /// <summary>A freshly posted comment, as the create endpoint answers.</summary>
    internal const string CreatedComment = """
        {
          "id": 2001,
          "created_on": "2026-08-03T08:00:00+00:00",
          "content": { "raw": "Please clamp the upper bound as well." },
          "user": { "display_name": "Grace Hopper", "uuid": "{99999999-8888-7777-6666-555555555555}" }
        }
        """;

    /// <summary>The caller's own participant entry, as approve and request-changes answer.</summary>
    internal const string ApprovedParticipant = """
        {
          "role": "REVIEWER",
          "approved": true,
          "state": "approved",
          "user": { "display_name": "Grace Hopper", "uuid": "{99999999-8888-7777-6666-555555555555}" }
        }
        """;

    /// <summary>One page of diffstat entries.</summary>
    internal const string DiffStatPage = $$"""
        {
          "size": 1,
          "next": "{{NextPageUrl}}",
          "values": [
            {
              "status": "modified",
              "lines_added": 2,
              "lines_removed": 1,
              "old": { "path": "src/Widget.cs" },
              "new": { "path": "src/Widget.cs" }
            }
          ]
        }
        """;

    /// <summary>
    /// One file's unified diff. Line 13 of the new file is the added <c>Math.Clamp</c> line, which
    /// is what the inline-anchor tests aim at.
    /// </summary>
    internal const string SingleFileDiff =
        "diff --git a/src/Widget.cs b/src/Widget.cs\n" +
        "index 1111111..2222222 100644\n" +
        "--- a/src/Widget.cs\n" +
        "+++ b/src/Widget.cs\n" +
        "@@ -10,6 +10,7 @@ public sealed class Widget\n" +
        "     public int Size { get; init; }\n" +
        " \n" +
        "-    public void Resize(int size) => Size = size;\n" +
        "+    public void Resize(int size) => Size = Math.Clamp(size, 0, MaxSize);\n" +
        "+\n" +
        "     public override string ToString() => $\"Widget({Size})\";\n" +
        " }\n";
}

/// <summary>
/// A credential provider that hands out a fixed header, or throws a prepared failure — enough to
/// drive both the happy path and the "you are not signed in" branch of the error funnel.
/// </summary>
internal sealed class StubCredentialProvider : ICredentialProvider
{
    private static readonly AuthenticationHeaderValue Header = new("Bearer", "stub-token");

    private readonly Exception? _failure;

    internal StubCredentialProvider(Exception? failure = null) => _failure = failure;

    /// <summary>How many times a 401 caused the credential to be discarded.</summary>
    internal int InvalidateCount { get; private set; }

    /// <inheritdoc />
    public ValueTask<AuthenticationHeaderValue> GetAuthenticationHeaderAsync(CancellationToken cancellationToken)
    {
        if (_failure is not null)
        {
            throw _failure;
        }

        return ValueTask.FromResult(Header);
    }

    /// <inheritdoc />
    public ValueTask InvalidateAsync(CancellationToken cancellationToken)
    {
        InvalidateCount++;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public string Describe() => "stub credential";
}
