using System.Reflection;

using Bitbucket.Mcp.Configuration;
using Bitbucket.Mcp.Http;

using ModelContextProtocol.Server;

// NUnit has a DescriptionAttribute of its own; the one tools carry is the ComponentModel one.
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;

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
    /// <summary>
    /// The design's tool list (AGENTS.md, *Tool table*), in full and in ordinal order. Adding a tool
    /// must mean editing this array, that table, <c>Build.cs</c>'s <c>ExpectedToolNames</c>, the
    /// shipped skill, <c>ToolSchemaTests</c>'s row and the README — six places, none optional.
    /// </summary>
    private static readonly string[] ExpectedToolNames =
    [
        "addPullRequestComment",
        "addPullRequestTask",
        "createPullRequest",
        "declinePullRequest",
        "getPipeline",
        "getPipelineStepLog",
        "getPullRequest",
        "getPullRequestComments",
        "getPullRequestDiff",
        "listCodeInsights",
        "listDefaultReviewers",
        "listPipelines",
        "listPullRequestStatuses",
        "listPullRequestTasks",
        "listPullRequests",
        "mergePullRequest",
        "resolvePullRequestComment",
        "setPullRequestReviewStatus",
        "updatePullRequest",
        "updatePullRequestTask",
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

    [Test]
    public void ExactlyTwentyToolsAreDeclaredAcrossTheThreeToolClasses()
    {
        Assert.That(ToolTestHost.ToolTypes.Count, Is.EqualTo(3));
        Assert.That(ToolTestHost.ToolMethods.Count, Is.EqualTo(ExpectedToolNames.Length));
        Assert.That(ToolTestHost.Tools.Count, Is.EqualTo(ExpectedToolNames.Length));
        Assert.That(ExpectedToolNames.Length, Is.EqualTo(20));
    }

    [Test]
    public void ToolNamesAreExactlyThePlannedOnes()
    {
        var actual = ToolTestHost.Tools.Select(tool => tool.ProtocolTool.Name).ToArray();

        Assert.That(actual, Is.EqualTo(ExpectedToolNames));
    }

    [TestCase("listPullRequests", "List pull requests")]
    [TestCase("getPullRequest", "Get pull request")]
    [TestCase("getPullRequestDiff", "Get pull request diff")]
    [TestCase("getPullRequestComments", "Get pull request comments")]
    [TestCase("listDefaultReviewers", "List default reviewers")]
    [TestCase("listPullRequestStatuses", "List pull request statuses")]
    [TestCase("listPullRequestTasks", "List pull request tasks")]
    [TestCase("createPullRequest", "Create pull request")]
    [TestCase("updatePullRequest", "Update pull request")]
    [TestCase("addPullRequestComment", "Add pull request comment")]
    [TestCase("resolvePullRequestComment", "Resolve pull request comment")]
    [TestCase("addPullRequestTask", "Add pull request task")]
    [TestCase("updatePullRequestTask", "Update pull request task")]
    [TestCase("setPullRequestReviewStatus", "Set pull request review status")]
    [TestCase("mergePullRequest", "Merge pull request")]
    [TestCase("declinePullRequest", "Decline pull request")]
    [TestCase("listPipelines", "List pipelines")]
    [TestCase("getPipeline", "Get pipeline")]
    [TestCase("getPipelineStepLog", "Get pipeline step log")]
    [TestCase("listCodeInsights", "List code insights")]
    public void TitleIsTheOneThePlanSpecifies(string name, string expectedTitle)
    {
        var tool = ToolTestHost.Find(name).ProtocolTool;

        Assert.That(tool.Title, Is.EqualTo(expectedTitle));
        Assert.That(tool.Annotations?.Title, Is.EqualTo(expectedTitle));
    }

    /// <summary>
    /// The annotation table from AGENTS.md, verbatim. A <see langword="null"/> destructive hint means
    /// the annotation is absent, which is what the seven read-only tools want: <c>readOnlyHint</c>
    /// already says they change nothing, and a destructive hint on a read tool is noise.
    /// </summary>
    [TestCase("listPullRequests", true, null, true, true)]
    [TestCase("getPullRequest", true, null, true, true)]
    [TestCase("getPullRequestDiff", true, null, true, true)]
    [TestCase("getPullRequestComments", true, null, true, true)]
    [TestCase("listDefaultReviewers", true, null, true, true)]
    [TestCase("listPullRequestStatuses", true, null, true, true)]
    [TestCase("listPullRequestTasks", true, null, true, true)]
    [TestCase("listPipelines", true, null, true, true)]
    [TestCase("getPipeline", true, null, true, true)]
    [TestCase("getPipelineStepLog", true, null, true, true)]
    [TestCase("listCodeInsights", true, null, true, true)]
    [TestCase("createPullRequest", false, false, false, true)]
    [TestCase("addPullRequestComment", false, false, false, true)]
    [TestCase("addPullRequestTask", false, false, false, true)]
    [TestCase("resolvePullRequestComment", false, false, true, true)]
    [TestCase("updatePullRequestTask", false, false, true, true)]
    [TestCase("setPullRequestReviewStatus", false, false, true, true)]
    [TestCase("updatePullRequest", false, true, false, true)]
    [TestCase("mergePullRequest", false, true, false, true)]
    [TestCase("declinePullRequest", false, true, false, true)]
    public void AnnotationsMatchThePlanTable(
        string name,
        bool readOnly,
        bool? destructive,
        bool idempotent,
        bool openWorld)
    {
        var annotations = ToolTestHost.Find(name).ProtocolTool.Annotations;

        Assert.That(annotations, Is.Not.Null);
        Assert.That(annotations.ReadOnlyHint, Is.EqualTo(readOnly));
        Assert.That(annotations.DestructiveHint, Is.EqualTo(destructive));
        Assert.That(annotations.IdempotentHint, Is.EqualTo(idempotent));
        Assert.That(annotations.OpenWorldHint, Is.EqualTo(openWorld));
    }

    [Test]
    public void EveryToolAdvertisesStructuredOutput()
    {
        foreach (var tool in ToolTestHost.Tools)
        {
            Assert.That(tool.ProtocolTool.OutputSchema is not null, Is.True, $"{tool.ProtocolTool.Name} has no output schema; UseStructuredContent must be true.");
        }
    }

    /// <summary>
    /// Sealed, not <c>static</c>: C# forbids a static class as the type argument of
    /// <c>WithTools&lt;T&gt;</c> (CS0718). The private constructor is what keeps it uninstantiable
    /// anyway, and the methods themselves must be static (D8) so no instance is activated per call.
    /// </summary>
    [Test]
    public void ToolClassesAreSealedAndUninstantiable()
    {
        foreach (var type in ToolTestHost.ToolTypes)
        {
            Assert.That(type.IsSealed, Is.True, $"{type.Name} must be sealed.");
            Assert.That(type.GetCustomAttribute<McpServerToolTypeAttribute>(), Is.Not.Null);

            var publicConstructors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
            Assert.That(publicConstructors, Is.Empty);
        }
    }

    [Test]
    public void EveryToolMethodIsPublicStaticAndReturnsATaskOfAShapedResult()
    {
        foreach (var method in ToolTestHost.ToolMethods)
        {
            Assert.That(method.IsPublic, Is.True, $"{method.Name} must be public.");
            Assert.That(method.IsStatic, Is.True, $"{method.Name} must be static (D8).");

            Assert.That(method.ReturnType.IsGenericType
                && method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>), Is.True, $"{method.Name} must return Task<T>, not {method.ReturnType.Name}.");

            var resultType = method.ReturnType.GetGenericArguments()[0];

            Assert.That(resultType.Namespace, Is.EqualTo("Bitbucket.Mcp.Tools.Models"));
        }
    }

    [Test]
    public void EveryToolMethodHasADescription()
    {
        foreach (var method in ToolTestHost.ToolMethods)
        {
            var description = method.GetCustomAttribute<DescriptionAttribute>()?.Description;

            Assert.That(string.IsNullOrWhiteSpace(description), Is.False, $"{method.Name} needs a [Description]; it is what the model reads to choose the tool.");
        }
    }

    [Test]
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

                Assert.That(string.IsNullOrWhiteSpace(description), Is.False, $"{method.Name}({parameter.Name}) needs a [Description]; it becomes the schema description.");
            }
        }
    }

    /// <summary>
    /// The token is the SDK's cancellation wiring, and a defaulted trailing parameter is the only
    /// shape that keeps every other argument callable by name and position from a test.
    /// </summary>
    [Test]
    public void CancellationTokenIsTheLastParameterOfEveryTool()
    {
        foreach (var method in ToolTestHost.ToolMethods)
        {
            var parameters = method.GetParameters();
            var last = parameters[^1];

            Assert.That(last.ParameterType, Is.EqualTo(typeof(CancellationToken)));
            Assert.That(last.HasDefaultValue, Is.True, $"{method.Name}'s cancellationToken must be optional.");

            Assert.That(
                parameters[..^1].Select(parameter => parameter.ParameterType),
                Has.None.EqualTo(typeof(CancellationToken)));
        }
    }

    /// <summary>
    /// Both collaborators are plain parameters the container binds (D8); every tool takes them, and
    /// takes them first, so the model-facing arguments are the tail of the signature.
    /// </summary>
    [Test]
    public void EveryToolTakesItsCollaboratorsAsTheFirstTwoParameters()
    {
        foreach (var method in ToolTestHost.ToolMethods)
        {
            var parameters = method.GetParameters();

            Assert.That(parameters[0].ParameterType, Is.EqualTo(typeof(BitbucketApiClient)));
            Assert.That(parameters[1].ParameterType, Is.EqualTo(typeof(BitbucketMcpOptions)));
        }
    }
}
