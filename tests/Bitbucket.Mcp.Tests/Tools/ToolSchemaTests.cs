using System.Text.Json;

using Bitbucket.Mcp.Tools.Models;

using Xunit;

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
    private static readonly string[] NeverInSchema = ["client", "options", "cancellationToken"];

    /// <summary>
    /// D6, asserted where it matters: the schemas above are only the shipped ones if the options
    /// that produced them are the shipped ones — our context first, the SDK's resolver second, and
    /// the whole thing frozen.
    /// </summary>
    [Fact]
    public void ProductionSerializerOptionsPutOurContextFirstAndAreReadOnly()
    {
        var options = McpServerSetup.CreateToolSerializerOptions();

        Assert.True(options.IsReadOnly);
        Assert.Equal(2, options.TypeInfoResolverChain.Count);
        Assert.IsType<BitbucketToolJsonContext>(options.TypeInfoResolverChain[0]);
    }

    [Theory]
    [InlineData(
        "listPullRequests",
        "repository,workspace,state,author,pageSize,cursor",
        "repository")]
    [InlineData(
        "getPullRequest",
        "repository,pullRequestId,workspace",
        "repository,pullRequestId")]
    [InlineData(
        "getPullRequestDiff",
        "repository,pullRequestId,workspace,mode,paths,contextLines,ignoreWhitespace,maxLinesPerFile,cursor",
        "repository,pullRequestId")]
    [InlineData(
        "getPullRequestComments",
        "repository,pullRequestId,workspace,pageSize,cursor",
        "repository,pullRequestId")]
    [InlineData(
        "createPullRequest",
        "repository,title,sourceBranch,workspace,destinationBranch,description,reviewers,closeSourceBranch,draft",
        "repository,title,sourceBranch")]
    [InlineData(
        "updatePullRequest",
        "repository,pullRequestId,workspace,title,description,destinationBranch,reviewers",
        "repository,pullRequestId")]
    [InlineData(
        "addPullRequestComment",
        "repository,pullRequestId,content,workspace,parentCommentId,path,line,lineType,startLine,codeSnippet",
        "repository,pullRequestId,content")]
    [InlineData(
        "setPullRequestReviewStatus",
        "repository,pullRequestId,status,workspace,comment",
        "repository,pullRequestId,status")]
    [InlineData(
        "mergePullRequest",
        "repository,pullRequestId,workspace,mergeStrategy,message,closeSourceBranch",
        "repository,pullRequestId")]
    [InlineData(
        "declinePullRequest",
        "repository,pullRequestId,workspace,reason",
        "repository,pullRequestId")]
    public void InputSchemaExposesExactlyTheModelSuppliedArguments(string name, string properties, string required)
    {
        var schema = ToolTestHost.Find(name).ProtocolTool.InputSchema;

        Assert.Equal("object", schema.GetProperty("type").GetString());

        var actualProperties = schema.GetProperty("properties")
            .EnumerateObject()
            .Select(property => property.Name)
            .ToArray();

        Assert.Equal(properties.Split(','), actualProperties);

        var actualRequired = schema.TryGetProperty("required", out var requiredElement)
            ? requiredElement.EnumerateArray().Select(item => item.GetString()).ToArray()
            : [];

        Assert.Equal(required.Split(','), actualRequired);
    }

    [Fact]
    public void NoInputSchemaMentionsAnInjectedParameter()
    {
        foreach (var tool in ToolTestHost.Tools)
        {
            foreach (var property in tool.ProtocolTool.InputSchema.GetProperty("properties").EnumerateObject())
            {
                Assert.DoesNotContain(property.Name, NeverInSchema, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void EveryInputPropertyIsCamelCaseAndDescribed()
    {
        foreach (var tool in ToolTestHost.Tools)
        {
            var toolName = tool.ProtocolTool.Name;

            foreach (var property in tool.ProtocolTool.InputSchema.GetProperty("properties").EnumerateObject())
            {
                Assert.True(
                    IsCamelCase(property.Name),
                    $"{toolName}.{property.Name} is not camelCase.");

                var described = property.Value.TryGetProperty("description", out var description)
                    && !string.IsNullOrWhiteSpace(description.GetString());

                Assert.True(described, $"{toolName}.{property.Name} has no schema description.");
            }
        }
    }

    [Fact]
    public void EveryToolHasAnObjectOutputSchemaInCamelCase()
    {
        foreach (var tool in ToolTestHost.Tools)
        {
            var outputSchema = tool.ProtocolTool.OutputSchema;

            Assert.NotNull(outputSchema);
            Assert.Equal("object", outputSchema.Value.GetProperty("type").GetString());

            AssertCamelCaseProperties(outputSchema.Value, tool.ProtocolTool.Name);
        }
    }

    /// <summary>
    /// The one tool with two shapes: <c>mode</c> chooses, and both payloads have to be describable
    /// by the single output schema the tool advertises.
    /// </summary>
    [Fact]
    public void DiffOutputSchemaCarriesBothModes()
    {
        var outputSchema = ToolTestHost.Find("getPullRequestDiff").ProtocolTool.OutputSchema;

        Assert.NotNull(outputSchema);

        var properties = outputSchema.Value.GetProperty("properties");

        Assert.True(properties.TryGetProperty("mode", out _));
        Assert.True(properties.TryGetProperty("diffstat", out _));
        Assert.True(properties.TryGetProperty("diff", out _));
    }

    /// <summary>Every paginated result has to hand the caller a way to ask for the next page.</summary>
    [Theory]
    [InlineData("listPullRequests", "nextCursor")]
    [InlineData("getPullRequestComments", "nextCursor")]
    public void PaginatedOutputSchemasCarryTheCursor(string name, string cursorProperty)
    {
        var outputSchema = ToolTestHost.Find(name).ProtocolTool.OutputSchema;

        Assert.NotNull(outputSchema);
        Assert.True(outputSchema.Value.GetProperty("properties").TryGetProperty(cursorProperty, out _));
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
                Assert.True(
                    IsCamelCase(property.Name),
                    $"{toolName} output property '{property.Name}' is not camelCase.");

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
