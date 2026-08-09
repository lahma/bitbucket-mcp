using System.Text.RegularExpressions;

using Bitbucket.Mcp.Tests.Tools;

using Xunit;

namespace Bitbucket.Mcp.Tests;

/// <summary>
/// The shipped Agent Skill (<c>.claude/skills/bitbucket-pull-requests/SKILL.md</c>) against the
/// tool surface it describes.
/// </summary>
/// <remarks>
/// <para>
/// The skill teaches the multi-tool workflow — diffstat before diff content, statuses before a
/// merge, the deduplication check before a create — which no tool schema can hold, because each
/// schema only sees its own tool. Nothing loads that file at build time, so it is exactly the kind
/// of document that keeps advertising last month's tool surface: the inventory grew from ten tools
/// to sixteen during development, and a skill written before that would still read as correct.
/// This test is what makes the skill a third place a tool has to be added, alongside AGENTS.md's
/// <em>Tool table</em> and <see cref="Tools.ToolInventoryTests"/>.
/// </para>
/// <para>
/// It checks both directions. A name the skill uses that no tool answers to sends a model at a
/// tool that does not exist; a tool the skill never mentions is a tool the workflow silently drops.
/// </para>
/// <para>
/// <b>Calibration.</b> A reference is a backticked camelCase token whose leading lowercase run is
/// one of the verbs the inventory itself uses (<c>add</c>, <c>create</c>, <c>decline</c>,
/// <c>get</c>, <c>list</c>, <c>merge</c>, <c>resolve</c>, <c>set</c>, <c>update</c>) — the naming
/// convention tool names follow. Tool <em>parameter</em> names are excluded by reflection rather
/// than by a hand-kept list, which is what keeps <c>mergeStrategy</c> from reading as a missing
/// tool. The known limitation is a result <em>field</em> that happens to start with one of those
/// verbs — <c>mergeCommitHash</c> is the only one today — which would have to be mentioned without
/// backticks, or added to the exclusions. That is a louder failure than the alternative of matching
/// nothing, which is why the pattern errs this way.
/// </para>
/// </remarks>
public class AgentSkillTests
{
    /// <summary>The file that identifies the repository root when walking up from the test assembly.</summary>
    private const string RootMarker = "bitbucket-mcp.slnx";

    /// <summary>
    /// The canonical skill location. <c>.claude/skills/</c> is what Claude Code loads as a project
    /// skill from a checkout, and what Cursor and VS Code read for compatibility; every other tool
    /// is pointed at this path from AGENTS.md and README.md rather than given a second copy.
    /// </summary>
    private const string SkillDirectory = ".claude/skills/bitbucket-pull-requests";

    /// <summary>A backticked span on one line — the way the skill spells every identifier.</summary>
    private static readonly Regex BacktickedSpan = new("`([^`\n]+)`", RegexOptions.Compiled);

    /// <summary>A camelCase identifier: a lowercase run, then a capital, then anything.</summary>
    private static readonly Regex CamelCaseIdentifier = new("^([a-z]+)[A-Z][A-Za-z0-9]*$", RegexOptions.Compiled);

    /// <summary>The Agent Skills spec's <c>name</c> rule: lowercase alphanumerics, single hyphens.</summary>
    private static readonly Regex SkillName = new("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled);

    [Fact]
    public void EveryToolTheSkillNamesExists()
    {
        var toolNames = ToolNames();
        var unknown = ReferencedToolNames().Where(name => !toolNames.Contains(name)).ToList();

        Assert.True(
            unknown.Count == 0,
            $"{SkillDirectory}/SKILL.md names tools this server does not have: {string.Join(", ", unknown)}. "
            + "Rename them, or drop the guidance that used them.");
    }

    [Fact]
    public void EveryToolIsNamedInTheSkill()
    {
        var referenced = ReferencedToolNames();

        var missing = ToolNames()
            .Where(name => !referenced.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"{SkillDirectory}/SKILL.md never mentions {string.Join(", ", missing)}. A tool the skill leaves out "
            + "is a tool the workflow it teaches silently drops — place it in a playbook, in backticks.");
    }

    /// <summary>
    /// The frontmatter is the only part of a skill that is always in context, and the two fields
    /// the Agent Skills spec requires are what every client keys discovery off. <c>name</c> has to
    /// equal the directory name: clients that take the command from the directory and clients that
    /// take it from the field would otherwise disagree about what this skill is called.
    /// </summary>
    [Fact]
    public void FrontmatterFollowsTheAgentSkillsSpec()
    {
        var lines = File.ReadAllLines(SkillFile());

        Assert.Equal("---", lines[0]);

        var end = Array.IndexOf(lines, "---", 1);
        Assert.True(end > 0, "SKILL.md has no closing frontmatter delimiter.");

        var frontmatter = lines[1..end];

        var name = Value(frontmatter, "name");
        Assert.Equal(SkillDirectory[(SkillDirectory.LastIndexOf('/') + 1)..], name);
        Assert.Matches(SkillName, name);
        Assert.True(name.Length <= 64, $"name is {name.Length} characters; the spec allows 64.");

        var description = Value(frontmatter, "description");
        Assert.False(string.IsNullOrWhiteSpace(description), "description is required and drives invocation.");
        Assert.True(
            description.Length <= 1024,
            $"description is {description.Length} characters; the spec allows 1024.");
    }

    /// <summary>The sixteen MCP names, read off the built tools rather than off any list.</summary>
    private static HashSet<string> ToolNames() =>
        ToolTestHost.Tools.Select(tool => tool.ProtocolTool.Name).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Every backticked token in the skill that is shaped like one of this server's tool names, with
    /// the tools' own parameter names taken back out.
    /// </summary>
    private static HashSet<string> ReferencedToolNames()
    {
        var verbs = ToolNames()
            .Select(name => CamelCaseIdentifier.Match(name).Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        // Self-check: the verb set is derived from the names it is used to recognise, so an empty
        // one would make the whole scan match nothing and pass.
        Assert.NotEmpty(verbs);

        var parameters = ToolTestHost.ToolMethods
            .SelectMany(method => method.GetParameters())
            .Select(parameter => parameter.Name!)
            .ToHashSet(StringComparer.Ordinal);

        var referenced = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match span in BacktickedSpan.Matches(File.ReadAllText(SkillFile())))
        {
            var token = span.Groups[1].Value;
            var identifier = CamelCaseIdentifier.Match(token);

            if (identifier.Success && verbs.Contains(identifier.Groups[1].Value) && !parameters.Contains(token))
            {
                referenced.Add(token);
            }
        }

        // Self-check: a scan that finds nothing is broken, not reassuring.
        Assert.True(referenced.Count > 0, $"Found no tool references at all in {SkillDirectory}/SKILL.md.");

        return referenced;
    }

    /// <summary>Reads one frontmatter key, folding a <c>&gt;-</c> block scalar back onto one line.</summary>
    private static string Value(string[] frontmatter, string key)
    {
        var index = Array.FindIndex(frontmatter, line => line.StartsWith(key + ":", StringComparison.Ordinal));
        Assert.True(index >= 0, $"SKILL.md frontmatter has no '{key}' key.");

        var head = frontmatter[index][(key.Length + 1)..].Trim();
        List<string> folded = head is ">-" or ">" or "|" or "|-" ? [] : [head];

        for (var next = index + 1; next < frontmatter.Length && frontmatter[next].StartsWith(' '); next++)
        {
            folded.Add(frontmatter[next].Trim());
        }

        return string.Join(' ', folded).Trim();
    }

    private static string SkillFile()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, RootMarker)))
            {
                var file = Path.Combine(directory.FullName, SkillDirectory, "SKILL.md");
                Assert.True(File.Exists(file), $"The shipped skill is missing: {SkillDirectory}/SKILL.md.");
                return file;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException($"Could not find {RootMarker} above {AppContext.BaseDirectory}.");
    }
}
