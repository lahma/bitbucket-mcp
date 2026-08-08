namespace Bitbucket.Mcp.Http;

/// <summary>
/// One page of results, with Bitbucket's opaque <c>next</c> URL already turned into a cursor the
/// model can hand back verbatim.
/// </summary>
/// <param name="Items">The page's items; empty, never <see langword="null"/>.</param>
/// <param name="NextCursor">
/// The cursor for the following page, or <see langword="null"/> when this is the last one. Opaque
/// by design — see <see cref="BitbucketCursor"/> for why it is not just the URL.
/// </param>
/// <param name="TotalSize">
/// Total items across all pages when Bitbucket reported one. It frequently does not, and it is not
/// worth a second request, so treat its absence as normal.
/// </param>
/// <typeparam name="T">The item type.</typeparam>
internal sealed record Page<T>(IReadOnlyList<T> Items, string? NextCursor, int? TotalSize);
