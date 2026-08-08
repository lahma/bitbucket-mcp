using System.Globalization;
using System.Net;
using System.Net.Sockets;

using Bitbucket.Mcp.Authentication;
using Bitbucket.Mcp.Configuration;

using Microsoft.Extensions.Logging;

using Xunit;

namespace Bitbucket.Mcp.Tests.Authentication;

/// <summary>
/// The browser half of the OAuth flow, driven end to end without a desktop session: the
/// <c>openBrowser</c> seam is replaced by a fake that reads the authorization URL and issues the
/// redirect itself, exactly as a browser would.
/// </summary>
public sealed class InteractiveAuthenticatorTests
{
    [Fact]
    public async Task AuthorizeReturnsTheCodeAndTheRedirectUriItWasIssuedAgainst()
    {
        var options = OptionsWithScratchPort();
        var browser = new FakeBrowser(state => $"code=browser-code&state={state}", TestContext.Current.CancellationToken);

        var authenticator = new InteractiveAuthenticator(options, AuthTestSupport.Loggers, browser.Open);

        var result = await authenticator.AuthorizeAsync(AuthTestSupport.ConsumerKey, TestContext.Current.CancellationToken);

        Assert.Equal("browser-code", result.Code);
        Assert.Equal(InteractiveAuthenticator.BuildRedirectUri(options), result.RedirectUri);

        Assert.Equal(
            $"http://127.0.0.1:{options.OAuthCallbackPort.ToString(CultureInfo.InvariantCulture)}/callback",
            result.RedirectUri);

        await browser.Callback;
    }

    /// <summary>
    /// The authorization URL is the one thing the user may have to open by hand, and every
    /// parameter in it is load bearing — not least <c>state</c>, which is what ties the callback we
    /// accept to the request we made.
    /// </summary>
    [Fact]
    public async Task AuthorizeUrlCarriesTheClientIdResponseTypeStateAndEscapedRedirectUri()
    {
        var options = OptionsWithScratchPort();
        var browser = new FakeBrowser(state => $"code=browser-code&state={state}", TestContext.Current.CancellationToken);

        var authenticator = new InteractiveAuthenticator(options, AuthTestSupport.Loggers, browser.Open);

        _ = await authenticator.AuthorizeAsync(AuthTestSupport.ConsumerKey, TestContext.Current.CancellationToken);

        var url = browser.OpenedUrl;

        Assert.NotNull(url);
        Assert.StartsWith("https://bitbucket.org/site/oauth2/authorize?", url, StringComparison.Ordinal);
        Assert.Equal(AuthTestSupport.ConsumerKey, AuthTestSupport.QueryValue(url, "client_id"));
        Assert.Equal("code", AuthTestSupport.QueryValue(url, "response_type"));
        Assert.Equal(InteractiveAuthenticator.BuildRedirectUri(options), AuthTestSupport.QueryValue(url, "redirect_uri"));

        // Escaped, not interpolated raw: an unescaped "://" would end the parameter early.
        Assert.Contains(Uri.EscapeDataString(InteractiveAuthenticator.BuildRedirectUri(options)), url, StringComparison.Ordinal);

        // 128 bits of CSPRNG entropy, base64url encoded.
        Assert.Equal(22, AuthTestSupport.QueryValue(url, "state").Length);

        await browser.Callback;
    }

    [Fact]
    public async Task EachSignInGetsItsOwnStateValue()
    {
        var states = new List<string>();

        for (var i = 0; i < 2; i++)
        {
            var options = OptionsWithScratchPort();
            var browser = new FakeBrowser(state => $"code=browser-code&state={state}", TestContext.Current.CancellationToken);
            var authenticator = new InteractiveAuthenticator(options, AuthTestSupport.Loggers, browser.Open);

            _ = await authenticator.AuthorizeAsync(AuthTestSupport.ConsumerKey, TestContext.Current.CancellationToken);

            states.Add(AuthTestSupport.QueryValue(browser.OpenedUrl!, "state"));

            await browser.Callback;
        }

        Assert.Distinct(states);
    }

    [Fact]
    public async Task ErrorRedirectBecomesAnInteractiveFailureNamingTheReason()
    {
        var options = OptionsWithScratchPort();

        var browser = new FakeBrowser(
            state => $"error=access_denied&error_description=The+user+refused&state={state}",
            TestContext.Current.CancellationToken);

        var authenticator = new InteractiveAuthenticator(options, AuthTestSupport.Loggers, browser.Open);

        var failure = await Assert.ThrowsAsync<AuthenticationRequiredException>(
            async () => await authenticator.AuthorizeAsync(AuthTestSupport.ConsumerKey, TestContext.Current.CancellationToken));

        Assert.Equal(AuthenticationRequiredReason.InteractiveFailed, failure.Reason);
        Assert.Contains("access_denied", failure.Message, StringComparison.Ordinal);
        Assert.Contains("The user refused", failure.Message, StringComparison.Ordinal);
        Assert.NotNull(failure.AuthorizeUrl);

        await browser.Callback;
    }

    /// <summary>
    /// A machine with no way to launch a browser is not a failure: the URL is logged and the
    /// listener stays up, so the user can paste it into a browser somewhere else.
    /// </summary>
    [Fact]
    public async Task BrowserThatWillNotOpenStillLeavesTheListenerWaiting()
    {
        var options = OptionsWithScratchPort();

        var browser = new FakeBrowser(
            state => $"code=pasted-by-hand&state={state}",
            TestContext.Current.CancellationToken,
            opened: false);

        var authenticator = new InteractiveAuthenticator(options, AuthTestSupport.Loggers, browser.Open);

        var result = await authenticator.AuthorizeAsync(AuthTestSupport.ConsumerKey, TestContext.Current.CancellationToken);

        Assert.Equal("pasted-by-hand", result.Code);

        await browser.Callback;
    }

    [Fact]
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

            var failure = await Assert.ThrowsAsync<AuthenticationRequiredException>(
                async () => await authenticator.AuthorizeAsync(AuthTestSupport.ConsumerKey, TestContext.Current.CancellationToken));

            Assert.Equal(AuthenticationRequiredReason.InteractiveFailed, failure.Reason);
            Assert.Contains("BITBUCKET_OAUTH_CALLBACK_PORT", failure.Message, StringComparison.Ordinal);
            Assert.NotNull(failure.AuthorizeUrl);
        }
        finally
        {
            squatter.Dispose();
        }
    }

    [Fact]
    public async Task NoBrowserModeRefusesToStartTheFlowAtAll()
    {
        var options = OptionsWithScratchPort() with { NoBrowser = true };

        var authenticator = new InteractiveAuthenticator(
            options,
            AuthTestSupport.Loggers,
            (_, _) => throw new InvalidOperationException("No browser may be opened."));

        var failure = await Assert.ThrowsAsync<AuthenticationRequiredException>(
            async () => await authenticator.AuthorizeAsync(AuthTestSupport.ConsumerKey, TestContext.Current.CancellationToken));

        Assert.Equal(AuthenticationRequiredReason.BrowserUnavailable, failure.Reason);
        Assert.Contains("BITBUCKET_MCP_NO_BROWSER", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Availability is answered from configuration alone — no port probe, no display check, no
    /// search for a browser binary — because it is consulted on the error path and by <c>status</c>.
    /// </summary>
    [Theory]
    [InlineData(false, "key", "secret", true)]
    [InlineData(true, "key", "secret", false)]
    [InlineData(false, null, "secret", false)]
    [InlineData(false, "key", null, false)]
    [InlineData(false, null, null, false)]
    [InlineData(true, null, null, false)]
    public void AvailabilityIsDecidedByConfigurationAlone(bool noBrowser, string? key, string? secret, bool expected)
    {
        var authenticator = new InteractiveAuthenticator(
            new BitbucketMcpOptions { NoBrowser = noBrowser, OAuthKey = key, OAuthSecret = secret },
            AuthTestSupport.Loggers,
            (_, _) => throw new InvalidOperationException("IsAvailable must not open anything."));

        Assert.Equal(expected, authenticator.IsAvailable);
    }

    [Fact]
    public void RedirectUriMatchesTheDocumentedDefaultAndBracketsIpv6Hosts()
    {
        Assert.Equal(
            "http://127.0.0.1:33418/callback",
            InteractiveAuthenticator.BuildRedirectUri(new BitbucketMcpOptions()));

        Assert.Equal(
            "http://localhost:33418/callback",
            InteractiveAuthenticator.BuildRedirectUri(new BitbucketMcpOptions { OAuthCallbackHost = "localhost" }));

        Assert.Equal(
            "http://[::1]:33418/callback",
            InteractiveAuthenticator.BuildRedirectUri(new BitbucketMcpOptions { OAuthCallbackHost = "::1" }));
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
