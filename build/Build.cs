using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;

using Fallout.Common;
using Fallout.Common.CI;
using Fallout.Common.CI.GitHubActions;
using Fallout.Common.IO;
using Fallout.Common.Tooling;
using Fallout.Common.Tools.DotNet;
using Fallout.Common.Utilities.Collections;
using Fallout.Components;
using Fallout.Solutions;

using Serilog;

using static Fallout.Common.Tools.DotNet.DotNetTasks;

using Project = Fallout.Solutions.Project;

/// <summary>
/// The Fallout orchestrator for bitbucket-mcp.
/// </summary>
/// <remarks>
/// Restore / Compile / Test come from the Fallout.Components interfaces; only the two things
/// that are specific to shipping a Native AOT MCP server are hand-written: <c>PublishAot</c>
/// (publish + archive per RID) and <c>SmokeTest</c> (a real stdio JSON-RPC handshake against
/// the published binary).
/// </remarks>
[ShutdownDotNetAfterServerBuild]
partial class Build : FalloutBuild,
    IHasSolution,
    IHasConfiguration,
    IHasArtifacts,
    IHasChangelog,
    IHasGitRepository,
    IRestore,
    ICompile,
    ITest,
    ICreateGitHubRelease
{
    public static int Main() => Execute<Build>(x => ((ITest)x).Test);

    /// <summary>The binary's product name; also the MCP <c>serverInfo.name</c> asserted by SmokeTest.</summary>
    const string ProductName = "bitbucket-mcp";

    /// <summary>
    /// The tool names <c>tools/list</c> must return, verbatim and complete. Grouped read-then-write
    /// to match the two tool classes; the comparison sorts both sides, so the order here is only
    /// for readers.
    /// </summary>
    static readonly string[] ExpectedToolNames =
    [
        // PullRequestReadTools
        "listPullRequests",
        "getPullRequest",
        "getPullRequestDiff",
        "getPullRequestComments",

        // PullRequestWriteTools
        "createPullRequest",
        "updatePullRequest",
        "addPullRequestComment",
        "setPullRequestReviewStatus",
        "mergePullRequest",
        "declinePullRequest",
    ];

    /// <summary>How long SmokeTest waits for both JSON-RPC responses before giving up.</summary>
    static readonly TimeSpan SmokeTestTimeout = TimeSpan.FromSeconds(30);

    [Parameter("Runtime identifier to publish for - defaults to the host RID")]
    readonly string Runtime = RuntimeInformation.RuntimeIdentifier;

    [Solution] readonly Solution Solution;
    Solution IHasSolution.Solution => Solution;

    public AbsolutePath ArtifactsDirectory => RootDirectory / "artifacts";

    AbsolutePath SourceDirectory => RootDirectory / "src";
    AbsolutePath TestsDirectory => RootDirectory / "tests";
    // Spelled out rather than derived from ProductName: on Linux the path is case-sensitive.
    AbsolutePath ServerProject => SourceDirectory / "Bitbucket.Mcp" / "Bitbucket.Mcp.csproj";
    AbsolutePath ChangelogPath => RootDirectory / "CHANGELOG.md";
    AbsolutePath PublishDirectory => ArtifactsDirectory / "publish" / Runtime;
    AbsolutePath StagingDirectory => ArtifactsDirectory / "staging" / Runtime;
    AbsolutePath ArchivesDirectory => ArtifactsDirectory / "archives";
    AbsolutePath ReleaseNotesFile => ArtifactsDirectory / "release-notes.md";

    bool IsWindowsRuntime => Runtime.StartsWith("win", StringComparison.OrdinalIgnoreCase);
    string ExecutableName => IsWindowsRuntime ? ProductName + ".exe" : ProductName;
    string ArchiveExtension => IsWindowsRuntime ? ".zip" : ".tar.gz";
    AbsolutePath PublishedExecutable => PublishDirectory / ExecutableName;
    AbsolutePath ArchiveFile => ArchivesDirectory / $"{ProductName}-{Version}-{Runtime}{ArchiveExtension}";

    /// <summary>The version parsed out of CHANGELOG.md - the single version authority.</summary>
    string Version { get; set; }

    ReleaseNotes LatestReleaseNotes { get; set; }

    /// <summary>True when this build is running for a <c>v*</c> tag - i.e. it is a release build.</summary>
    /// <remarks>
    /// Fallout's own repositories derive this from <c>GitRepository.Tags</c> because there the tag
    /// <em>is</em> the version. Here CHANGELOG.md is the version authority and the tag only has to
    /// agree with it, so on GitHub Actions the ref the run was triggered for is the authoritative
    /// answer: <c>GITHUB_REF_NAME</c> is the tag name on a tag push, and it is what
    /// <see cref="AssertReleaseTagMatchesChangelogVersion"/> compares against anyway. Off CI it
    /// falls back to a v* tag pointing at HEAD, so the gate can be exercised locally.
    /// </remarks>
    bool IsTaggedBuild => VersionTag != null;

    /// <summary>The <c>v*</c> tag this build is running for, or <c>null</c> if it is not a tag build.</summary>
    string VersionTag
    {
        get
        {
            if (GitHubActions.Instance == null)
            {
                return ((IHasGitRepository)this).GitRepository?.Tags.FirstOrDefault(IsVersionTag);
            }

            // GITHUB_REF_TYPE distinguishes a tag push from a branch push, but it is only consulted
            // when it is actually set, so the gate stays exercisable with GITHUB_REF_NAME alone.
            var refType = Environment.GetEnvironmentVariable("GITHUB_REF_TYPE");
            var refName = Environment.GetEnvironmentVariable("GITHUB_REF_NAME");

            return refType is null or "tag" && IsVersionTag(refName) ? refName : null;
        }
    }

    /// <summary>A version tag is <c>v</c> followed by a digit - so a <c>vnext</c> branch is not one.</summary>
    static bool IsVersionTag(string value) =>
        value?.StartsWith('v') == true && value.Length > 1 && char.IsAsciiDigit(value[1]);

    protected override void OnBuildInitialized()
    {
        base.OnBuildInitialized();

        // CHANGELOG.md is the version authority (never mutated by the build). Its first line must
        // parse as a version header - a "# Changelog" title would abort here.
        var changelog = new ReleaseNotesParser().Parse(File.ReadAllText(ChangelogPath));
        LatestReleaseNotes = changelog.FirstOrDefault()
            .NotNull($"{ChangelogPath} contains no parsable release section");

        Version = LatestReleaseNotes.SemVersion.ToString();
        Log.Information("Version from {Changelog}: {Version}", ChangelogPath, Version);
    }

    Target Clean => _ => _
        .Description("Deletes all build output and the artifacts directory")
        .Before<IRestore>()
        .Executes(() =>
        {
            SourceDirectory.GlobDirectories("**/bin", "**/obj").DeleteDirectories();
            TestsDirectory.GlobDirectories("**/bin", "**/obj").DeleteDirectories();
            ArtifactsDirectory.CreateOrCleanDirectory();
        });

    IEnumerable<Project> ITest.TestProjects => Solution.GetAllProjects("*.Tests");

    Configure<DotNetBuildSettings> ICompile.CompileSettings => _ => _
        .SetProperty("Version", Version);

    Configure<DotNetTestSettings> ITest.TestSettings => _ => _
        .SetProperty("Version", Version);

    Target PublishAot => _ => _
        .Description("Publishes a Native AOT binary for --runtime and archives it into artifacts/archives")
        .Produces(ArchivesDirectory / "*.zip")
        .Produces(ArchivesDirectory / "*.tar.gz")
        .Executes(() =>
        {
            // Deliberately independent of Compile: the AOT publish is a self-contained, per-RID
            // Release publish (D9 - the RID only ever reaches the SDK through -r).
            PublishDirectory.CreateOrCleanDirectory();

            DotNetPublish(_ => _
                .SetProject(ServerProject)
                .SetConfiguration(Configuration.Release)
                .SetRuntime(Runtime)
                .SetSelfContained(true)
                .SetOutput(PublishDirectory)
                .SetProperty("Version", Version));

            Assert.True(PublishedExecutable.FileExists(),
                $"Native AOT publish did not produce '{PublishedExecutable}'");

            // Archive exactly the three files a user needs, not the whole publish directory.
            StagingDirectory.CreateOrCleanDirectory();
            PublishedExecutable.CopyToDirectory(StagingDirectory, ExistsPolicy.FileOverwrite);
            (RootDirectory / "LICENSE").CopyToDirectory(StagingDirectory, ExistsPolicy.FileOverwrite);
            (RootDirectory / "README.md").CopyToDirectory(StagingDirectory, ExistsPolicy.FileOverwrite);

            ArchivesDirectory.CreateDirectory();
            ArchiveFile.DeleteFile();

            if (IsWindowsRuntime)
            {
                StagingDirectory.ZipTo(ArchiveFile, fileMode: FileMode.Create);
            }
            else
            {
                // R2: NOT CompressionExtensions.TarGZipTo. It goes through SharpZipLib's
                // TarEntry.CreateEntryFromFile, which hard-codes every entry's mode to 0700 instead of
                // reading it off disk - the binary would come out of the archive unreadable by anyone
                // but the extracting user, and LICENSE/README.md would come out executable. The tar CLI
                // copies the real mode (0755 for a published AOT binary), and both the GitHub runners
                // and Git Bash on Windows ship it. Files are named explicitly so the archive is flat.
                ProcessTasks
                    .StartProcess("tar",
                        $"-czf {ArchiveFile} -C {StagingDirectory} {ExecutableName} LICENSE README.md")
                    .AssertWaitForExit()
                    .AssertZeroExitCode();
            }

            Log.Information("Created {Archive}", ArchiveFile);
            ReportSummary(_ => _
                .AddPair("Runtime", Runtime)
                .AddPair("Archive", ArchiveFile.Name));
        });

    Target SmokeTest => _ => _
        .Description("Runs a real stdio JSON-RPC handshake against the published AOT binary")
        .DependsOn(PublishAot)
        .Executes(() =>
        {
            var responses = RunHandshake();

            var serverName = responses[InitializeId]
                .GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString();
            Assert.True(serverName == ProductName,
                $"initialize returned serverInfo.name '{serverName}', expected '{ProductName}'");

            var toolsElement = responses[ToolsListId].GetProperty("result").GetProperty("tools");
            Assert.True(toolsElement.ValueKind == JsonValueKind.Array,
                $"tools/list returned a '{toolsElement.ValueKind}' for 'tools', expected an array");

            var toolNames = toolsElement.EnumerateArray()
                .Select(x => x.GetProperty("name").GetString())
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray();
            var expected = ExpectedToolNames.OrderBy(x => x, StringComparer.Ordinal).ToArray();

            Assert.True(toolNames.SequenceEqual(expected, StringComparer.Ordinal),
                $"tools/list returned [{string.Join(", ", toolNames)}], expected [{string.Join(", ", expected)}]");

            Log.Information("Handshake OK: {Server} exposed {Count} tool(s)", serverName, toolNames.Length);
            ReportSummary(_ => _
                .AddPair("Server", serverName)
                .AddPair("Tools", toolNames.Length.ToString()));
        });

    const int InitializeId = 1;
    const int ToolsListId = 2;

    /// <summary>
    /// Spawns the published binary, drives one initialize / initialized / tools/list exchange and
    /// returns the responses keyed by JSON-RPC id.
    /// </summary>
    /// <remarks>
    /// stdin is deliberately held open until both responses have been read: the stdio transport
    /// tears down as soon as stdin hits EOF and drops whatever is still in flight, so closing
    /// stdin first loses responses. Responses are matched by id because the order is not
    /// guaranteed.
    /// </remarks>
    IReadOnlyDictionary<int, JsonElement> RunHandshake()
    {
        // The literal ids must stay in sync with InitializeId / ToolsListId below; the JSON is
        // written out verbatim rather than interpolated so it reads exactly as it goes on the wire.
        var requests = new[]
        {
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"fallout-smoketest","version":"1.0.0"}}}""",
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""",
        };

        var startInfo = new ProcessStartInfo
                        {
                            FileName = PublishedExecutable,
                            WorkingDirectory = PublishDirectory,
                            UseShellExecute = false,
                            RedirectStandardInput = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                        };

        var responses = new Dictionary<int, JsonElement>();
        var diagnostics = new List<string>();
        Exception readerFailure = null;

        using var process = new Process { StartInfo = startInfo };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null)
            {
                return;
            }

            lock (diagnostics)
            {
                diagnostics.Add(e.Data);
            }
        };

        Log.Information("Starting {Executable}", PublishedExecutable);
        process.Start();
        process.BeginErrorReadLine();

        var reader = new Thread(() =>
        {
            try
            {
                string line;
                while ((line = process.StandardOutput.ReadLine()) != null)
                {
                    if (line.Length == 0)
                    {
                        continue;
                    }

                    using var document = JsonDocument.Parse(line);
                    if (document.RootElement.TryGetProperty("id", out var id) && id.TryGetInt32(out var value))
                    {
                        lock (responses)
                        {
                            responses[value] = document.RootElement.Clone();
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                readerFailure = exception;
            }
        }) { IsBackground = true };
        reader.Start();

        try
        {
            foreach (var request in requests)
            {
                process.StandardInput.WriteLine(request);
                process.StandardInput.Flush();
            }

            var stopwatch = Stopwatch.StartNew();
            while (true)
            {
                lock (responses)
                {
                    if (responses.ContainsKey(InitializeId) && responses.ContainsKey(ToolsListId))
                    {
                        break;
                    }
                }

                if (readerFailure != null)
                {
                    throw new InvalidOperationException(
                        $"Reading the server's stdout failed.{FormatDiagnostics(diagnostics)}", readerFailure);
                }

                if (process.HasExited && stopwatch.Elapsed > TimeSpan.FromSeconds(1))
                {
                    throw new InvalidOperationException(
                        $"The server exited with code {process.ExitCode} before answering." +
                        FormatDiagnostics(diagnostics));
                }

                Assert.True(stopwatch.Elapsed < SmokeTestTimeout,
                    $"Timed out after {SmokeTestTimeout.TotalSeconds:0} s waiting for the initialize and " +
                    $"tools/list responses.{FormatDiagnostics(diagnostics)}");

                Thread.Sleep(millisecondsTimeout: 25);
            }

            lock (responses)
            {
                return new Dictionary<int, JsonElement>(responses);
            }
        }
        finally
        {
            // Only now: EOF on stdin is the server's shutdown signal.
            TryCloseInput(process);

            if (!process.WaitForExit(milliseconds: 5000))
            {
                Log.Warning("Server did not exit after stdin was closed; killing it");
                process.Kill(entireProcessTree: true);
            }

            reader.Join(TimeSpan.FromSeconds(5));
        }
    }

    static void TryCloseInput(Process process)
    {
        try
        {
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The server may already have gone away.
        }
    }

    static string FormatDiagnostics(List<string> diagnostics)
    {
        lock (diagnostics)
        {
            return diagnostics.Count == 0
                ? string.Empty
                : Environment.NewLine + "Server stderr:" + Environment.NewLine + string.Join(Environment.NewLine, diagnostics);
        }
    }

    string ICreateGitHubRelease.Name => $"v{Version}";

    /// <summary>A version with a pre-release suffix (1.0.0-rc.1, 0.0.1-test) never gets marked "Latest".</summary>
    bool ICreateGitHubRelease.Prerelease => Version.Contains('-');

    IEnumerable<AbsolutePath> ICreateGitHubRelease.AssetFiles => ArchivesDirectory.GlobFiles("*.zip", "*.tar.gz");

    /// <summary>
    /// The release body is read from here rather than from CHANGELOG.md directly.
    /// </summary>
    /// <remarks>
    /// <c>ICreateGitHubRelease</c> builds the body with <c>ChangelogTasks.ExtractChangelogSectionNotes</c>,
    /// which only recognises <c>## </c> headings and stops a section at the first line that is not a
    /// bullet. Our changelog uses <c>#</c> headings (the format <c>ReleaseNotesParser</c> - the version
    /// authority - expects) and wraps its bullets over several lines, so pointed at CHANGELOG.md that
    /// helper finds nothing and the release ships with an empty body. <see cref="WriteReleaseNotes"/>
    /// rewrites the top section into the shape it does understand.
    /// </remarks>
    string IHasChangelog.ChangelogFile => ReleaseNotesFile;

    // Both actions run before the inherited release logic: actions are appended in call order.
    Target ICreateGitHubRelease.CreateGitHubRelease => _ => _
        .Executes(AssertReleaseTagMatchesChangelogVersion)
        .Executes(WriteReleaseNotes)
        .Inherit<ICreateGitHubRelease>();

    /// <summary>
    /// Rewrites the newest CHANGELOG.md section into <see cref="ReleaseNotesFile"/> as a
    /// <c>## version</c> heading followed by one single-line bullet per entry, which is the only
    /// shape <c>ExtractChangelogSectionNotes</c> reads back in full.
    /// </summary>
    void WriteReleaseNotes()
    {
        var bullets = new List<string>();

        foreach (var line in LatestReleaseNotes.Notes)
        {
            // A "- " line opens a new entry; every other line is the continuation of a wrapped one.
            // The leading prose paragraph has no bullet to continue, so it becomes an entry itself.
            if (line.StartsWith("- ", StringComparison.Ordinal) || bullets.Count == 0)
            {
                bullets.Add(line.StartsWith("- ", StringComparison.Ordinal) ? line[2..] : line);
            }
            else
            {
                bullets[^1] += " " + line;
            }
        }

        var lines = new List<string> { $"## {Version}" };
        lines.AddRange(bullets.Select(x => $"- {x}"));

        ReleaseNotesFile.Parent.CreateDirectory();
        ReleaseNotesFile.WriteAllLines(lines.ToArray());

        Log.Information("Wrote {Count} release note(s) to {File}", bullets.Count, ReleaseNotesFile);
    }

    /// <summary>
    /// In CI the git tag is what people see; CHANGELOG.md is what the build believes. If the two
    /// disagree the release would be named after one and contain the other, so fail loudly.
    /// </summary>
    void AssertReleaseTagMatchesChangelogVersion()
    {
        if (GitHubActions.Instance == null)
        {
            Log.Warning("Not running in GitHub Actions - skipping the release tag check");
            return;
        }

        var expected = $"v{Version}";
        var actual = Environment.GetEnvironmentVariable("GITHUB_REF_NAME");

        Assert.True(actual == expected,
            $"Refusing to publish: the workflow ran for ref '{actual}' but CHANGELOG.md says the version " +
            $"is {Version} (tag '{expected}'). Tag the commit that carries the matching CHANGELOG entry.");
    }
}
