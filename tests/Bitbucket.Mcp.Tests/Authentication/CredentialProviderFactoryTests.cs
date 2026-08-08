using System.Text;

using Bitbucket.Mcp.Authentication;
using Bitbucket.Mcp.Configuration;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Xunit;

namespace Bitbucket.Mcp.Tests.Authentication;

/// <summary>
/// Which credential the server ends up with, and — just as importantly — what choosing it costs.
/// The factory runs while the container is being built, where there is nowhere to report a failure
/// (stdout is the protocol channel), so it may only read the options object: no disk, no network,
/// and no decision that authentication is impossible.
/// </summary>
public sealed class CredentialProviderFactoryTests
{
    /// <summary>
    /// The documented precedence. The half-configured Basic rows matter most: an email without a
    /// token cannot authenticate anything, and a Basic header that always 401s would be a worse
    /// answer than falling through to OAuth.
    /// </summary>
    [Theory]
    [InlineData("access-token", null, null, "Bearer")]
    [InlineData("access-token", "me@example.com", "api-token", "Bearer")]
    [InlineData(null, "me@example.com", "api-token", "Basic")]
    [InlineData(null, "me@example.com", null, null)]
    [InlineData(null, null, "api-token", null)]
    [InlineData(null, null, null, null)]
    [InlineData("   ", "   ", "   ", null)]
    public async Task PrecedenceFollowsTheDocumentedTable(
        string? accessToken,
        string? email,
        string? apiToken,
        string? expectedScheme)
    {
        var options = EnvironmentWith(
            ("BITBUCKET_ACCESS_TOKEN", accessToken),
            ("BITBUCKET_EMAIL", email),
            ("BITBUCKET_API_TOKEN", apiToken));

        var provider = CredentialProviderFactory.CreateStatic(options);

        if (expectedScheme is null)
        {
            Assert.Null(provider);
            return;
        }

        Assert.NotNull(provider);

        var header = await provider.GetAuthenticationHeaderAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expectedScheme, header.Scheme);
    }

    [Fact]
    public async Task AccessTokenBecomesABearerHeaderVerbatim()
    {
        var options = EnvironmentWith(("BITBUCKET_ACCESS_TOKEN", "  workspace-access-token  "));

        var provider = CredentialProviderFactory.CreateStatic(options);

        Assert.NotNull(provider);

        var header = await provider.GetAuthenticationHeaderAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Bearer", header.Scheme);
        Assert.Equal("workspace-access-token", header.Parameter);
    }

    /// <summary>
    /// An Atlassian API token, not an app password — those were removed on 2026-07-28 and are
    /// deliberately not implemented — but the same wire shape.
    /// </summary>
    [Fact]
    public async Task EmailAndApiTokenBecomeBasicCredentials()
    {
        var options = EnvironmentWith(
            ("BITBUCKET_EMAIL", "me@example.com"),
            ("BITBUCKET_API_TOKEN", "the-api-token"));

        var provider = CredentialProviderFactory.CreateStatic(options);

        Assert.NotNull(provider);

        var header = await provider.GetAuthenticationHeaderAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Basic", header.Scheme);

        Assert.Equal(
            Convert.ToBase64String(Encoding.UTF8.GetBytes("me@example.com:the-api-token")),
            header.Parameter);
    }

    [Fact]
    public void DescriptionsNameTheSourceAndNeverTheSecret()
    {
        var bearer = CredentialProviderFactory.CreateStatic(
            EnvironmentWith(("BITBUCKET_ACCESS_TOKEN", "super-secret-access-token")));

        Assert.NotNull(bearer);
        Assert.Contains("BITBUCKET_ACCESS_TOKEN", bearer.Describe(), StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret-access-token", bearer.Describe(), StringComparison.Ordinal);

        var basic = CredentialProviderFactory.CreateStatic(
            EnvironmentWith(
                ("BITBUCKET_EMAIL", "me@example.com"),
                ("BITBUCKET_API_TOKEN", "super-secret-api-token")));

        Assert.NotNull(basic);
        Assert.Contains("BITBUCKET_EMAIL", basic.Describe(), StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret-api-token", basic.Describe(), StringComparison.Ordinal);

        // The Basic credential is base64, not encryption: the encoded form must not leak either.
        Assert.DoesNotContain(
            Convert.ToBase64String(Encoding.UTF8.GetBytes("me@example.com:super-secret-api-token")),
            basic.Describe(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A static credential must not drag the OAuth graph into existence — the container here can
    /// resolve nothing else, so anything more than an options read would throw.
    /// </summary>
    [Fact]
    public void StaticCredentialIsChosenWithoutResolvingTheOAuthGraph()
    {
        var services = new ServiceCollection();
        services.AddSingleton(EnvironmentWith(("BITBUCKET_ACCESS_TOKEN", "access-token")));

        using var container = services.BuildServiceProvider();

        var credential = CredentialProviderFactory.Create(container);

        Assert.IsType<StaticCredentialProvider>(credential);
    }

    [Fact]
    public void OAuthIsTheFallbackEvenWhenNoConsumerIsConfigured()
    {
        using var temp = new TempTokenDirectory();
        using var container = BuildContainer(new BitbucketMcpOptions { TokenFilePath = temp.TokenFilePath });

        var credential = CredentialProviderFactory.Create(container);

        try
        {
            Assert.IsType<OAuthCredentialProvider>(credential);
            Assert.Contains("not configured", credential.Describe(), StringComparison.Ordinal);
            Assert.False(Directory.Exists(temp.DirectoryPath), "Choosing a credential must not touch the disk.");
        }
        finally
        {
            (credential as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// A server with no credentials at all still completes the MCP handshake and still lists its
    /// tools; the failure arrives on the first tool call, where it can be reported, and it names
    /// every variable that would fix it.
    /// </summary>
    [Fact]
    public async Task UnconfiguredOAuthFailsOnFirstUseAndStillCreatesNoDirectory()
    {
        using var temp = new TempTokenDirectory();
        using var container = BuildContainer(new BitbucketMcpOptions { TokenFilePath = temp.TokenFilePath });

        var credential = CredentialProviderFactory.Create(container);

        try
        {
            var failure = await Assert.ThrowsAsync<AuthenticationRequiredException>(
                async () => await credential.GetAuthenticationHeaderAsync(TestContext.Current.CancellationToken));

            Assert.Equal(AuthenticationRequiredReason.NotConfigured, failure.Reason);
            Assert.Contains("BITBUCKET_ACCESS_TOKEN", failure.Message, StringComparison.Ordinal);
            Assert.Contains("BITBUCKET_OAUTH_KEY", failure.Message, StringComparison.Ordinal);
            Assert.Contains("bitbucket-mcp login", failure.Message, StringComparison.Ordinal);

            Assert.False(
                Directory.Exists(temp.DirectoryPath),
                "An unconfigured OAuth provider must fail before it creates anything.");
        }
        finally
        {
            (credential as IDisposable)?.Dispose();
        }
    }

    [Fact]
    public async Task InvalidatingAStaticCredentialIsANoOp()
    {
        var options = EnvironmentWith(("BITBUCKET_ACCESS_TOKEN", "access-token"));

        var provider = CredentialProviderFactory.CreateStatic(options);

        Assert.NotNull(provider);

        await provider.InvalidateAsync(TestContext.Current.CancellationToken);

        var header = await provider.GetAuthenticationHeaderAsync(TestContext.Current.CancellationToken);

        // There is nothing to refresh: an environment token that is rejected is wrong, not stale.
        Assert.Equal("access-token", header.Parameter);
    }

    /// <summary>Options from an arbitrary variable source, so the process environment is never touched.</summary>
    private static BitbucketMcpOptions EnvironmentWith(params (string Name, string? Value)[] variables)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var (name, value) in variables)
        {
            values[name] = value;
        }

        return BitbucketMcpOptions.FromEnvironment(name => values.GetValueOrDefault(name));
    }

    /// <summary>The same registrations the server makes, minus everything the factory cannot reach.</summary>
    private static ServiceProvider BuildContainer(BitbucketMcpOptions options)
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddSingleton(options);
        services.AddSingleton(TimeProvider.System);

        services.AddSingleton(sp => new TokenStore(
            options,
            sp.GetRequiredService<ILoggerFactory>(),
            sp.GetRequiredService<TimeProvider>()));

        services.AddSingleton(sp => new OAuthTokenClient(
            options,
            sp.GetRequiredService<ILoggerFactory>(),
            sp.GetRequiredService<TimeProvider>()));

        services.AddSingleton<IInteractiveAuthenticator>(NullInteractiveAuthenticator.Instance);

        return services.BuildServiceProvider();
    }
}
