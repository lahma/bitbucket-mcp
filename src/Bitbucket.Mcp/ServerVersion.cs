using System.Reflection;

namespace Bitbucket.Mcp;

/// <summary>
/// The version this binary reports, both to <c>--version</c> and to MCP clients in
/// <c>serverInfo</c>. Read once from the assembly's informational version, which the build
/// derives from <c>CHANGELOG.md</c>.
/// </summary>
internal static class ServerVersion
{
    /// <summary>The product name, used as the MCP server name and as the binary name.</summary>
    internal const string Name = "bitbucket-mcp";

    /// <summary>
    /// The informational version with any source-revision suffix (<c>+sha</c>) removed.
    /// </summary>
    internal static string Value { get; } = Resolve();

    private static string Resolve()
    {
        var informational = typeof(ServerVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (string.IsNullOrEmpty(informational))
        {
            return "0.0.0";
        }

        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? informational : informational[..plus];
    }
}
