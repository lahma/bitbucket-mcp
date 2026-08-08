using System.Net.Http.Headers;
using System.Text;

namespace Bitbucket.Mcp.Authentication;

/// <summary>
/// A credential that came from the environment and never changes: a bearer access token, or Basic
/// <c>email:api-token</c>. The header is computed once, at construction, and handed out unchanged.
/// </summary>
/// <remarks>
/// <para>
/// There is nothing to refresh and nothing to invalidate — if the token is rejected, it is wrong,
/// not stale, and the fix is in the environment rather than in this process. So
/// <see cref="InvalidateAsync"/> does nothing, and <c>AuthenticationHandler</c>'s one 401 retry
/// simply re-sends the same header before reporting the 401 (which costs one request and keeps the
/// handler free of provider-specific special cases).
/// </para>
/// <para>
/// Bitbucket app passwords were removed on 2026-07-28 and are deliberately not implemented. The
/// Basic form here is an Atlassian <em>API token</em>, which is a different credential with the
/// same wire shape.
/// </para>
/// </remarks>
internal sealed class StaticCredentialProvider : ICredentialProvider
{
    private readonly AuthenticationHeaderValue _header;
    private readonly string _description;

    /// <param name="header">The header to attach to every request.</param>
    /// <param name="description">
    /// What <see cref="Describe"/> returns. Must name where the credential came from and must not
    /// contain any part of it.
    /// </param>
    internal StaticCredentialProvider(AuthenticationHeaderValue header, string description)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        _header = header;
        _description = description;
    }

    /// <summary>A workspace, repository or project access token, sent as <c>Bearer</c>.</summary>
    internal static StaticCredentialProvider ForBearerToken(string accessToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);

        return new StaticCredentialProvider(
            new AuthenticationHeaderValue("Bearer", accessToken),
            "Bearer access token from BITBUCKET_ACCESS_TOKEN");
    }

    /// <summary>
    /// An Atlassian API token paired with the account email, sent as
    /// <c>Basic base64(email:token)</c>.
    /// </summary>
    internal static StaticCredentialProvider ForApiToken(string email, string apiToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiToken);

        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{apiToken}"));

        return new StaticCredentialProvider(
            new AuthenticationHeaderValue("Basic", credentials),
            $"Basic API token for {email} (BITBUCKET_EMAIL + BITBUCKET_API_TOKEN)");
    }

    /// <inheritdoc />
    public ValueTask<AuthenticationHeaderValue> GetAuthenticationHeaderAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(_header);
    }

    /// <inheritdoc />
    public ValueTask InvalidateAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <inheritdoc />
    public string Describe() => _description;
}
