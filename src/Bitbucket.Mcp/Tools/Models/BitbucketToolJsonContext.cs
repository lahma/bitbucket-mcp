using System.Text.Json.Serialization;

namespace Bitbucket.Mcp.Tools.Models;

/// <summary>
/// The source-generated serializer contract for everything the tool layer hands back to an MCP
/// client — result records and the primitive parameter types the SDK builds tool schemas from.
/// </summary>
/// <remarks>
/// <para>
/// This is the context that goes <b>first</b> in the MCP server's <c>TypeInfoResolverChain</c>
/// (D6), with the SDK's own resolver second. First-match-wins, so ours answers for our types and
/// falls through for MCP protocol types — which is what makes JIT and AOT resolve identically
/// instead of one of them silently reaching for reflection.
/// </para>
/// <para>
/// Deliberately separate from <c>BitbucketWireJsonContext</c>, which is snake_case and never
/// chained in here: Bitbucket's shapes are wire contracts we do not control, these are ours.
/// </para>
/// <para>
/// The primitive registrations at the bottom are not decorative. With
/// <c>JsonSerializerIsReflectionEnabledByDefault=false</c> (D7) the schema exporter can only
/// describe a parameter whose type some resolver in the chain knows, so every type used as a tool
/// parameter has to be resolvable — including the nullable value types, which no other context in
/// the chain declares.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]

// Pull request results.
[JsonSerializable(typeof(UserSummary))]
[JsonSerializable(typeof(ParticipantSummary))]
[JsonSerializable(typeof(PullRequestSummary))]
[JsonSerializable(typeof(PullRequestListResult))]
[JsonSerializable(typeof(PullRequestDetail))]

// Diff results.
[JsonSerializable(typeof(DiffStatFile))]
[JsonSerializable(typeof(DiffStatResult))]
[JsonSerializable(typeof(DiffFileDiff))]
[JsonSerializable(typeof(DiffResult))]
[JsonSerializable(typeof(PullRequestDiffResult))]

// Comment results.
[JsonSerializable(typeof(CommentSummary))]
[JsonSerializable(typeof(CommentListResult))]
[JsonSerializable(typeof(CommentResult))]
[JsonSerializable(typeof(CommentResolutionResult))]

// Default reviewer results.
[JsonSerializable(typeof(DefaultReviewerSummary))]
[JsonSerializable(typeof(DefaultReviewerListResult))]

// Build status results.
[JsonSerializable(typeof(PullRequestStatusSummary))]
[JsonSerializable(typeof(PullRequestStatusListResult))]

// Task results.
[JsonSerializable(typeof(PullRequestTask))]
[JsonSerializable(typeof(PullRequestTaskListResult))]

// Review, merge and decline results.
[JsonSerializable(typeof(ReviewStatusResult))]
[JsonSerializable(typeof(MergeResult))]
[JsonSerializable(typeof(DeclineResult))]

// Tool parameter types, for schema generation.
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(int?))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(long?))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(bool?))]
internal sealed partial class BitbucketToolJsonContext : JsonSerializerContext;
