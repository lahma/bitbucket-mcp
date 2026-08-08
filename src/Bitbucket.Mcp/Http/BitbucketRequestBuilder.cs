using System.Globalization;
using System.Text;

namespace Bitbucket.Mcp.Http;

/// <summary>
/// Composes the relative URL of a Bitbucket request: path segments and query parameters, each one
/// escaped exactly once.
/// </summary>
/// <remarks>
/// <para>
/// The URL is relative on purpose — it is resolved against the client's
/// <see cref="HttpClient.BaseAddress"/> of <c>https://api.bitbucket.org/2.0/</c>, so no code path
/// here can be talked into naming a different host. (The one place an absolute URL is used is a
/// pagination cursor, which <see cref="BitbucketCursor"/> validates instead.)
/// </para>
/// <para>
/// Every path segment goes through <see cref="Uri.EscapeDataString(string)"/>, including the ones
/// that look like constants. Workspace and repository slugs come from the model and may contain
/// anything; escaping them is what stops a slug of <c>../../</c> from walking out of the
/// repository's namespace. Escaping a genuine constant such as <c>request-changes</c> is a no-op,
/// so there is no reason to have a second, unescaped path method that could be reached by accident.
/// </para>
/// <para>
/// Path and query are accumulated separately, so the call order of
/// <see cref="Segment(string)"/> and the <c>Query</c> methods does not matter.
/// </para>
/// </remarks>
internal sealed class BitbucketRequestBuilder
{
    private readonly StringBuilder _path;
    private StringBuilder? _query;

    private BitbucketRequestBuilder(string rootSegment)
    {
        _path = new StringBuilder(Uri.EscapeDataString(rootSegment));
    }

    /// <summary>
    /// Starts a URL at <c>repositories/{workspace}/{repositorySlug}</c>, the prefix of every
    /// pull-request endpoint.
    /// </summary>
    /// <param name="workspace">The workspace <em>URL segment</em>, not its display name.</param>
    /// <param name="repositorySlug">The repository slug.</param>
    internal static BitbucketRequestBuilder Repository(string workspace, string repositorySlug)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositorySlug);

        return new BitbucketRequestBuilder("repositories")
            .Segment(workspace)
            .Segment(repositorySlug);
    }

    /// <summary>Appends one escaped path segment.</summary>
    internal BitbucketRequestBuilder Segment(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        _path.Append('/').Append(Uri.EscapeDataString(value));
        return this;
    }

    /// <summary>Appends a numeric path segment, such as a pull request id.</summary>
    internal BitbucketRequestBuilder Segment(int value)
    {
        _path.Append('/').Append(value.ToString(CultureInfo.InvariantCulture));
        return this;
    }

    /// <summary>
    /// Appends <c>name=value</c>, or nothing at all when the value is absent — "unset" and "set to
    /// the empty string" are different requests to Bitbucket, and callers always mean the former.
    /// </summary>
    internal BitbucketRequestBuilder Query(string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return this;
        }

        AppendQuery(name, value);
        return this;
    }

    /// <summary>Appends a numeric query parameter, or nothing when it is <see langword="null"/>.</summary>
    internal BitbucketRequestBuilder Query(string name, int? value) =>
        value is null ? this : Query(name, value.GetValueOrDefault().ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// Appends a boolean query parameter as <c>true</c>/<c>false</c>, or nothing when it is
    /// <see langword="null"/>.
    /// </summary>
    internal BitbucketRequestBuilder Query(string name, bool? value) =>
        value is null ? this : Query(name, value.GetValueOrDefault() ? "true" : "false");

    /// <summary>
    /// Repeats <c>name=value</c> once per element. This is how the diff endpoint takes a file
    /// selection — <c>?path=a&amp;path=b</c>, not a comma-separated list — and it is the mechanism
    /// the whole diffstat-first workflow rests on.
    /// </summary>
    internal BitbucketRequestBuilder QueryEach(string name, IReadOnlyList<string>? values)
    {
        if (values is null)
        {
            return this;
        }

        foreach (var value in values)
        {
            Query(name, value);
        }

        return this;
    }

    /// <summary>The composed relative URL.</summary>
    internal string Build() => _query is null
        ? _path.ToString()
        : string.Concat(_path.ToString(), "?", _query.ToString());

    /// <inheritdoc />
    public override string ToString() => Build();

    private void AppendQuery(string name, string value)
    {
        if (_query is null)
        {
            _query = new StringBuilder();
        }
        else
        {
            _query.Append('&');
        }

        _query.Append(Uri.EscapeDataString(name)).Append('=').Append(Uri.EscapeDataString(value));
    }
}
