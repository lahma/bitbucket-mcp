using System.Text.Json.Serialization;

namespace Bitbucket.Mcp.Http.Models;

/// <summary>
/// The one link off a Bitbucket object that is worth carrying: <c>html</c>, the page a human opens.
/// </summary>
/// <remarks>
/// Bitbucket attaches a dozen <c>links</c> entries to every object — <c>self</c>, <c>diff</c>,
/// <c>commits</c>, <c>approve</c> and the rest — all of which are URLs this server composes itself
/// and no caller can use. Only <c>html.href</c> is requested (see <c>FieldSets</c>), because the
/// one thing a model cannot synthesise is the web address to hand back to the user.
/// </remarks>
internal sealed record LinksDto
{
    /// <summary>The Bitbucket web UI page for the object.</summary>
    [JsonPropertyName("html")]
    public LinkDto? Html { get; init; }
}

/// <summary>A single Bitbucket link.</summary>
internal sealed record LinkDto
{
    /// <summary>The absolute URL.</summary>
    [JsonPropertyName("href")]
    public string? Href { get; init; }
}
