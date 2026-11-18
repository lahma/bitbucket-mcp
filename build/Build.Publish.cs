using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;

using Fallout.Common;
using Fallout.Common.CI.GitHubActions;
using Fallout.Common.IO;
using Fallout.Common.Tooling;
using Fallout.Common.Tools.DotNet;
using Fallout.Common.Utilities.Collections;
using Fallout.Common.Utilities.Net;

using Serilog;

using static Fallout.Common.Tools.DotNet.DotNetTasks;

/// <summary>
/// The NuGet distribution channel (D17): <c>Pack</c> builds the framework-dependent .NET tool
/// package and <c>Publish</c> pushes it to nuget.org with a short-lived API key minted from the
/// workflow's GitHub OIDC token (trusted publishing - no stored NuGet API key anywhere). Then
/// <c>PublishMcpRegistry</c> lists that version in the official MCP Registry (D25), with a
/// registry token minted from the same kind of OIDC token.
/// </summary>
/// <remarks>
/// The Native AOT binaries on GitHub Releases remain the primary channel; this one exists so a
/// user can type <c>dnx bitbucket-mcp</c> without downloading an archive first. There is no preview
/// feed: the only push this repository ever makes is a tagged release to nuget.org, which is why
/// <c>Publish</c> is a single-source target gated on <see cref="Build.IsTaggedBuild"/> rather than
/// a two-way switch between a release feed and a preview one.
/// </remarks>
partial class Build
{
    const string NuGetSource = "https://api.nuget.org/v3/index.json";

    /// <summary>The OIDC audience nuget.org expects. Not <c>nuget</c>, whatever the design doc says.</summary>
    const string NuGetAudience = "https://www.nuget.org";

    const string NuGetTokenServiceUrl = "https://www.nuget.org/api/v2/token";

    /// <summary>
    /// Sent with every request the build makes to nuget.org and the MCP Registry. Any non-empty
    /// User-Agent will do, but there must be one - see <see cref="GetTrustedPublishingApiKey"/>.
    /// </summary>
    const string BuildUserAgent = "bitbucket-mcp-build/1.0";

    const string McpRegistryUrl = "https://registry.modelcontextprotocol.io";

    /// <summary>The OIDC audience the registry checks: its own scheme and host.</summary>
    const string McpRegistryAudience = "https://registry.modelcontextprotocol.io";

    /// <summary>The nuget.org service-index resource a package version's README is served through.</summary>
    const string ReadmeUriTemplateResource = "ReadmeUriTemplate/6.13.0";

    /// <summary>
    /// How long <c>PublishMcpRegistry</c> waits for nuget.org to serve the README of the version just
    /// pushed. Validation and indexing took about nine minutes for 1.3.1; this leaves room for a slow day.
    /// </summary>
    static readonly TimeSpan NuGetReadmeTimeout = TimeSpan.FromMinutes(40);

    static readonly TimeSpan NuGetReadmePollInterval = TimeSpan.FromSeconds(30);

    /// <summary>The workflow file the nuget.org trusted publishing policy is scoped to.</summary>
    /// <remarks>
    /// Only used in the error message, but it is the field people get wrong, so it is worth naming
    /// the actual value rather than describing it.
    /// </remarks>
    const string PublishWorkflowFile = "publish.yml";

    [Parameter("nuget.org profile name of the account that owns the trusted publishing policy " +
               "(env: NUGET_USER). Public information, not a secret.")]
    readonly string NuGetUser;

    AbsolutePath PackagesDirectory => ArtifactsDirectory / "packages";
    AbsolutePath ServerManifestFile => RootDirectory / ".mcp" / "server.json";

    Target Pack => _ => _
        .Description("Packs the server as a .NET tool / MCP server NuGet package into artifacts/packages")
        .Produces(PackagesDirectory / "*.nupkg")
        .Executes(() =>
        {
            // Independent of Compile for the same reason PublishAot is: this is a Release build of
            // one project, and `dotnet pack` of a tool package runs its own publish anyway.
            PackagesDirectory.CreateOrCleanDirectory();

            DotNetPack(_ => _
                .SetProject(ServerProject)
                .SetConfiguration(Configuration.Release)
                .SetOutputDirectory(PackagesDirectory)
                .SetProperty("Version", Version));

            var packages = PackagesDirectory.GlobFiles("*.nupkg");
            Assert.NotEmpty(packages, $"dotnet pack produced no .nupkg in '{PackagesDirectory}'");

            foreach (var package in packages)
            {
                Log.Information("Packed {Package}", package.Name);
            }

            ReportSummary(_ => _
                .AddPair("Version", Version)
                .AddPair("Packages", packages.Count.ToString()));
        });

    Target Publish => _ => _
        .Description("Pushes artifacts/packages to nuget.org using a trusted-publishing (OIDC) key")
        .DependsOn(Pack)
        .OnlyWhenDynamic(() => ShouldPublish())
        .Executes(() =>
        {
            AssertPublishContext();

            var packages = PackagesDirectory.GlobFiles("*.nupkg");
            Assert.NotEmpty(packages, $"Nothing to publish: no .nupkg in '{PackagesDirectory}'");

            // Minted here rather than in an earlier target or job: the key lives 15-60 minutes and
            // one OIDC token mints exactly one key.
            var apiKey = GetTrustedPublishingApiKey();

            DotNetNuGetPush(_ => _
                    .SetSource(NuGetSource)
                    .SetApiKey(apiKey)
                    .EnableSkipDuplicate()
                    .CombineWith(packages, (_, v) => _.SetTargetPath(v)),
                degreeOfParallelism: 1,
                completeOnFailure: true);

            ReportSummary(_ => _
                .AddPair("Pushed", packages.Count.ToString())
                .AddPair("Version", Version));
        });

    /// <remarks>
    /// D25. The registry proves ownership of <c>io.github.lahma/...</c> by finding
    /// <c>mcp-name: &lt;server name&gt;</c> in the README nuget.org serves for this exact version, and
    /// refuses the publish until it can, so the target first waits for that README - through the same
    /// service-index resource the registry reads it from. It runs after <c>Publish</c>, so a registry
    /// failure cannot hold up or undo the nuget.org push; it fails the job loudly instead, and a
    /// re-run is safe: <c>Publish</c> skips the duplicate push, and a version the registry already
    /// lists is reported and left alone (versions there are immutable).
    /// </remarks>
    Target PublishMcpRegistry => _ => _
        .Description("Lists .mcp/server.json in the MCP Registry once nuget.org serves this version's README")
        .DependsOn(Publish)
        // Not ShouldPublish(): Publish already explained the skip, and the summary names this condition.
        .OnlyWhenDynamic(() => IsTaggedBuild)
        .Executes(() =>
        {
            AssertPublishContext();

            var manifest = JsonNode.Parse(ServerManifestFile.ReadAllText())!;
            var serverName = manifest["name"]!.GetValue<string>();
            var packageId = manifest["packages"]![0]!["identifier"]!.GetValue<string>();

            using var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd(BuildUserAgent);

            if (IsListedInMcpRegistry(client, serverName))
            {
                Log.Information("The MCP Registry already lists {Server} {Version}; nothing to publish", serverName, Version);
                ReportSummary(_ => _.AddPair("Registry", $"{serverName} {Version} (already listed)"));
                return;
            }

            WaitForPackageReadme(client, packageId, serverName);

            var registryToken = client
                .CreateRequest(HttpMethod.Post, $"{McpRegistryUrl}/v0/auth/github-oidc")
                .WithJsonContent(new { oidc_token = GetGitHubOidcToken(McpRegistryAudience) })
                .GetResponse()
                .AssertResponse(x => x.IsSuccessStatusCode
                    ? null
                    : $"MCP Registry token exchange failed ({(int) x.StatusCode}): " +
                      $"{x.Content.ReadAsStringAsync().GetAwaiter().GetResult()}. The job needs " +
                      "'permissions: id-token: write', and the repository owner must own the io.github.<owner> " +
                      $"namespace of '{serverName}'.")
                .GetBodyAsJsonObject().GetAwaiter().GetResult()["registry_token"]
                .NotNull("The MCP Registry returned no registry_token")
                .GetValue<string>();

            GitHubActions.Instance?.WriteCommand("add-mask", registryToken);

            client
                .CreateRequest(HttpMethod.Post, $"{McpRegistryUrl}/v0/publish")
                .WithBearerAuthentication(registryToken)
                .WithJsonContent(manifest)
                .GetResponse()
                .AssertResponse(x => x.IsSuccessStatusCode
                    ? null
                    : $"MCP Registry publish of {serverName} {Version} failed ({(int) x.StatusCode}): " +
                      $"{x.Content.ReadAsStringAsync().GetAwaiter().GetResult()}. The nuget.org package is already " +
                      "pushed; fix the cause and re-run the job.");

            Log.Information("Published {Server} {Version} to the MCP Registry", serverName, Version);
            ReportSummary(_ => _.AddPair("Registry", $"{serverName} {Version}"));
        });

    /// <summary>
    /// Whether the registry already lists this version: <c>200</c> from the version endpoint, against
    /// <c>404</c> for one it does not have. Anything else is an answer the publish cannot act on.
    /// </summary>
    bool IsListedInMcpRegistry(HttpClient client, string serverName)
    {
        var url = $"{McpRegistryUrl}/v0/servers/{Uri.EscapeDataString(serverName)}/versions/{Uri.EscapeDataString(Version)}";

        using var response = client.GetAsync(url).GetAwaiter().GetResult();

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        Assert.True(response.IsSuccessStatusCode,
            $"Could not ask the MCP Registry whether it lists {serverName} {Version}: GET {url} answered " +
            $"{(int) response.StatusCode}. Re-run the job once the registry answers.");

        return true;
    }

    /// <summary>
    /// Polls the README nuget.org serves for this version until it exists, then checks it carries the
    /// registry's ownership marker - a README without it is refused outright, so waiting longer would
    /// not help.
    /// </summary>
    void WaitForPackageReadme(HttpClient client, string packageId, string serverName)
    {
        var template = JsonNode.Parse(client.GetStringAsync(NuGetSource).GetAwaiter().GetResult())!["resources"]!
            .AsArray()
            .FirstOrDefault(x => x!["@type"]!.GetValue<string>() == ReadmeUriTemplateResource)
            .NotNull($"nuget.org's service index has no {ReadmeUriTemplateResource}")!["@id"]!
            .GetValue<string>();

        var url = template
            .Replace("{lower_id}", packageId.ToLowerInvariant(), StringComparison.Ordinal)
            .Replace("{lower_version}", Version.ToLowerInvariant(), StringComparison.Ordinal);
        var marker = $"mcp-name: {serverName}";
        var deadline = DateTimeOffset.UtcNow + NuGetReadmeTimeout;

        while (true)
        {
            using var response = client.GetAsync(url).GetAwaiter().GetResult();

            if (response.IsSuccessStatusCode)
            {
                Assert.True(
                    response.Content.ReadAsStringAsync().GetAwaiter().GetResult().Contains(marker, StringComparison.Ordinal),
                    $"nuget.org serves the README of {packageId} {Version} without '{marker}', so the MCP Registry would " +
                    "refuse the publish. README.md must carry the marker in the version it is packed into.");
                return;
            }

            Assert.True(DateTimeOffset.UtcNow < deadline,
                $"nuget.org still did not serve {url} after {NuGetReadmeTimeout.TotalMinutes:0} minutes. The package " +
                "is pushed; re-run the job once it does.");

            Log.Information("Waiting for nuget.org to serve the README of {Package} {Version} ({Status})",
                packageId, Version, (int) response.StatusCode);
            Thread.Sleep(NuGetReadmePollInterval);
        }
    }

    /// <summary>
    /// The tag gate. There is exactly one thing a <c>Publish</c> run can legitimately be - a v* tag
    /// build pushing to nuget.org - so anything else is skipped rather than failed, which keeps
    /// <c>Compile Test Pack Publish</c> a safe line to run anywhere. Skipping is never silent.
    /// </summary>
    bool ShouldPublish()
    {
        if (IsTaggedBuild)
        {
            return true;
        }

        Log.Warning(
            "Skipping Publish: this build is not a v* tag build, and nothing is pushed to nuget.org from " +
            "anything else (there is no preview feed). Run `Pack` to build and inspect the package locally; " +
            "push tag v{Version} - the version CHANGELOG.md declares - to publish it.", Version);

        return false;
    }

    /// <summary>
    /// Refuses to run anywhere the OIDC exchange cannot possibly succeed, with the reason spelled
    /// out. Same tag-versus-CHANGELOG rule as <c>CreateGitHubRelease</c>: CHANGELOG.md is the
    /// version authority, so the tag has to agree with it or the package version and the release
    /// name would tell different stories.
    /// </summary>
    void AssertPublishContext()
    {
        Assert.True(GitHubActions.Instance != null,
            "Publish only runs on GitHub Actions: the nuget.org API key is minted from the workflow's " +
            "OIDC token, which exists nowhere else. Run `.\\build.ps1 Pack` locally to inspect the package, " +
            "and push a v* tag to publish.");

        Assert.NotNullOrWhiteSpace(NuGetUser,
            "The nuget.org profile name of the trusted publishing policy's creator is required. Set it as " +
            "NUGET_USER (env:) on the publish job, or pass --nuget-user. It is public information, " +
            "not a secret.");

        var expected = $"v{Version}";
        var actual = Environment.GetEnvironmentVariable("GITHUB_REF_NAME");

        Assert.True(actual == expected,
            $"Refusing to publish to nuget.org: the workflow ran for ref '{actual}' but CHANGELOG.md says the " +
            $"version is {Version} (tag '{expected}'). Tag the commit that carries the matching CHANGELOG entry.");
    }

    /// <summary>
    /// The job's GitHub OIDC token for one audience. Each consumer checks its own audience, so
    /// nuget.org and the MCP Registry each get a token of their own.
    /// </summary>
    static string GetGitHubOidcToken(string audience)
    {
        const string MissingOidc =
            "GitHub OIDC is unavailable - the publish job needs 'permissions: id-token: write'";

        var requestUrl = Assert.NotNullOrWhiteSpace(
            Environment.GetEnvironmentVariable("ACTIONS_ID_TOKEN_REQUEST_URL"), MissingOidc);
        var requestToken = Assert.NotNullOrWhiteSpace(
            Environment.GetEnvironmentVariable("ACTIONS_ID_TOKEN_REQUEST_TOKEN"), MissingOidc);

        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(BuildUserAgent);

        return client
            .CreateRequest(HttpMethod.Get, $"{requestUrl}&audience={Uri.EscapeDataString(audience)}")
            .WithBearerAuthentication(requestToken)
            .GetResponse()
            .AssertSuccessfulStatusCode()
            .GetBodyAsJsonObject().GetAwaiter().GetResult()["value"].GetValue<string>();
    }

    /// <summary>
    /// Exchanges the job's GitHub OIDC token for a short-lived nuget.org API key - what
    /// <c>NuGet/login@v1</c> does, without taking a dependency on a marketplace action.
    /// </summary>
    string GetTrustedPublishingApiKey()
    {
        var idToken = GetGitHubOidcToken(NuGetAudience);

        using var client = new HttpClient();

        // REQUIRED. nuget.org's token endpoint sits behind Azure Front Door, which answers
        // HTTP 400 "A User-Agent header is required." to a request that carries none - and a bare
        // HttpClient sends none. The check runs before the token or the policy is looked at, so the
        // symptom of forgetting this is a 400 rather than the 401 a misconfiguration would give.
        // The JS http client behind NuGet/login always sends one, which is why only in-build .NET
        // callers ever hit it.
        client.DefaultRequestHeaders.UserAgent.ParseAdd(BuildUserAgent);

        var body = client
            .CreateRequest(HttpMethod.Post, NuGetTokenServiceUrl)
            .WithBearerAuthentication(idToken)
            .WithJsonContent(new { username = NuGetUser, tokenType = "ApiKey" })
            .GetResponse()
            // Surface the response body: nuget.org answers { "error": "..." } and it usually names
            // the cause outright.
            .AssertResponse(x => x.IsSuccessStatusCode
                ? null
                : $"nuget.org token exchange failed ({(int) x.StatusCode}): " +
                  $"{x.Content.ReadAsStringAsync().GetAwaiter().GetResult()}. A 400 means the request shape is " +
                  "wrong (checked before any policy lookup); a 401 means no trusted publishing policy matched " +
                  $"repository lahma/bitbucket-mcp, workflow file {PublishWorkflowFile}, environment nuget and " +
                  $"creator '{NuGetUser}'.")
            .GetBodyAsJsonObject().GetAwaiter().GetResult();

        // The action reads 'apiKey'; the original design document says 'api_key'. Accept either.
        var apiKey = (body["apiKey"] ?? body["api_key"])
            .NotNull($"nuget.org returned no API key: {body.ToJsonString()}")
            .GetValue<string>();

        GitHubActions.Instance?.WriteCommand("add-mask", apiKey);
        Log.Information("Minted a short-lived nuget.org API key, expires {Expires}",
            body["expires"]?.GetValue<string>() ?? "(not reported)");

        return apiKey;
    }
}
