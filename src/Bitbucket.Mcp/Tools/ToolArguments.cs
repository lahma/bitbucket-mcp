using System.Text;
using System.Text.Json;

using ModelContextProtocol.Protocol;

namespace Bitbucket.Mcp.Tools;

/// <summary>
/// Refuses a tool call that names an argument its tool's input schema does not.
/// </summary>
/// <remarks>
/// <para>
/// The SDK binds arguments to parameters by exact name and ignores any it cannot place. A
/// mis-cased required argument (<c>Repository</c>) then fails with a bare "An error occurred
/// invoking", which says nothing about the argument; a mis-cased optional one is dropped without a
/// word, so <c>Workspace</c> instead of <c>workspace</c> quietly runs the call against
/// <c>BITBUCKET_DEFAULT_WORKSPACE</c> — for a write tool, a change made in the wrong place.
/// </para>
/// <para>
/// Checked against the schema the client was shown, before the tool runs, both become an error that
/// names the argument and, when only the case is wrong, the spelling that was meant. The check is a
/// call filter in <see cref="McpServerSetup"/>, so it covers every tool without any of them knowing.
/// </para>
/// </remarks>
internal static class ToolArguments
{
    /// <summary>
    /// The error for a call that names arguments <paramref name="tool"/> does not take, or
    /// <see langword="null"/> when every name is one of its schema's properties.
    /// </summary>
    /// <param name="tool">The tool as clients see it, input schema included.</param>
    /// <param name="argumentNames">The names in the call's <c>arguments</c>, or <see langword="null"/> when it sent none.</param>
    internal static string? Refuse(Tool tool, IEnumerable<string>? argumentNames)
    {
        ArgumentNullException.ThrowIfNull(tool);

        if (argumentNames is null)
        {
            return null;
        }

        var accepted = AcceptedNames(tool.InputSchema);
        List<string>? unknown = null;

        foreach (var name in argumentNames)
        {
            if (!accepted.Contains(name, StringComparer.Ordinal))
            {
                (unknown ??= []).Add(name);
            }
        }

        if (unknown is null)
        {
            return null;
        }

        var message = new StringBuilder(tool.Name).Append(" has no argument named ");

        for (var i = 0; i < unknown.Count; i++)
        {
            if (i > 0)
            {
                message.Append(i == unknown.Count - 1 ? " or " : ", ");
            }

            message.Append('\'').Append(unknown[i]).Append('\'');

            var meant = accepted.Find(name => string.Equals(name, unknown[i], StringComparison.OrdinalIgnoreCase));

            if (meant is not null)
            {
                message.Append(" (did you mean '").Append(meant).Append("'?)");
            }
        }

        message.Append(". Argument names are case-sensitive; ").Append(tool.Name).Append(
            accepted.Count == 0 ? " takes none" : " takes " + string.Join(", ", accepted));

        return message.Append(". Nothing was sent to Bitbucket.").ToString();
    }

    /// <summary>The schema's property names, in the order the schema lists them.</summary>
    private static List<string> AcceptedNames(JsonElement schema) =>
        schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object
            ? [.. properties.EnumerateObject().Select(static property => property.Name)]
            : [];
}
