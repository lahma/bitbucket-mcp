using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Bitbucket.Mcp.Tests;

/// <summary>
/// The NuGet channel's runtime list is written down twice and has to agree (D17): the project's
/// <c>ToolPackageRuntimeIdentifiers</c>, which the pointer package names to every client, and
/// <c>release.yml</c>'s matrix, whose legs are the only machines that can build those packages.
/// </summary>
/// <remarks>
/// A runtime in the project but not in the matrix is a pointer naming a package that is never built,
/// and the publish job refuses the incomplete set; one in the matrix but not in the project is a
/// native binary the CLI never offers. Either way the gap would first show at release time, which is
/// why it is asserted on every build instead.
/// </remarks>
public class ToolPackageTests
{
    private const string ServerProjectPath = "src/Bitbucket.Mcp/Bitbucket.Mcp.csproj";
    private const string ReleaseWorkflowPath = ".github/workflows/release.yml";

    [Test]
    public void TheToolPackagesNativeRuntimesAreExactlyTheReleaseLegs()
    {
        var declared = ToolPackageRuntimes().Where(rid => rid != "any").Order(StringComparer.Ordinal);
        var legs = ReleaseLegRuntimes().Order(StringComparer.Ordinal);

        Assert.That(declared, Is.EqualTo(legs));
    }

    /// <summary>Every other platform falls back to the framework-dependent package; without it, they get nothing.</summary>
    [Test]
    public void TheToolPackageCarriesAFrameworkDependentFallback() =>
        Assert.That(ToolPackageRuntimes(), Does.Contain("any"));

    /// <summary>The artifact name is the contract with the publish job, which collects exactly this prefix.</summary>
    [Test]
    public void EveryReleaseLegPacksAndUploadsItsToolPackageUnderTheCollectedName()
    {
        var workflow = File.ReadAllText(Path.Combine(RepositoryRoot.FullPath, ReleaseWorkflowPath));

        Assert.That(workflow, Does.Contain("PackRuntime --runtime ${{ matrix.rid }}"));
        Assert.That(workflow, Does.Contain("name: tool-package-${{ matrix.rid }}"));
    }

    private static string[] ToolPackageRuntimes() =>
        XDocument.Load(Path.Combine(RepositoryRoot.FullPath, ServerProjectPath))
            .Descendants("ToolPackageRuntimeIdentifiers")
            .Single()
            .Value
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static IEnumerable<string> ReleaseLegRuntimes() =>
        Regex.Matches(
                File.ReadAllText(Path.Combine(RepositoryRoot.FullPath, ReleaseWorkflowPath)),
                @"^\s+rid:\s*(\S+)\s*$",
                RegexOptions.Multiline)
            .Select(match => match.Groups[1].Value);
}
