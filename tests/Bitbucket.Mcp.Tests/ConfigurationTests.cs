using Bitbucket.Mcp.Authentication;
using Bitbucket.Mcp.Configuration;


namespace Bitbucket.Mcp.Tests;

/// <summary>
/// The plugin-option layer: which of the two variables that can supply each credential actually
/// does, and what happens when the Claude Code plugin launcher passes a blank one.
/// </summary>
/// <remarks>
/// This is the regression suite for issue #4. The manifest maps every declared option into the
/// server's environment, and an option nobody filled in arrives as the <b>empty string</b> rather
/// than being omitted — so mapping it onto the plain name blanked a credential the user already
/// had. Reading the prefixed name first, with blank treated as absent, is what makes a blank prompt
/// mean "not configured" instead of "configured as nothing".
/// </remarks>
public sealed class ConfigurationTests
{
    private const string Prefix = BitbucketMcpOptions.PluginOptionPrefix;

    /// <summary>
    /// The bug itself: a blank plugin option must not shadow a real ambient value. Every variable
    /// the manifest maps is covered, because the manifest maps all six and any one of them
    /// regressing is the same defect.
    /// </summary>
    [TestCase("BITBUCKET_ACCESS_TOKEN")]
    [TestCase("BITBUCKET_EMAIL")]
    [TestCase("BITBUCKET_API_TOKEN")]
    [TestCase("BITBUCKET_OAUTH_KEY")]
    [TestCase("BITBUCKET_OAUTH_SECRET")]
    [TestCase("BITBUCKET_DEFAULT_WORKSPACE")]
    public void ABlankPluginOptionDoesNotShadowTheAmbientVariable(string name)
    {
        var options = Read((name, "ambient-value"), (Prefix + name, string.Empty));

        Assert.That(ValueOf(options, name), Is.EqualTo("ambient-value"));
    }

    /// <summary>Whitespace is blank too — the launcher trims nothing on its way through.</summary>
    [TestCase("BITBUCKET_ACCESS_TOKEN")]
    [TestCase("BITBUCKET_API_TOKEN")]
    public void AWhitespacePluginOptionIsAlsoTreatedAsAbsent(string name)
    {
        var options = Read((name, "ambient-value"), (Prefix + name, "   "));

        Assert.That(ValueOf(options, name), Is.EqualTo("ambient-value"));
    }

    /// <summary>A prompt the user did fill in still wins — that is the whole point of the option.</summary>
    [TestCase("BITBUCKET_ACCESS_TOKEN")]
    [TestCase("BITBUCKET_EMAIL")]
    [TestCase("BITBUCKET_API_TOKEN")]
    [TestCase("BITBUCKET_OAUTH_KEY")]
    [TestCase("BITBUCKET_OAUTH_SECRET")]
    [TestCase("BITBUCKET_DEFAULT_WORKSPACE")]
    public void AFilledPluginOptionWinsOverTheAmbientVariable(string name)
    {
        var options = Read((name, "ambient-value"), (Prefix + name, "from-the-prompt"));

        Assert.That(ValueOf(options, name), Is.EqualTo("from-the-prompt"));
    }

    [Test]
    public void ThePluginOptionAloneIsEnough()
    {
        var options = Read((Prefix + "BITBUCKET_ACCESS_TOKEN", "only-the-prompt"));

        Assert.That(options.AccessToken, Is.EqualTo("only-the-prompt"));
        Assert.That(options.AccessTokenVariable, Is.EqualTo(Prefix + "BITBUCKET_ACCESS_TOKEN"));
    }

    /// <summary>
    /// The source is reported so <c>status</c> can say which of the two won. Without it the report
    /// reads "set" either way, which is exactly what made the original clobber invisible.
    /// </summary>
    [Test]
    public void TheSourceVariableIsReported()
    {
        Assert.That(Read(("BITBUCKET_ACCESS_TOKEN", "t")).AccessTokenVariable, Is.EqualTo("BITBUCKET_ACCESS_TOKEN"));

        Assert.That(Read((Prefix + "BITBUCKET_ACCESS_TOKEN", "t")).AccessTokenVariable, Is.EqualTo(Prefix + "BITBUCKET_ACCESS_TOKEN"));

        Assert.That(Read().AccessTokenVariable, Is.Null);
    }

    /// <summary>
    /// The consequence the issue is actually about: blanking the top of the precedence chain must
    /// not demote the caller to the mechanism below it.
    /// </summary>
    [Test]
    public void ABlankPluginTokenDoesNotDemoteTheAuthMechanism()
    {
        var options = Read(
            ("BITBUCKET_ACCESS_TOKEN", "ambient-bearer"),
            ("BITBUCKET_EMAIL", "me@example.com"),
            ("BITBUCKET_API_TOKEN", "ambient-api-token"),
            (Prefix + "BITBUCKET_ACCESS_TOKEN", string.Empty),
            (Prefix + "BITBUCKET_EMAIL", string.Empty),
            (Prefix + "BITBUCKET_API_TOKEN", string.Empty));

        var provider = CredentialProviderFactory.CreateStatic(options);

        Assert.That(provider, Is.Not.Null);
        Assert.That(provider.Describe(), Does.Contain("Bearer"));
    }

    /// <summary>
    /// And the whole chain must not be emptied into a browser sign-in nobody asked for, which is
    /// what a plugin install with every prompt left blank used to do.
    /// </summary>
    [Test]
    public void AnAllBlankPluginInstallLeavesAnAmbientCredentialIntact()
    {
        var options = Read(
            ("BITBUCKET_EMAIL", "me@example.com"),
            ("BITBUCKET_API_TOKEN", "ambient-api-token"),
            (Prefix + "BITBUCKET_ACCESS_TOKEN", string.Empty),
            (Prefix + "BITBUCKET_EMAIL", string.Empty),
            (Prefix + "BITBUCKET_API_TOKEN", string.Empty),
            (Prefix + "BITBUCKET_OAUTH_KEY", string.Empty),
            (Prefix + "BITBUCKET_OAUTH_SECRET", string.Empty),
            (Prefix + "BITBUCKET_DEFAULT_WORKSPACE", string.Empty));

        var provider = CredentialProviderFactory.CreateStatic(options);

        Assert.That(provider, Is.Not.Null);
        Assert.That(provider.Describe(), Does.Contain("Basic"));
    }

    /// <summary>The credential names the variable it came from, so the report cannot mislead.</summary>
    [Test]
    public void TheCredentialDescriptionNamesThePluginVariableWhenThatIsWhatSuppliedIt()
    {
        var bearer = CredentialProviderFactory.CreateStatic(
            Read((Prefix + "BITBUCKET_ACCESS_TOKEN", "t")));

        Assert.That(bearer, Is.Not.Null);
        Assert.That(bearer.Describe(), Does.Contain(Prefix + "BITBUCKET_ACCESS_TOKEN"));

        var basic = CredentialProviderFactory.CreateStatic(Read(
            (Prefix + "BITBUCKET_EMAIL", "me@example.com"),
            ("BITBUCKET_API_TOKEN", "ambient")));

        Assert.That(basic, Is.Not.Null);

        // Mixed sources are the confusing case, so both are named individually.
        Assert.That(basic.Describe(), Does.Contain(Prefix + "BITBUCKET_EMAIL"));
        Assert.That(basic.Describe(), Does.Contain("+ BITBUCKET_API_TOKEN"));
    }

    /// <summary>
    /// No part of a credential ever reaches the description, whichever variable supplied it.
    /// </summary>
    [Test]
    public void TheDescriptionNeverContainsTheCredential()
    {
        var provider = CredentialProviderFactory.CreateStatic(
            Read((Prefix + "BITBUCKET_ACCESS_TOKEN", "super-secret-value")));

        Assert.That(provider, Is.Not.Null);
        Assert.That(provider.Describe(), Does.Not.Contain("super-secret-value"));
    }

    /// <summary>
    /// Variables the manifest does not declare are read from the environment alone. The prefix is
    /// the plugin launcher's half of a contract, not a general-purpose override anyone sets by hand.
    /// </summary>
    [Test]
    public void UnmappedVariablesAreNotReadThroughThePluginLayer()
    {
        var options = Read(
            (Prefix + "BITBUCKET_MCP_MAX_LOG_LINES", "7"),
            (Prefix + "BITBUCKET_OAUTH_CALLBACK_HOST", "::1"));

        Assert.That(options.MaxLogLines, Is.EqualTo(BitbucketMcpOptions.DefaultMaxLogLines));
        Assert.That(options.OAuthCallbackHost, Is.EqualTo(BitbucketMcpOptions.DefaultOAuthCallbackHost));
    }

    private static BitbucketMcpOptions Read(params (string Name, string? Value)[] variables)
    {
        var map = variables.ToDictionary(x => x.Name, x => x.Value, StringComparer.Ordinal);

        return BitbucketMcpOptions.FromEnvironment(name => map.GetValueOrDefault(name));
    }

    private static string? ValueOf(BitbucketMcpOptions options, string name) => name switch
    {
        "BITBUCKET_ACCESS_TOKEN" => options.AccessToken,
        "BITBUCKET_EMAIL" => options.Email,
        "BITBUCKET_API_TOKEN" => options.ApiToken,
        "BITBUCKET_OAUTH_KEY" => options.OAuthKey,
        "BITBUCKET_OAUTH_SECRET" => options.OAuthSecret,
        "BITBUCKET_DEFAULT_WORKSPACE" => options.DefaultWorkspace,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Not a manifest-mapped variable."),
    };
}
