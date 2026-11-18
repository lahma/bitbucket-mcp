using System.Globalization;
using System.Net;
using System.Net.Sockets;

using Bitbucket.Mcp.Authentication;
using Bitbucket.Mcp.Configuration;

using Microsoft.Extensions.Logging;


namespace Bitbucket.Mcp.Tests.Authentication;

/// <summary>
/// The browser half of the OAuth flow, driven end to end without a desktop session: the
/// <c>openBrowser</c> seam is replaced by a fake that reads the authorization URL and issues the
/// redirect itself, exactly as a browser would.
/// </summary>
public sealed class InteractiveAuthenticatorTests
{
    [Test]
    public async Task AuthorizeReturnsTheCodeAndTheRedirectUriItWasIssuedAgainst()
    {
        var options = OptionsWithScratchPort();
        var browser = new FakeBrowser(state => $"code=browser-code&state={state}", TestContext.CurrentContext.CancellationToken);

        var authenticator = new InteractiveAuthenticator(options, AuthTestSupport.Loggers, browser.Open);

        var result = await authenticator.AuthorizeAsync(AuthTestSupport.ConsumerKey, TestContext.CurrentContext.CancellationToken);

        Assert.That(result.Code, Is.EqualTo("browser-code"));
        Assert.That(result.RedirectUri, Is.EqualTo(InteractiveAuthenticator.BuildRedirectUri(options)));

        Assert.That(result.RedirectUri, Is.EqualTo($"http://127.0.0.1:{options.OAuthCallbackPort.ToString(CultureInfo.InvariantCulture)}/callback"));

        await browser.Callback;
    }

    /// <summary>
    /// The authorization URL is the one thing the user may have to open by hand, and every
    /// parameter in it is load bearing — not least <c>state</c>, which is what ties the callback we
    /// accept to the request we made.
    /// </summary>
    [Test]
    public async Task AuthorizeUrlCarriesTheClientIdResponseTypeStateAndEscapedRedirectUri()
    {
        var options = OptionsWithScratchPort();
        var browser = new FakeBrowser(state => $"code=browser-code&state={state}", TestContext.CurrentContext.CancellationToken);

        var authenticator = new InteractiveAuthenticator(options, AuthTestSupport.Loggers, browser.Open);

        _ = await authenticator.AuthorizeAsync(AuthTestSupport.ConsumerKey, TestContext.CurrentContext.CancellationToken);

        var url = browser.OpenedUrl;

        Assert.That(url, Is.Not.Null);
        Assert.That(url, Does.StartWith("https://bitbucket.org/site/oauth2/authorize?"));
        Assert.That(AuthTestSupport.QueryValue(url, "client_id"), Is.EqualTo(AuthTestSupport.ConsumerKey));
        Assert.That(AuthTestSupport.QueryValue(url, "response_type"), Is.EqualTo("code"));
        Assert.That(AuthTestSupport.QueryValue(url, "redirect_uri"), Is.EqualTo(InteractiveAuthenticator.BuildRedirectUri(options)));

        // Escaped, not interpolated raw: an unescaped "://" would end the parameter early.
        Assert.That(url, Does.Contain(Uri.EscapeDataString(InteractiveAuthenticator.BuildRedirectUri(options))));

        // 128 bits of CSPRNG entropy, base64url encoded.
        Assert.That(AuthTestSupport.QueryValue(url, "state").Length, Is.EqualTo(22));

        await browser.Callback;
    }

    [Test]
    public async Task EachSignInGetsItsOwnStateValue()
    {
        var states = new List<string>();

        for (var i = 0; i < 2; i++)
        {
            var options = OptionsWithScratchPort();
            var browser = new FakeBrowser(state => $"code=browser-code&state={state}", TestContext.CurrentContext.CancellationToken);
            var authenticator = new InteractiveAuthenticator(options, AuthTestSupport.Loggers, browser.Open);

            _ = await authenticator.AuthorizeAsync(AuthTestSupport.ConsumerKey, TestContext.CurrentContext.CancellationToken);

            states.Add(AuthTestSupport.QueryValue(browser.OpenedUrl!, "state"));

            await browser.Callback;
        }

        Assert.That(states, Is.Unique);
    }

    [Test]
    public async Task ErrorRedirectBecomesAnInteractiveFailureNamingTheReason()
    {
        var options = OptionsWithScratchPort();

        var browser = new FakeBrowser(
            state => $"error=access_denied&error_description=The+user+refused&state={state}",
            TestContext.CurrentContext.CancellationToken);

        var authenticator = new InteractiveAuthenticator(options, AuthTestSupport.Loggers, browser.Open);

        var failure = await Expect.ThrowsAsync<AuthenticationRequiredException>(async () => await authenticator.AuthorizeAsync(AuthTestSupport.ConsumerKey, TestContext.CurrentContext.CancellationToken));

        Assert.That(failure.Reason, Is.EqualTo(AuthenticationRequiredReason.InteractiveFailed));
        Assert.That(failure.Message, Does.Contain("access_denied"));
        Assert.That(failure.Message, Does.Contain("The user refused"));
        Assert.That(failure.AuthorizeUrl, Is.Not.Null);

        await browser.Callback;
    }

    /// <summary>
    /// A machine with no way to launch a browser is not a failure: the URL is logged and the
    /// listener stays up, so the user can paste it into a browser somewhere else.
    /// </summary>
    [Test]
    public async Task BrowserThatWillNotOpenStillLeavesTheListenerWaiting()
    {
        var options = OptionsWithScratchPort();

        var browser = new FakeBrowser(
            state => $"code=pasted-by-hand&state={state}",
            TestContext.CurrentContext.CancellationToken,
            opened: false);

        var authenticator = new InteractiveAuthenticator(options, AuthTestSupport.Loggers, browser.Open);

        var result = await authenticator.AuthorizeAsync(AuthTestSupport.ConsumerKey, TestContext.CurrentContext.CancellationToken);

        Assert.That(result.Code, Is.EqualTo("pasted-by-hand"));

        await browser.Callback;
    }

    [Test]
    public async Task PortAlreadyInUseIsReportedWithTheAuthorizeUrlAndTheVariableToChange()
    {
        var options = OptionsWithScratchPort();

        var squatter = new TcpListener(IPAddress.Loopback, options.OAuthCallbackPort);
        squatter.Start();

        try
        {
            var authenticator = new InteractiveAuthenticator(
                options,
                AuthTestSupport.Loggers,
                (_, _) => throw new InvalidOperationException("The browser must not be opened when the port is taken."));

            var failure = await Expect.ThrowsAsync<AuthenticationRequiredException>(async () => await authenticator.AuthorizeAsync(AuthTestSupport.ConsumerKey, TestContext.CurrentContext.CancellationToken));

            Assert.That(failure.Reason, Is.EqualTo(AuthenticationRequiredReason.InteractiveFailed));
            Assert.That(failure.Message, Does.Contain("BITBUCKET_OAUTH_CALLBACK_PORT"));
            Assert.That(failure.AuthorizeUrl, Is.Not.Null);
        }
        finally
        {
            squatter.Dispose();
        }
    }

    [Test]
    public async Task NoBrowserModeRefusesToStartTheFlowAtAll()
    {
        var options = OptionsWithScratchPort() with { NoBrowser = true };

        var authenticator = new InteractiveAuthenticator(
            options,
            AuthTestSupport.Loggers,
            (_, _) => throw new InvalidOperationException("No browser may be opened."));

        var failure = await Expect.ThrowsAsync<AuthenticationRequiredException>(async () => await authenticator.AuthorizeAsync(AuthTestSupport.ConsumerKey, TestContext.CurrentContext.CancellationToken));

        Assert.That(failure.Reason, Is.EqualTo(AuthenticationRequiredReason.BrowserUnavailable));
        Assert.That(failure.Message, Does.Contain("BITBUCKET_MCP_NO_BROWSER"));
    }

    /// <summary>
    /// Availability is answered from configuration alone — no port probe, no display check, no
    /// search for a browser binary — because it is consulted on the error path and by <c>status</c>.
    /// </summary>
    [TestCase(false, "key", "secret", true)]
    [TestCase(true, "key", "secret", false)]
    [TestCase(false, null, "secret", false)]
    [TestCase(false, "key", null, false)]
    [TestCase(false, null, null, false)]
    [TestCase(true, null, null, false)]
    public void AvailabilityIsDecidedByConfigurationAlone(bool noBrowser, string? key, string? secret, bool expected)
    {
        var authenticator = new InteractiveAuthenticator(
            new BitbucketMcpOptions { NoBrowser = noBrowser, OAuthKey = key, OAuthSecret = secret },
            AuthTestSupport.Loggers,
            (_, _) => throw new InvalidOperationException("IsAvailable must not open anything."));

        Assert.That(authenticator.IsAvailable, Is.EqualTo(expected));
    }

    [Test]
    public void RedirectUriMatchesTheDocumentedDefaultAndBracketsIpv6Hosts()
    {
        Assert.That(InteractiveAuthenticator.BuildRedirectUri(new BitbucketMcpOptions()), Is.EqualTo("http://127.0.0.1:33418/callback"));

        Assert.That(InteractiveAuthenticator.BuildRedirectUri(new BitbucketMcpOptions { OAuthCallbackHost = "localhost" }), Is.EqualTo("http://localhost:33418/callback"));

        Assert.That(InteractiveAuthenticator.BuildRedirectUri(new BitbucketMcpOptions { OAuthCallbackHost = "::1" }), Is.EqualTo("http://[::1]:33418/callback"));
    }

    private static BitbucketMcpOptions OptionsWithScratchPort() => new()
    {
        OAuthKey = AuthTestSupport.ConsumerKey,
        OAuthSecret = AuthTestSupport.ConsumerSecret,
        OAuthCallbackPort = AuthTestSupport.FreeScratchPort(),
    };

    /// <summary>
    /// Stands in for the browser: records the authorization URL it was handed and then issues the
    /// redirect to the loopback listener, on a background task so that the flow can reach its wait.
    /// </summary>
    private sealed class FakeBrowser
    {
        private readonly Func<string, string> _callbackQuery;
        private readonly CancellationToken _cancellationToken;
        private readonly bool _opened;

        internal FakeBrowser(Func<string, string> callbackQuery, CancellationToken cancellationToken, bool opened = true)
        {
            _callbackQuery = callbackQuery;
            _cancellationToken = cancellationToken;
            _opened = opened;
        }

        /// <summary>The URL the authenticator asked to open.</summary>
        internal string? OpenedUrl { get; private set; }

        /// <summary>The redirect the fake browser issued, to be awaited before the test ends.</summary>
        internal Task Callback { get; private set; } = Task.CompletedTask;

        internal bool Open(string url, ILogger logger)
        {
            ArgumentNullException.ThrowIfNull(logger);

            OpenedUrl = url;

            var redirectUri = AuthTestSupport.QueryValue(url, "redirect_uri");
            var query = _callbackQuery(AuthTestSupport.QueryValue(url, "state"));
            var cancellationToken = _cancellationToken;

            Callback = Task.Run(
                async () => await AuthTestSupport.GetAsync($"{redirectUri}?{query}", cancellationToken),
                cancellationToken);

            return _opened;
        }
    }
}
