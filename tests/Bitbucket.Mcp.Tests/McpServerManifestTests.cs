using System.Text.Json;
using System.Xml.Linq;


namespace Bitbucket.Mcp.Tests;

/// <summary>
/// <c>.mcp/server.json</c> is the MCP server manifest packed into the NuGet package at
/// <c>/.mcp/server.json</c>, where nuget.org reads it to render the package's MCP tab and to
/// generate client configuration, and which the build posts to the official MCP Registry on every
/// release (D25).
/// </summary>
/// <remarks>
/// <para>
/// It restates two things the build already knows — the version and the package id — and nothing
/// in the build reads it back, so a released package could advertise last release's version
/// indefinitely without anything failing. That is what this test is for. It follows the same
/// source-scanning shape as <see cref="Tools.NoStdoutWritesTest"/>: it reads the checked-in files
/// from <see cref="RepositoryRoot"/> rather than anything generated.
/// </para>
/// <para>
/// The manifest is read with <see cref="JsonDocument"/> rather than deserialized into a record,
/// because the test project runs with <c>JsonSerializerIsReflectionEnabledByDefault=false</c> (D7)
/// and this file has no business in a <c>JsonSerializerContext</c>.
/// </para>
/// </remarks>
public class McpServerManifestTests
{
    private const string ManifestPath = ".mcp/server.json";
    private const string ChangelogPath = "CHANGELOG.md";
    private const string ServerProjectPath = "src/Bitbucket.Mcp/Bitbucket.Mcp.csproj";

    [Test]
    public void ManifestVersionMatchesTheChangelog()
    {
        var root = RepositoryRoot.FullPath;
        var changelogVersion = ReadChangelogVersion(root);

        using var manifest = ReadManifest(root);
        var package = SingleNuGetPackage(manifest);

        Assert.That(manifest.RootElement.GetProperty("version").GetString(), Is.EqualTo(changelogVersion));
        Assert.That(package.GetProperty("version").GetString(), Is.EqualTo(changelogVersion));
    }

    [Test]
    public void ManifestIdentifierMatchesThePackagedId()
    {
        var root = RepositoryRoot.FullPath;

        var packageId = XDocument
            .Load(Path.Combine(root, ServerProjectPath))
            .Descendants("PackageId")
            .Select(x => x.Value)
            .SingleOrDefault();

        Assert.That(string.IsNullOrWhiteSpace(packageId), Is.False, $"{ServerProjectPath} declares no <PackageId>.");

        using var manifest = ReadManifest(root);
        Assert.That(SingleNuGetPackage(manifest).GetProperty("identifier").GetString(), Is.EqualTo(packageId));
    }

    /// <summary>
    /// nuget.org only looks at the first <c>packages</c> entry whose <c>registryType</c> is
    /// <c>nuget</c>, and the server speaks stdio only — a second entry or a different transport
    /// would silently change what clients are told to run.
    /// </summary>
    [Test]
    public void ManifestDeclaresExactlyOneStdioNuGetPackage()
    {
        var root = RepositoryRoot.FullPath;
        using var manifest = ReadManifest(root);

        var package = SingleNuGetPackage(manifest);

        Assert.That(package.GetProperty("transport").GetProperty("type").GetString(), Is.EqualTo("stdio"));
        Assert.That(manifest.RootElement.GetProperty("name").GetString(), Is.EqualTo("io.github.lahma/bitbucket-mcp"));
    }

    /// <summary>
    /// The shape the official MCP Registry accepts. The registry proves ownership against the README
    /// packed into the nupkg of the exact version being listed, so a version shipped without these
    /// can never be listed later - the release would push to nuget.org and then fail its registry step.
    /// </summary>
    [Test]
    public void ManifestIsOneTheMcpRegistryAccepts()
    {
        var root = RepositoryRoot.FullPath;
        using var manifest = ReadManifest(root);

        Assert.That(
            manifest.RootElement.GetProperty("$schema").GetString(),
            Is.EqualTo("https://static.modelcontextprotocol.io/schemas/2025-12-11/server.schema.json"));

        // The schema's maxLength; a longer description is rejected outright.
        Assert.That(manifest.RootElement.GetProperty("description").GetString(), Has.Length.LessThanOrEqualTo(100));

        // Exactly the NuGet service index, or the registry refuses the package entry.
        Assert.That(
            SingleNuGetPackage(manifest).GetProperty("registryBaseUrl").GetString(),
            Is.EqualTo("https://api.nuget.org/v3/index.json"));
    }

    /// <summary>The ownership marker the registry looks for in the packaged README.</summary>
    [Test]
    public void ReadmeCarriesTheRegistryOwnershipMarker()
    {
        var root = RepositoryRoot.FullPath;
        using var manifest = ReadManifest(root);

        var name = manifest.RootElement.GetProperty("name").GetString();

        Assert.That(File.ReadAllText(Path.Combine(root, "README.md")), Does.Contain($"mcp-name: {name}"));
    }

    private static JsonElement SingleNuGetPackage(JsonDocument manifest)
    {
        var packages = manifest.RootElement.GetProperty("packages")
            .EnumerateArray()
            .Where(x => x.GetProperty("registryType").GetString() == "nuget")
            .ToList();

        Assert.That(packages, Has.Exactly(1).Items);
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

        Assert.That(first, Does.StartWith("# "));
        return first[2..].Trim();
    }
}
