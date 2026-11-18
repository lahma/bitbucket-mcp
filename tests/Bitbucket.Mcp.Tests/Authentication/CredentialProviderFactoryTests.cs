using System.Text;

using Bitbucket.Mcp.Authentication;
using Bitbucket.Mcp.Configuration;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;


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
    [TestCase("access-token", null, null, "Bearer")]
    [TestCase("access-token", "me@example.com", "api-token", "Bearer")]
    [TestCase(null, "me@example.com", "api-token", "Basic")]
    [TestCase(null, "me@example.com", null, null)]
    [TestCase(null, null, "api-token", null)]
    [TestCase(null, null, null, null)]
    [TestCase("   ", "   ", "   ", null)]
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
            Assert.That(provider, Is.Null);
            return;
        }

        Assert.That(provider, Is.Not.Null);

        var header = await provider.GetAuthenticationHeaderAsync(TestContext.CurrentContext.CancellationToken);

        Assert.That(header.Scheme, Is.EqualTo(expectedScheme));
    }

    [Test]
    public async Task AccessTokenBecomesABearerHeaderVerbatim()
    {
        var options = EnvironmentWith(("BITBUCKET_ACCESS_TOKEN", "  workspace-access-token  "));

        var provider = CredentialProviderFactory.CreateStatic(options);

        Assert.That(provider, Is.Not.Null);

        var header = await provider.GetAuthenticationHeaderAsync(TestContext.CurrentContext.CancellationToken);

        Assert.That(header.Scheme, Is.EqualTo("Bearer"));
        Assert.That(header.Parameter, Is.EqualTo("workspace-access-token"));
    }

    /// <summary>
    /// An Atlassian API token, not an app password — those were removed on 2026-07-28 and are
    /// deliberately not implemented — but the same wire shape.
    /// </summary>
    [Test]
    public async Task EmailAndApiTokenBecomeBasicCredentials()
    {
        var options = EnvironmentWith(
            ("BITBUCKET_EMAIL", "me@example.com"),
            ("BITBUCKET_API_TOKEN", "the-api-token"));

        var provider = CredentialProviderFactory.CreateStatic(options);

        Assert.That(provider, Is.Not.Null);

        var header = await provider.GetAuthenticationHeaderAsync(TestContext.CurrentContext.CancellationToken);

        Assert.That(header.Scheme, Is.EqualTo("Basic"));

        Assert.That(header.Parameter, Is.EqualTo(Convert.ToBase64String(Encoding.UTF8.GetBytes("me@example.com:the-api-token"))));
    }

    [Test]
    public void DescriptionsNameTheSourceAndNeverTheSecret()
    {
        var bearer = CredentialProviderFactory.CreateStatic(
            EnvironmentWith(("BITBUCKET_ACCESS_TOKEN", "super-secret-access-token")));

        Assert.That(bearer, Is.Not.Null);
        Assert.That(bearer.Describe(), Does.Contain("BITBUCKET_ACCESS_TOKEN"));
        Assert.That(bearer.Describe(), Does.Not.Contain("super-secret-access-token"));

        var basic = CredentialProviderFactory.CreateStatic(
            EnvironmentWith(
                ("BITBUCKET_EMAIL", "me@example.com"),
                ("BITBUCKET_API_TOKEN", "super-secret-api-token")));

        Assert.That(basic, Is.Not.Null);
        Assert.That(basic.Describe(), Does.Contain("BITBUCKET_EMAIL"));
        Assert.That(basic.Describe(), Does.Not.Contain("super-secret-api-token"));

        // The Basic credential is base64, not encryption: the encoded form must not leak either.
        Assert.That(basic.Describe(), Does.Not.Contain(Convert.ToBase64String(Encoding.UTF8.GetBytes("me@example.com:super-secret-api-token"))));
    }

    /// <summary>
    /// A static credential must not drag the OAuth graph into existence — the container here can
    /// resolve nothing else, so anything more than an options read would throw.
    /// </summary>
    [Test]
    public void StaticCredentialIsChosenWithoutResolvingTheOAuthGraph()
    {
        var services = new ServiceCollection();
        services.AddSingleton(EnvironmentWith(("BITBUCKET_ACCESS_TOKEN", "access-token")));

        using var container = services.BuildServiceProvider();

        var credential = CredentialProviderFactory.Create(container);

        Assert.That(credential, Is.TypeOf<StaticCredentialProvider>());
    }

    [Test]
    public void OAuthIsTheFallbackEvenWhenNoConsumerIsConfigured()
    {
        using var temp = new TempTokenDirectory();
        using var container = BuildContainer(new BitbucketMcpOptions { TokenFilePath = temp.TokenFilePath });

        var credential = CredentialProviderFactory.Create(container);

        try
        {
            Assert.That(credential, Is.TypeOf<OAuthCredentialProvider>());
            Assert.That(credential.Describe(), Does.Contain("not configured"));
            Assert.That(Directory.Exists(temp.DirectoryPath), Is.False, "Choosing a credential must not touch the disk.");
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
    [Test]
    public async Task UnconfiguredOAuthFailsOnFirstUseAndStillCreatesNoDirectory()
    {
        using var temp = new TempTokenDirectory();
        using var container = BuildContainer(new BitbucketMcpOptions { TokenFilePath = temp.TokenFilePath });

        var credential = CredentialProviderFactory.Create(container);

        try
        {
            var failure = await Expect.ThrowsAsync<AuthenticationRequiredException>(async () => await credential.GetAuthenticationHeaderAsync(TestContext.CurrentContext.CancellationToken));

            Assert.That(failure.Reason, Is.EqualTo(AuthenticationRequiredReason.NotConfigured));
            Assert.That(failure.Message, Does.Contain("BITBUCKET_ACCESS_TOKEN"));
            Assert.That(failure.Message, Does.Contain("BITBUCKET_OAUTH_KEY"));
            Assert.That(failure.Message, Does.Contain("bitbucket-mcp login"));

            Assert.That(Directory.Exists(temp.DirectoryPath), Is.False, "An unconfigured OAuth provider must fail before it creates anything.");
        }
        finally
        {
            (credential as IDisposable)?.Dispose();
        }
    }

    [Test]
    public async Task InvalidatingAStaticCredentialIsANoOp()
    {
        var options = EnvironmentWith(("BITBUCKET_ACCESS_TOKEN", "access-token"));

        var provider = CredentialProviderFactory.CreateStatic(options);

        Assert.That(provider, Is.Not.Null);

        await provider.InvalidateAsync(TestContext.CurrentContext.CancellationToken);

        var header = await provider.GetAuthenticationHeaderAsync(TestContext.CurrentContext.CancellationToken);

        // There is nothing to refresh: an environment token that is rejected is wrong, not stale.
        Assert.That(header.Parameter, Is.EqualTo("access-token"));
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
