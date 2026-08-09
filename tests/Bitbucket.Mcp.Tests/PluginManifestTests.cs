using System.Text.Json;

using Xunit;

namespace Bitbucket.Mcp.Tests;

/// <summary>
/// The two Claude Code plugin manifests that make this repository installable with
/// <c>/plugin marketplace add lahma/bitbucket-mcp</c>: <c>.claude-plugin/marketplace.json</c> (the
/// catalog) and <c>.claude-plugin/plugin.json</c> (the plugin itself, whose source is the
/// repository root).
/// </summary>
/// <remarks>
/// <para>
/// One install delivers two things — the shipped skill and the MCP server wired through
/// <c>dnx</c> — and both are described by paths and version strings that nothing else in the build
/// reads back. A skill path that stops resolving ships a plugin with no skill; a <c>dnx</c> pin left
/// behind at release time ships this version's skill driving last version's server. Neither fails
/// anywhere else, which is what this file is for. It is the same argument, and the same shape, as
/// <see cref="McpServerManifestTests"/>.
/// </para>
/// <para>
/// The credential surface is checked rather than restated: the environment block the plugin hands
/// the server has to be exactly the one <c>.mcp/server.json</c> documents, each entry wired to a
/// <c>userConfig</c> option that exists, with secrecy agreeing between the two. A typo in a
/// <c>${user_config.…}</c> placeholder does not fail anywhere — it passes the placeholder itself
/// through as the credential.
/// </para>
/// <para>
/// Read with <see cref="JsonDocument"/> rather than deserialized, for the reason
/// <see cref="McpServerManifestTests"/> gives: the test project runs with
/// <c>JsonSerializerIsReflectionEnabledByDefault=false</c> (D7) and these files have no business in
/// a <c>JsonSerializerContext</c>.
/// </para>
/// </remarks>
public class PluginManifestTests
{
    /// <summary>The file that identifies the repository root when walking up from the test assembly.</summary>
    private const string RootMarker = "bitbucket-mcp.slnx";

    private const string MarketplacePath = ".claude-plugin/marketplace.json";
    private const string PluginPath = ".claude-plugin/plugin.json";
    private const string ServerManifestPath = ".mcp/server.json";
    private const string ChangelogPath = "CHANGELOG.md";

    /// <summary>The NuGet package `dnx` is told to run — the same id `.mcp/server.json` identifies.</summary>
    private const string PackageId = "bitbucket-mcp";

    /// <summary>
    /// The plugin's source is the repository root, which is what lets the manifest point at the one
    /// canonical <c>SKILL.md</c> under <c>.claude/skills/</c> instead of a second copy. A plugin
    /// cannot reference files outside its own root, so any other source would force a duplicate.
    /// </summary>
    [Fact]
    public void TheMarketplaceListsThisRepositoryAsItsOnePlugin()
    {
        var root = FindRepositoryRoot();

        using var marketplace = Read(root, MarketplacePath);
        using var plugin = Read(root, PluginPath);

        Assert.Equal("bitbucket-mcp", marketplace.RootElement.GetProperty("name").GetString());

        Assert.False(
            string.IsNullOrWhiteSpace(marketplace.RootElement.GetProperty("owner").GetProperty("name").GetString()),
            "A marketplace needs an owner name; users see it before they trust the source.");

        var entries = marketplace.RootElement.GetProperty("plugins").EnumerateArray().ToList();
        var entry = Assert.Single(entries);

        Assert.Equal(plugin.RootElement.GetProperty("name").GetString(), entry.GetProperty("name").GetString());
        Assert.Equal("./", entry.GetProperty("source").GetString());
    }

    /// <summary>
    /// <c>CHANGELOG.md</c> is the version authority the whole build reads from, and an explicit
    /// plugin <c>version</c> is what Claude Code keys updates off: leave it behind and users are
    /// told they are already up to date.
    /// </summary>
    [Fact]
    public void ThePluginVersionIsTheVersionAuthority()
    {
        var root = FindRepositoryRoot();

        using var plugin = Read(root, PluginPath);

        Assert.Equal(ReadChangelogVersion(root), plugin.RootElement.GetProperty("version").GetString());
    }

    /// <summary>
    /// The bundled server is pinned, not floating. A floating <c>dnx bitbucket-mcp</c> would change
    /// what the plugin runs without the plugin version changing — invisible to <c>/plugin update</c>,
    /// and able to pair this release's skill with a server that no longer matches it.
    /// </summary>
    [Fact]
    public void TheBundledServerIsPinnedToThatSameVersion()
    {
        var root = FindRepositoryRoot();
        var version = ReadChangelogVersion(root);

        using var plugin = Read(root, PluginPath);

        var arguments = SingleServer(plugin)
            .GetProperty("args")
            .EnumerateArray()
            .Select(argument => argument.GetString())
            .ToList();

        Assert.Contains($"{PackageId}@{version}", arguments);
    }

    /// <summary>
    /// The manifest names the skill directory explicitly, because the plugin's source is the
    /// repository root and the default <c>skills/</c> scan would otherwise find nothing.
    /// </summary>
    [Fact]
    public void EveryDeclaredSkillPathResolvesToASkill()
    {
        var root = FindRepositoryRoot();

        using var plugin = Read(root, PluginPath);

        var paths = plugin.RootElement.GetProperty("skills")
            .EnumerateArray()
            .Select(path => path.GetString()!)
            .ToList();

        Assert.NotEmpty(paths);

        foreach (var path in paths)
        {
            Assert.StartsWith("./", path, StringComparison.Ordinal);

            var skill = Path.Combine(root, path[2..].Replace('/', Path.DirectorySeparatorChar), "SKILL.md");

            Assert.True(
                File.Exists(skill),
                $"{PluginPath} declares the skill path '{path}', which has no SKILL.md. The plugin would "
                + "install with no skill at all.");
        }
    }

    /// <summary>
    /// The environment block and <c>.mcp/server.json</c> describe the same server's credentials, and
    /// each value has to reach a <c>userConfig</c> option that exists — an unresolved
    /// <c>${user_config.…}</c> is handed to the server as a literal credential.
    /// </summary>
    [Fact]
    public void EveryDocumentedCredentialIsPromptedForAndPassedThrough()
    {
        var root = FindRepositoryRoot();

        using var plugin = Read(root, PluginPath);
        using var server = Read(root, ServerManifestPath);

        var documented = server.RootElement.GetProperty("packages")
            .EnumerateArray()
            .Single(package => package.GetProperty("registryType").GetString() == "nuget")
            .GetProperty("environmentVariables")
            .EnumerateArray()
            .ToDictionary(
                variable => variable.GetProperty("name").GetString()!,
                variable => variable.TryGetProperty("isSecret", out var secret) && secret.GetBoolean(),
                StringComparer.Ordinal);

        var options = plugin.RootElement.GetProperty("userConfig");

        var passed = SingleServer(plugin).GetProperty("env")
            .EnumerateObject()
            .ToDictionary(entry => entry.Name, entry => entry.Value.GetString()!, StringComparer.Ordinal);

        Assert.Equal(
            documented.Keys.OrderBy(name => name, StringComparer.Ordinal),
            passed.Keys.OrderBy(name => name, StringComparer.Ordinal));

        foreach (var (name, isSecret) in documented)
        {
            var placeholder = passed[name];

            Assert.True(
                placeholder.StartsWith("${user_config.", StringComparison.Ordinal)
                && placeholder.EndsWith('}'),
                $"{name} is passed as '{placeholder}', which is a literal, not a configured value.");

            var key = placeholder["${user_config.".Length..^1];

            Assert.True(
                options.TryGetProperty(key, out var option),
                $"{name} substitutes '{key}', which no userConfig option declares. The server would receive "
                + "the placeholder text as its credential.");

            var sensitive = option.TryGetProperty("sensitive", out var flag) && flag.GetBoolean();

            Assert.True(
                sensitive == isSecret,
                $"{name} is {(isSecret ? "secret" : "not secret")} in {ServerManifestPath} but "
                + $"{(sensitive ? "sensitive" : "not sensitive")} in {PluginPath}. A secret that is not marked "
                + "sensitive is written to settings.json in the clear.");
        }
    }

    private static JsonDocument Read(string root, string relativePath)
        => JsonDocument.Parse(File.ReadAllText(Path.Combine(root, relativePath)));

    /// <summary>The plugin bundles exactly one MCP server; more would need naming here to be meaningful.</summary>
    private static JsonElement SingleServer(JsonDocument plugin)
    {
        var servers = plugin.RootElement.GetProperty("mcpServers").EnumerateObject().ToList();

        Assert.Single(servers);
        return servers[0].Value;
    }

    /// <summary>The version authority: <c>CHANGELOG.md</c>'s first line is a <c># version</c> header.</summary>
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
