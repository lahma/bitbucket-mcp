using System.Text.Json.Serialization;

using Bitbucket.Mcp.Authentication;

namespace Bitbucket.Mcp.Http.Models;

/// <summary>
/// The source-generated serializer contract for everything that crosses the wire to Bitbucket, to
/// the OAuth token endpoint, or to the token cache file.
/// </summary>
/// <remarks>
/// <para>
/// <b>This context is used by the client and auth layers only, and is never chained into the MCP
/// server's <c>JsonSerializerOptions</c>.</b> Bitbucket's shapes are snake_case wire contracts we
/// do not control; the tool-facing shapes are camelCase and ours. Mixing the two resolvers would
/// let a wire type leak into a tool schema and vice versa.
/// </para>
/// <para>
/// No naming policy is configured, deliberately. Every property carries an explicit
/// <see cref="JsonPropertyNameAttribute"/>, so a rename or a refactor cannot silently change what
/// goes on the wire, and reading the DTO tells you the exact JSON.
/// </para>
/// <para>
/// <c>JsonSerializerIsReflectionEnabledByDefault=false</c> is set in the csproj (D7), so a type
/// missing from this list fails at the first <c>dotnet test</c> rather than only after an AOT
/// publish.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]

// Paginated responses. Closed generics have to be registered explicitly — the generator cannot
// discover which instantiations exist.
[JsonSerializable(typeof(PageEnvelope<PullRequestSummaryDto>), TypeInfoPropertyName = "PullRequestSummaryPage")]
[JsonSerializable(typeof(PageEnvelope<CommentDto>), TypeInfoPropertyName = "CommentPage")]
[JsonSerializable(typeof(PageEnvelope<DiffStatEntryDto>), TypeInfoPropertyName = "DiffStatEntryPage")]

// Single-object responses.
[JsonSerializable(typeof(PullRequestDto))]
[JsonSerializable(typeof(PullRequestSummaryDto))]
[JsonSerializable(typeof(AccountDto))]
[JsonSerializable(typeof(ParticipantDto))]
[JsonSerializable(typeof(CommentDto))]
[JsonSerializable(typeof(DiffStatEntryDto))]
[JsonSerializable(typeof(InlineDto))]
[JsonSerializable(typeof(MergeTaskStatusDto))]
[JsonSerializable(typeof(ErrorEnvelopeDto))]

// Request bodies.
[JsonSerializable(typeof(CreatePullRequestRequest))]
[JsonSerializable(typeof(UpdatePullRequestRequest))]
[JsonSerializable(typeof(CommentRequest))]
[JsonSerializable(typeof(MergeRequest))]
[JsonSerializable(typeof(DeclineRequest))]

// OAuth token endpoint.
[JsonSerializable(typeof(OAuthTokenResponse))]
[JsonSerializable(typeof(OAuthErrorResponse))]

// Token cache file.
[JsonSerializable(typeof(TokenFileEnvelope))]
[JsonSerializable(typeof(TokenSet))]
internal sealed partial class BitbucketWireJsonContext : JsonSerializerContext;
