using System.Text.Json.Serialization;

namespace Bitbucket.Mcp.Http.Models;

/// <summary>
/// A successful response from <c>https://bitbucket.org/site/oauth2/access_token</c>, for both the
/// authorization-code exchange and a refresh.
/// </summary>
internal sealed record OAuthTokenResponse
{
    /// <summary>The bearer token.</summary>
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; init; }

    /// <summary>
    /// Lifetime in seconds. This is the <em>only</em> source of the expiry (D14): Atlassian's docs
    /// disagree with themselves about whether it is one hour or two, so nothing may be hard-coded.
    /// </summary>
    [JsonPropertyName("expires_in")]
    public int? ExpiresIn { get; init; }

    /// <summary>
    /// The single-use refresh token. Absent on some responses, in which case the previous one
    /// stays current.
    /// </summary>
    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; init; }

    /// <summary>Always <c>bearer</c> in practice.</summary>
    [JsonPropertyName("token_type")]
    public string? TokenType { get; init; }

    /// <summary>The granted scopes, in Bitbucket's non-standard <c>scopes</c> field.</summary>
    [JsonPropertyName("scopes")]
    public string? Scopes { get; init; }

    /// <summary>The granted scopes, in the RFC 6749 <c>scope</c> field.</summary>
    [JsonPropertyName("scope")]
    public string? Scope { get; init; }

    /// <summary>
    /// The granted scopes from whichever field the response happened to use. Bitbucket has
    /// returned both spellings at different times and on different endpoints.
    /// </summary>
    [JsonIgnore]
    public string? GrantedScopes => Scopes ?? Scope;
}

/// <summary>
/// An error response from the token endpoint. <c>invalid_grant</c> is the one that matters: it is
/// how a spent or revoked refresh token reports itself, and it drives the one retry with the
/// previous refresh token before the user is sent back to the browser.
/// </summary>
internal sealed record OAuthErrorResponse
{
    /// <summary>The RFC 6749 error code, for example <c>invalid_grant</c>.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; init; }

    /// <summary>Human-readable elaboration, when the server sends one.</summary>
    [JsonPropertyName("error_description")]
    public string? ErrorDescription { get; init; }
}
