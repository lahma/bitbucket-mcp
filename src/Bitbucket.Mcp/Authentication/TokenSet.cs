using System.Text.Json.Serialization;

namespace Bitbucket.Mcp.Authentication;

/// <summary>
/// The persisted OAuth state: one access token, its expiry, and the refresh chain.
/// </summary>
/// <remarks>
/// <para>
/// Bitbucket refresh tokens are <em>single-use</em>: a successful refresh returns a new one and
/// invalidates the old. <see cref="PreviousRefreshToken"/> keeps the token that was just spent so
/// that a rotation lost to a crash or a racing process can be retried once instead of forcing the
/// user back through the browser.
/// </para>
/// <para>
/// Properties are <c>public</c> because System.Text.Json only binds public members; the type
/// itself stays internal. It is serialised by <c>BitbucketWireJsonContext</c> with the explicit
/// names below — no naming policy is in play.
/// </para>
/// </remarks>
internal sealed record TokenSet
{
    /// <summary>The layout version written by this build.</summary>
    internal const int CurrentVersion = 1;

    /// <summary>
    /// How long before the real expiry an access token is treated as already expired (D14). Covers
    /// clock skew and the flight time of the request the token is about to be used on.
    /// </summary>
    internal static readonly TimeSpan ExpirySkew = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Cache layout version. A file written by an older or newer build (or one missing the field,
    /// which reads back as <c>0</c>) must be treated as empty rather than guessed at.
    /// </summary>
    [JsonPropertyName("version")]
    public int Version { get; init; }

    /// <summary>
    /// SHA-256 of the OAuth consumer key that obtained these tokens. If it does not match the
    /// currently configured key the cache belongs to a different consumer and counts as empty —
    /// the key itself is not stored, so a leaked cache file does not leak the consumer identity.
    /// </summary>
    [JsonPropertyName("consumerKeyFingerprint")]
    public string? ConsumerKeyFingerprint { get; init; }

    /// <summary>The bearer token. <see langword="null"/> once invalidated.</summary>
    [JsonPropertyName("accessToken")]
    public string? AccessToken { get; init; }

    /// <summary>
    /// Absolute expiry, always computed from the <c>expires_in</c> the token endpoint returned and
    /// never hard-coded (D14 — Atlassian's own docs contradict themselves, 1 h versus 2 h).
    /// </summary>
    [JsonPropertyName("expiresAtUtc")]
    public DateTimeOffset ExpiresAtUtc { get; init; }

    /// <summary>The current, unspent refresh token.</summary>
    [JsonPropertyName("refreshToken")]
    public string? RefreshToken { get; init; }

    /// <summary>
    /// The refresh token that <see cref="RefreshToken"/> replaced. Retried exactly once on
    /// <c>invalid_grant</c>, to recover a rotation whose result never reached disk.
    /// </summary>
    [JsonPropertyName("previousRefreshToken")]
    public string? PreviousRefreshToken { get; init; }

    /// <summary>
    /// The space-delimited scopes the authorization server reported for this grant, as returned —
    /// shown by the <c>status</c> command so a missing <c>pullrequest:write</c> is diagnosable.
    /// </summary>
    [JsonPropertyName("scopes")]
    public string? Scopes { get; init; }

    /// <summary>
    /// Whether <see cref="AccessToken"/> is present and still valid <see cref="ExpirySkew"/> ahead
    /// of <paramref name="timeProvider"/>'s idea of now.
    /// </summary>
    internal bool IsAccessTokenValid(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        return !string.IsNullOrEmpty(AccessToken)
            && ExpiresAtUtc - ExpirySkew > timeProvider.GetUtcNow();
    }
}
