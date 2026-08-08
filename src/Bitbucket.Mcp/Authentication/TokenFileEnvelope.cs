using System.Text.Json.Serialization;

namespace Bitbucket.Mcp.Authentication;

/// <summary>
/// The outer object actually written to the token cache file. The <see cref="TokenSet"/> is
/// serialised, optionally encrypted, and carried as base64 in <see cref="Payload"/> so that the
/// at-rest protection scheme is self-describing: a file written on Windows under DPAPI is
/// recognisably not readable elsewhere, instead of failing as corrupt JSON.
/// </summary>
/// <remarks>
/// Properties are <c>public</c> because System.Text.Json only binds public members. A file that
/// fails to deserialise (truncated, hand-edited, written by a future version) is treated as an
/// empty cache by the token store, never as a fatal error.
/// </remarks>
internal sealed record TokenFileEnvelope
{
    /// <summary>DPAPI-protected payload, current user scope. Windows only.</summary>
    internal const string SchemeDpapi = "dpapi";

    /// <summary>Unencrypted payload, protected only by file permissions (0600 on Unix).</summary>
    internal const string SchemePlain = "plain";

    /// <summary>Either <see cref="SchemeDpapi"/> or <see cref="SchemePlain"/>.</summary>
    [JsonPropertyName("scheme")]
    public required string Scheme { get; init; }

    /// <summary>Base64 of the (possibly encrypted) UTF-8 <see cref="TokenSet"/> JSON.</summary>
    [JsonPropertyName("payload")]
    public required string Payload { get; init; }
}
