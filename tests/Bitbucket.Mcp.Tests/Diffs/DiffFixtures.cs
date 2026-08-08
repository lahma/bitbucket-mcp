using System.Collections.Concurrent;

using Bitbucket.Mcp.Diffs;

namespace Bitbucket.Mcp.Tests.Diffs;

/// <summary>
/// Loads the <c>Fixtures/diff-*.diff</c> embedded resources.
/// </summary>
/// <remarks>
/// The text is normalised to LF on the way out. <c>.gitattributes</c> pins <c>eol=lf</c> per
/// extension and does not list <c>.diff</c>, so a Windows checkout (<c>core.autocrlf=true</c>)
/// hands these files to the compiler with CRLF while CI on Linux sees LF. Normalising here keeps
/// every expectation platform-independent; the CRLF code path is still covered, by
/// <see cref="ToCrlf"/> re-inflating a fixture on purpose.
/// </remarks>
internal static class DiffFixtures
{
    /// <summary>A realistic multi-file diff: preamble, modification, rename, deletion, addition, two binaries.</summary>
    internal const string MultiFile = "diff-multi-file.diff";

    /// <summary>A single two-hunk file shaped for inline-anchor resolution.</summary>
    internal const string Anchors = "diff-anchors.diff";

    /// <summary>A path git escaped octally: <c>"a/src/caf\303\251.cs"</c>.</summary>
    internal const string OctalPath = "diff-octal-path.diff";

    /// <summary>Output produced with <c>--no-prefix</c>: no <c>a/</c> or <c>b/</c> on any path.</summary>
    internal const string NoPrefix = "diff-no-prefix.diff";

    /// <summary>A plain <c>diff -u</c> with no <c>diff --git</c> line at all.</summary>
    internal const string Headerless = "diff-headerless.diff";

    /// <summary>A <c>GIT binary patch</c> with a few hundred literal lines.</summary>
    internal const string BinaryPatch = "diff-binary-patch.diff";

    /// <summary>One added file whose body runs past the 4000-line global budget.</summary>
    internal const string LargeFile = "diff-large-file.diff";

    private static readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.Ordinal);

    /// <summary>Reads a fixture's text, with LF line endings whatever the checkout did.</summary>
    internal static string Read(string fileName) => Cache.GetOrAdd(fileName, static name =>
    {
        var assembly = typeof(DiffFixtures).Assembly;
        var suffix = "." + name;
        string? resource = null;

        foreach (var candidate in assembly.GetManifestResourceNames())
        {
            if (candidate.EndsWith(suffix, StringComparison.Ordinal))
            {
                resource = candidate;
                break;
            }
        }

        if (resource is null)
        {
            throw new InvalidOperationException(
                $"Embedded fixture '{name}' not found. Available: {string.Join(", ", assembly.GetManifestResourceNames())}");
        }

        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Embedded fixture '{resource}' had no stream.");
        using var reader = new StreamReader(stream);

        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    });

    /// <summary>Reads and parses a fixture.</summary>
    internal static IReadOnlyList<DiffFile> Split(string fileName) => UnifiedDiffParser.Split(Read(fileName));

    /// <summary>Turns LF text into CRLF text, for the "CRLF input parses like LF input" tests.</summary>
    internal static string ToCrlf(string lfText) => lfText.Replace("\n", "\r\n", StringComparison.Ordinal);
}
