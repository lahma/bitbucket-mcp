using System.ComponentModel;
using System.Reflection;

using Bitbucket.Mcp.Configuration;
using Bitbucket.Mcp.Http;

using ModelContextProtocol.Server;

using Xunit;

namespace Bitbucket.Mcp.Tests.Tools;

/// <summary>
/// The tool surface as a contract: which tools exist, what they are called, and what an MCP client
/// is told about each one before it decides whether to ask the user first.
/// </summary>
/// <remarks>
/// <para>
/// The annotation table is the load-bearing part. <c>Destructive</c> defaults to
/// <see langword="true"/> in the SDK, so a write tool that forgets to say otherwise makes clients
/// prompt before every comment; a destructive tool that says <c>false</c> makes them merge without
/// asking. Neither shows up in any other test, and neither would be noticed until it was noticed by
/// a user.
/// </para>
/// <para>
/// Everything here is asserted against the values an MCP client actually receives — the
/// <see cref="McpServerTool.ProtocolTool"/> built through the SDK with the production serializer
/// options — rather than against the attribute, so an SDK change in how attributes become
/// annotations cannot pass unnoticed.
/// </para>
/// </remarks>
public class ToolInventoryTests
{
    /// <summary>The plan's tool list, in full. Adding a tool must mean editing this line.</summary>
    private static readonly string[] ExpectedToolNames =
    [
        "addPullRequestComment",
        "createPullRequest",
        "declinePullRequest",
        "getPullRequest",
        "getPullRequestComments",
        "getPullRequestDiff",
        "listPullRequests",
        "mergePullRequest",
        "setPullRequestReviewStatus",
        "updatePullRequest",
    ];

    /// <summary>
    /// The parameter types the SDK binds from DI or from the protocol, and therefore leaves out of
    /// the generated schema (D8). Everything else is a model-supplied argument.
    /// </summary>
    private static readonly Type[] InjectedParameterTypes =
    [
        typeof(BitbucketApiClient),
        typeof(BitbucketMcpOptions),
        typeof(CancellationToken),
    ];

    [Fact]
    public void ExactlyTenToolsAreDeclaredAcrossTheTwoToolClasses()
    {
        Assert.Equal(2, ToolTestHost.ToolTypes.Count);
        Assert.Equal(10, ToolTestHost.ToolMethods.Count);
        Assert.Equal(10, ToolTestHost.Tools.Count);
    }

    [Fact]
    public void ToolNamesAreExactlyThePlannedOnes()
    {
        var actual = ToolTestHost.Tools.Select(tool => tool.ProtocolTool.Name).ToArray();

        Assert.Equal(ExpectedToolNames, actual);
    }

    [Theory]
    [InlineData("listPullRequests", "List pull requests")]
    [InlineData("getPullRequest", "Get pull request")]
    [InlineData("getPullRequestDiff", "Get pull request diff")]
    [InlineData("getPullRequestComments", "Get pull request comments")]
    [InlineData("createPullRequest", "Create pull request")]
    [InlineData("updatePullRequest", "Update pull request")]
    [InlineData("addPullRequestComment", "Add pull request comment")]
    [InlineData("setPullRequestReviewStatus", "Set pull request review status")]
    [InlineData("mergePullRequest", "Merge pull request")]
    [InlineData("declinePullRequest", "Decline pull request")]
    public void TitleIsTheOneThePlanSpecifies(string name, string expectedTitle)
    {
        var tool = ToolTestHost.Find(name).ProtocolTool;

        Assert.Equal(expectedTitle, tool.Title);
        Assert.Equal(expectedTitle, tool.Annotations?.Title);
    }

    /// <summary>
    /// The annotation table from the plan, verbatim. A <see langword="null"/> destructive hint means
    /// the annotation is absent, which is what the four read-only tools want: <c>readOnlyHint</c>
    /// already says they change nothing, and a destructive hint on a read tool is noise.
    /// </summary>
    [Theory]
    [InlineData("listPullRequests", true, null, true, true)]
    [InlineData("getPullRequest", true, null, true, true)]
    [InlineData("getPullRequestDiff", true, null, true, true)]
    [InlineData("getPullRequestComments", true, null, true, true)]
    [InlineData("createPullRequest", false, false, false, true)]
    [InlineData("addPullRequestComment", false, false, false, true)]
    [InlineData("setPullRequestReviewStatus", false, false, true, true)]
    [InlineData("updatePullRequest", false, true, false, true)]
    [InlineData("mergePullRequest", false, true, false, true)]
    [InlineData("declinePullRequest", false, true, false, true)]
    public void AnnotationsMatchThePlanTable(
        string name,
        bool readOnly,
        bool? destructive,
        bool idempotent,
        bool openWorld)
    {
        var annotations = ToolTestHost.Find(name).ProtocolTool.Annotations;

        Assert.NotNull(annotations);
        Assert.Equal(readOnly, annotations.ReadOnlyHint);
        Assert.Equal(destructive, annotations.DestructiveHint);
        Assert.Equal(idempotent, annotations.IdempotentHint);
        Assert.Equal(openWorld, annotations.OpenWorldHint);
    }

    [Fact]
    public void EveryToolAdvertisesStructuredOutput()
    {
        foreach (var tool in ToolTestHost.Tools)
        {
            Assert.True(
                tool.ProtocolTool.OutputSchema is not null,
                $"{tool.ProtocolTool.Name} has no output schema; UseStructuredContent must be true.");
        }
    }

    /// <summary>
    /// Sealed, not <c>static</c>: C# forbids a static class as the type argument of
    /// <c>WithTools&lt;T&gt;</c> (CS0718). The private constructor is what keeps it uninstantiable
    /// anyway, and the methods themselves must be static (D8) so no instance is activated per call.
    /// </summary>
    [Fact]
    public void ToolClassesAreSealedAndUninstantiable()
    {
        foreach (var type in ToolTestHost.ToolTypes)
        {
            Assert.True(type.IsSealed, $"{type.Name} must be sealed.");
            Assert.NotNull(type.GetCustomAttribute<McpServerToolTypeAttribute>());

            var publicConstructors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
            Assert.Empty(publicConstructors);
        }
    }

    [Fact]
    public void EveryToolMethodIsPublicStaticAndReturnsATaskOfAShapedResult()
    {
        foreach (var method in ToolTestHost.ToolMethods)
        {
            Assert.True(method.IsPublic, $"{method.Name} must be public.");
            Assert.True(method.IsStatic, $"{method.Name} must be static (D8).");

            Assert.True(
                method.ReturnType.IsGenericType
                && method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>),
                $"{method.Name} must return Task<T>, not {method.ReturnType.Name}.");

            var resultType = method.ReturnType.GetGenericArguments()[0];

            Assert.Equal("Bitbucket.Mcp.Tools.Models", resultType.Namespace);
        }
    }

    [Fact]
    public void EveryToolMethodHasADescription()
    {
        foreach (var method in ToolTestHost.ToolMethods)
        {
            var description = method.GetCustomAttribute<DescriptionAttribute>()?.Description;

            Assert.False(
                string.IsNullOrWhiteSpace(description),
                $"{method.Name} needs a [Description]; it is what the model reads to choose the tool.");
        }
    }

    [Fact]
    public void EveryModelSuppliedParameterHasADescription()
    {
        foreach (var method in ToolTestHost.ToolMethods)
        {
            foreach (var parameter in method.GetParameters())
            {
                if (InjectedParameterTypes.Contains(parameter.ParameterType))
                {
                    continue;
                }

                var description = parameter.GetCustomAttribute<DescriptionAttribute>()?.Description;

                Assert.False(
                    string.IsNullOrWhiteSpace(description),
                    $"{method.Name}({parameter.Name}) needs a [Description]; it becomes the schema description.");
            }
        }
    }

    /// <summary>
    /// The token is the SDK's cancellation wiring, and a defaulted trailing parameter is the only
    /// shape that keeps every other argument callable by name and position from a test.
    /// </summary>
    [Fact]
    public void CancellationTokenIsTheLastParameterOfEveryTool()
    {
        foreach (var method in ToolTestHost.ToolMethods)
        {
            var parameters = method.GetParameters();
            var last = parameters[^1];

            Assert.Equal(typeof(CancellationToken), last.ParameterType);
            Assert.True(last.HasDefaultValue, $"{method.Name}'s cancellationToken must be optional.");

            Assert.DoesNotContain(
                parameters[..^1],
                parameter => parameter.ParameterType == typeof(CancellationToken));
        }
    }

    /// <summary>
    /// Both collaborators are plain parameters the container binds (D8); every tool takes them, and
    /// takes them first, so the model-facing arguments are the tail of the signature.
    /// </summary>
    [Fact]
    public void EveryToolTakesItsCollaboratorsAsTheFirstTwoParameters()
    {
        foreach (var method in ToolTestHost.ToolMethods)
        {
            var parameters = method.GetParameters();

            Assert.Equal(typeof(BitbucketApiClient), parameters[0].ParameterType);
            Assert.Equal(typeof(BitbucketMcpOptions), parameters[1].ParameterType);
        }
    }
}
