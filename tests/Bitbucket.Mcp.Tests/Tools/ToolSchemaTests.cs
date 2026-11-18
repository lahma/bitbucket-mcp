using System.Text.Json;

using Bitbucket.Mcp.Tools.Models;


namespace Bitbucket.Mcp.Tests.Tools;

/// <summary>
/// The JSON schemas an MCP client receives from <c>tools/list</c>, generated with the server's own
/// serializer options.
/// </summary>
/// <remarks>
/// <para>
/// A schema is the only description of a tool the model ever sees, so the failures worth catching
/// here are the silent ones: an injected collaborator leaking in as a required argument the model
/// then has to invent, a parameter losing its description, or the naming policy slipping so that
/// the schema says <c>PageSize</c> while the tool binds <c>pageSize</c>.
/// </para>
/// <para>
/// The options come from <see cref="McpServerSetup.CreateToolSerializerOptions"/> — the same call
/// the server makes — because a schema generated with different options is not evidence about the
/// shipped one.
/// </para>
/// </remarks>
public class ToolSchemaTests
{
    /// <summary>Parameter names the SDK must never expose: they are bound from DI or the protocol.</summary>
    private static readonly string[] NeverInSchema = ["client", "options", "progress", "cancellationToken"];

    /// <summary>
    /// D6, asserted where it matters: the schemas above are only the shipped ones if the options
    /// that produced them are the shipped ones — our context first, the SDK's resolver second, and
    /// the whole thing frozen.
    /// </summary>
    [Test]
    public void ProductionSerializerOptionsPutOurContextFirstAndAreReadOnly()
    {
        var options = McpServerSetup.CreateToolSerializerOptions();

        Assert.That(options.IsReadOnly, Is.True);
        Assert.That(options.TypeInfoResolverChain.Count, Is.EqualTo(2));
        Assert.That(options.TypeInfoResolverChain[0], Is.TypeOf<BitbucketToolJsonContext>());
    }

    [TestCase(
        "listPullRequests",
        "repository,workspace,state,author,sourceBranch,reviewer,destinationBranch,pageSize,cursor",
        "repository")]
    [TestCase(
        "listPipelines",
        "repository,workspace,targetBranch,commit,status,pageSize,cursor",
        "repository")]
    [TestCase(
        "getPipeline",
        "repository,pipeline,workspace",
        "repository,pipeline")]
    [TestCase(
        "getPipelineStepLog",
        "repository,pipeline,stepUuid,workspace,mode,pattern,contextLines,maxLines",
        "repository,pipeline,stepUuid")]
    [TestCase(
        "listCodeInsights",
        "repository,commit,workspace,reportId,maxAnnotations,pageSize,cursor",
        "repository,commit")]
    [TestCase(
        "listDefaultReviewers",
        "repository,workspace,pageSize,cursor",
        "repository")]
    [TestCase(
        "listPullRequestStatuses",
        "repository,pullRequestId,workspace,pageSize,cursor",
        "repository,pullRequestId")]
    [TestCase(
        "listPullRequestTasks",
        "repository,pullRequestId,workspace,pageSize,cursor",
        "repository,pullRequestId")]
    [TestCase(
        "updatePullRequestComment",
        "repository,pullRequestId,commentId,content,delete,workspace",
        "repository,pullRequestId,commentId")]
    [TestCase(
        "resolvePullRequestComment",
        "repository,pullRequestId,commentId,resolved,workspace",
        "repository,pullRequestId,commentId,resolved")]
    [TestCase(
        "addPullRequestTask",
        "repository,pullRequestId,content,workspace,commentId",
        "repository,pullRequestId,content")]
    [TestCase(
        "updatePullRequestTask",
        "repository,pullRequestId,taskId,workspace,state,content",
        "repository,pullRequestId,taskId")]
    [TestCase(
        "getPullRequest",
        "repository,pullRequestId,workspace",
        "repository,pullRequestId")]
    [TestCase(
        "getPullRequestDiff",
        "repository,pullRequestId,workspace,mode,paths,contextLines,ignoreWhitespace,maxLinesPerFile,cursor",
        "repository,pullRequestId")]
    [TestCase(
        "getPullRequestComments",
        "repository,pullRequestId,workspace,unresolvedOnly,pageSize,cursor",
        "repository,pullRequestId")]
    [TestCase(
        "getPullRequestMergeability",
        "repository,pullRequestId,workspace",
        "repository,pullRequestId")]
    [TestCase(
        "createPullRequest",
        "repository,title,sourceBranch,workspace,destinationBranch,description,reviewers,closeSourceBranch,draft",
        "repository,title,sourceBranch")]
    [TestCase(
        "updatePullRequest",
        "repository,pullRequestId,workspace,title,description,destinationBranch,reviewers,closeSourceBranch,draft",
        "repository,pullRequestId")]
    [TestCase(
        "addPullRequestComment",
        "repository,pullRequestId,content,workspace,parentCommentId,path,line,lineType,startLine,codeSnippet",
        "repository,pullRequestId,content")]
    [TestCase(
        "setPullRequestReviewStatus",
        "repository,pullRequestId,status,workspace,comment",
        "repository,pullRequestId,status")]
    [TestCase(
        "mergePullRequest",
        "repository,pullRequestId,workspace,mergeStrategy,message,closeSourceBranch",
        "repository,pullRequestId")]
    [TestCase(
        "declinePullRequest",
        "repository,pullRequestId,workspace,reason",
        "repository,pullRequestId")]
    public void InputSchemaExposesExactlyTheModelSuppliedArguments(string name, string properties, string required)
    {
        var schema = ToolTestHost.Find(name).ProtocolTool.InputSchema;

        Assert.That(schema.GetProperty("type").GetString(), Is.EqualTo("object"));

        var actualProperties = schema.GetProperty("properties")
            .EnumerateObject()
            .Select(property => property.Name)
            .ToArray();

        Assert.That(actualProperties, Is.EqualTo(properties.Split(',')));

        var actualRequired = schema.TryGetProperty("required", out var requiredElement)
            ? requiredElement.EnumerateArray().Select(item => item.GetString()).ToArray()
            : [];

        Assert.That(actualRequired, Is.EqualTo(required.Split(',')));
    }

    [Test]
    public void NoInputSchemaMentionsAnInjectedParameter()
    {
        foreach (var tool in ToolTestHost.Tools)
        {
            foreach (var property in tool.ProtocolTool.InputSchema.GetProperty("properties").EnumerateObject())
            {
                Assert.That(NeverInSchema, Has.No.Member(property.Name).Using((IEqualityComparer<string>) StringComparer.OrdinalIgnoreCase));
            }
        }
    }

    [Test]
    public void EveryInputPropertyIsCamelCaseAndDescribed()
    {
        foreach (var tool in ToolTestHost.Tools)
        {
            var toolName = tool.ProtocolTool.Name;

            foreach (var property in tool.ProtocolTool.InputSchema.GetProperty("properties").EnumerateObject())
            {
                Assert.That(IsCamelCase(property.Name), Is.True, $"{toolName}.{property.Name} is not camelCase.");

                var described = property.Value.TryGetProperty("description", out var description)
                    && !string.IsNullOrWhiteSpace(description.GetString());

                Assert.That(described, Is.True, $"{toolName}.{property.Name} has no schema description.");
            }
        }
    }

    [Test]
    public void EveryToolHasAnObjectOutputSchemaInCamelCase()
    {
        foreach (var tool in ToolTestHost.Tools)
        {
            var outputSchema = tool.ProtocolTool.OutputSchema;

            Assert.That(outputSchema, Is.Not.Null);
            Assert.That(outputSchema.Value.GetProperty("type").GetString(), Is.EqualTo("object"));

            AssertCamelCaseProperties(outputSchema.Value, tool.ProtocolTool.Name);
        }
    }

    /// <summary>
    /// The one tool with two shapes: <c>mode</c> chooses, and both payloads have to be describable
    /// by the single output schema the tool advertises.
    /// </summary>
    [Test]
    public void DiffOutputSchemaCarriesBothModes()
    {
        var outputSchema = ToolTestHost.Find("getPullRequestDiff").ProtocolTool.OutputSchema;

        Assert.That(outputSchema, Is.Not.Null);

        var properties = outputSchema.Value.GetProperty("properties");

        Assert.That(properties.TryGetProperty("mode", out _), Is.True);
        Assert.That(properties.TryGetProperty("diffstat", out _), Is.True);
        Assert.That(properties.TryGetProperty("diff", out _), Is.True);
    }

    /// <summary>Every paginated result has to hand the caller a way to ask for the next page.</summary>
    [TestCase("listPullRequests", "nextCursor")]
    [TestCase("getPullRequestComments", "nextCursor")]
    [TestCase("listDefaultReviewers", "nextCursor")]
    [TestCase("listPullRequestStatuses", "nextCursor")]
    [TestCase("listPullRequestTasks", "nextCursor")]
    [TestCase("listPipelines", "nextCursor")]
    [TestCase("listCodeInsights", "nextCursor")]
    public void PaginatedOutputSchemasCarryTheCursor(string name, string cursorProperty)
    {
        var outputSchema = ToolTestHost.Find(name).ProtocolTool.OutputSchema;

        Assert.That(outputSchema, Is.Not.Null);
        Assert.That(outputSchema.Value.GetProperty("properties").TryGetProperty(cursorProperty, out _), Is.True);
    }

    private static void AssertCamelCaseProperties(JsonElement schema, string toolName)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (schema.TryGetProperty("properties", out var properties)
            && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in properties.EnumerateObject())
            {
                Assert.That(IsCamelCase(property.Name), Is.True, $"{toolName} output property '{property.Name}' is not camelCase.");

                AssertCamelCaseProperties(property.Value, toolName);
            }
        }

        if (schema.TryGetProperty("items", out var items))
        {
            AssertCamelCaseProperties(items, toolName);
        }
    }

    /// <summary>
    /// Hand-rolled rather than a regex: the rule is small, and the failure message matters more
    /// than the pattern.
    /// </summary>
    private static bool IsCamelCase(string name)
    {
        if (name.Length == 0 || !char.IsLower(name[0]))
        {
            return false;
        }

        foreach (var character in name)
        {
            if (!char.IsLetterOrDigit(character))
            {
                return false;
            }
        }

        return true;
    }
}
