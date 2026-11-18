namespace Bitbucket.Mcp.Tests;

/// <summary>
/// The checked-in repository, for the tests that hold source files, manifests and workflows to a rule.
/// </summary>
/// <remarks>
/// Found by walking up from the test assembly to the directory that holds <c>bitbucket-mcp.slnx</c>,
/// so those tests read the files as committed rather than anything copied to the build output.
/// </remarks>
internal static class RepositoryRoot
{
    private const string Marker = "bitbucket-mcp.slnx";

    private static readonly Lazy<string> Root = new(Find);

    /// <summary>The repository root's full path.</summary>
    internal static string FullPath => Root.Value;

    private static string Find()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, Marker)))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException($"Could not find {Marker} above {AppContext.BaseDirectory}.");
    }
}
