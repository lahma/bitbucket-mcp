using System.Text.Json;
using System.Xml.Linq;

using Xunit;

namespace Bitbucket.Mcp.Tests;

/// <summary>
/// <c>.mcp/server.json</c> is the MCP server manifest packed into the NuGet package at
/// <c>/.mcp/server.json</c>, where nuget.org reads it to render the package's MCP tab and to
/// generate client configuration.
/// </summary>
/// <remarks>
/// <para>
/// It restates two things the build already knows — the version and the package id — and nothing
/// in the build reads it back, so a released package could advertise last release's version
/// indefinitely without anything failing. That is what this test is for. It follows the same
/// source-scanning shape as <see cref="Tools.NoStdoutWritesTest"/>: walk up from the test assembly
/// to the repository root, then read the checked-in files rather than anything generated.
/// </para>
/// <para>
/// The manifest is read with <see cref="JsonDocument"/> rather than deserialized into a record,
/// because the test project runs with <c>JsonSerializerIsReflectionEnabledByDefault=false</c> (D7)
/// and this file has no business in a <c>JsonSerializerContext</c>.
/// </para>
/// </remarks>
public class McpServerManifestTests
{
    /// <summary>The file that identifies the repository root when walking up from the test assembly.</summary>
    private const string RootMarker = "bitbucket-mcp.slnx";

    private const string ManifestPath = ".mcp/server.json";
    private const string ChangelogPath = "CHANGELOG.md";
    private const string ServerProjectPath = "src/Bitbucket.Mcp/Bitbucket.Mcp.csproj";

    [Fact]
    public void ManifestVersionMatchesTheChangelog()
    {
        var root = FindRepositoryRoot();
        var changelogVersion = ReadChangelogVersion(root);

        using var manifest = ReadManifest(root);
        var package = SingleNuGetPackage(manifest);

        Assert.Equal(changelogVersion, manifest.RootElement.GetProperty("version").GetString());
        Assert.Equal(changelogVersion, package.GetProperty("version").GetString());
    }

    [Fact]
    public void ManifestIdentifierMatchesThePackagedId()
    {
        var root = FindRepositoryRoot();

        var packageId = XDocument
            .Load(Path.Combine(root, ServerProjectPath))
            .Descendants("PackageId")
            .Select(x => x.Value)
            .SingleOrDefault();

        Assert.False(string.IsNullOrWhiteSpace(packageId), $"{ServerProjectPath} declares no <PackageId>.");

        using var manifest = ReadManifest(root);
        Assert.Equal(packageId, SingleNuGetPackage(manifest).GetProperty("identifier").GetString());
    }

    /// <summary>
    /// nuget.org only looks at the first <c>packages</c> entry whose <c>registryType</c> is
    /// <c>nuget</c>, and the server speaks stdio only — a second entry or a different transport
    /// would silently change what clients are told to run.
    /// </summary>
    [Fact]
    public void ManifestDeclaresExactlyOneStdioNuGetPackage()
    {
        var root = FindRepositoryRoot();
        using var manifest = ReadManifest(root);

        var package = SingleNuGetPackage(manifest);

        Assert.Equal("stdio", package.GetProperty("transport").GetProperty("type").GetString());
        Assert.Equal("io.github.lahma/bitbucket-mcp", manifest.RootElement.GetProperty("name").GetString());
    }

    private static JsonElement SingleNuGetPackage(JsonDocument manifest)
    {
        var packages = manifest.RootElement.GetProperty("packages")
            .EnumerateArray()
            .Where(x => x.GetProperty("registryType").GetString() == "nuget")
            .ToList();

        Assert.Single(packages);
        return packages[0];
    }

    private static JsonDocument ReadManifest(string root)
        => JsonDocument.Parse(File.ReadAllText(Path.Combine(root, ManifestPath)));

    /// <summary>
    /// The version authority: CHANGELOG.md's first line is a <c># version</c> header, which is what
    /// the Fallout build parses in <c>OnBuildInitialized</c>.
    /// </summary>
    private static string ReadChangelogVersion(string root)
    {
        var first = File.ReadLines(Path.Combine(root, ChangelogPath)).First().Trim();

        Assert.StartsWith("# ", first, StringComparison.Ordinal);
        return first[2..].Trim();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, RootMarker)))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException($"Could not find {RootMarker} above {AppContext.BaseDirectory}.");
    }
}
