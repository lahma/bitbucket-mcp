using System.Globalization;
using System.Text;

using Bitbucket.Mcp.Authentication;
using Bitbucket.Mcp.Configuration;


namespace Bitbucket.Mcp.Tests.Authentication;

/// <summary>
/// The token cache is the one piece of mutable state two processes share, and it holds a single-use
/// refresh token. These tests pin the four properties the rest of the auth subsystem relies on:
/// writes are atomic, a damaged file reads back as "signed out" instead of throwing, the
/// cross-process lock is genuinely exclusive, and the file is not readable by other users.
/// </summary>
public sealed class TokenStoreTests
{
    [Test]
    public void FilePathsComeFromTheConfiguredOverride()
    {
        using var temp = new TempTokenDirectory();

        var store = new TokenStore(
            new BitbucketMcpOptions { TokenFilePath = temp.TokenFilePath },
            AuthTestSupport.Loggers);

        Assert.That(store.FilePath, Is.EqualTo(temp.TokenFilePath));
        Assert.That(store.DirectoryPath, Is.EqualTo(temp.DirectoryPath));
        Assert.That(store.LockFilePath, Is.EqualTo(temp.LockFilePath));
        Assert.That(Path.GetFileName(store.LockFilePath), Is.EqualTo("tokens.lock"));
    }

    [Test]
    public void OverrideIsResolvedToAnAbsolutePathAndTrimmed()
    {
        var resolved = TokenStore.ResolveTokenFilePath("  relative-tokens.json  ");

        Assert.That(Path.IsPathFullyQualified(resolved), Is.True, $"'{resolved}' should be fully qualified.");
        Assert.That(Path.GetFileName(resolved), Is.EqualTo("relative-tokens.json"));
    }

    [Test]
    public void ConstructionTouchesNoDisk()
    {
        using var temp = new TempTokenDirectory();

        _ = new TokenStore(new BitbucketMcpOptions { TokenFilePath = temp.TokenFilePath }, AuthTestSupport.Loggers);

        Assert.That(Directory.Exists(temp.DirectoryPath), Is.False, "Constructing the store must not create its directory.");
    }

    [Test]
    public async Task MissingCacheLoadsAsNothing()
    {
        using var temp = new TempTokenDirectory();

        var store = AuthTestSupport.StoreFor(temp.TokenFilePath);

        Assert.That(await store.LoadAsync(TestContext.CurrentContext.CancellationToken), Is.Null);
    }

    [Test]
    public async Task SaveThenLoadRoundTripsEveryField()
    {
        using var temp = new TempTokenDirectory();

        var store = AuthTestSupport.StoreFor(temp.TokenFilePath);

        var saved = AuthTestSupport.TokenSetFor(
            "access-token-value",
            new DateTimeOffset(2026, 8, 8, 13, 30, 0, TimeSpan.Zero),
            refreshToken: "refresh-current",
            previousRefreshToken: "refresh-previous");

        await store.SaveAsync(saved, TestContext.CurrentContext.CancellationToken);

        var loaded = await store.LoadAsync(TestContext.CurrentContext.CancellationToken);

        Assert.That(loaded, Is.Not.Null);
        Assert.That(loaded.Version, Is.EqualTo(TokenSet.CurrentVersion));
        Assert.That(loaded.ConsumerKeyFingerprint, Is.EqualTo(saved.ConsumerKeyFingerprint));
        Assert.That(loaded.AccessToken, Is.EqualTo("access-token-value"));
        Assert.That(loaded.ExpiresAtUtc, Is.EqualTo(saved.ExpiresAtUtc));
        Assert.That(loaded.RefreshToken, Is.EqualTo("refresh-current"));
        Assert.That(loaded.PreviousRefreshToken, Is.EqualTo("refresh-previous"));
        Assert.That(loaded.Scopes, Is.EqualTo(saved.Scopes));
    }

    [Test]
    public async Task SaveStampsTheCurrentLayoutVersionEvenWhenTheCallerDidNot()
    {
        using var temp = new TempTokenDirectory();

        var store = AuthTestSupport.StoreFor(temp.TokenFilePath);

        await store.SaveAsync(
            AuthTestSupport.TokenSetFor("access", TestTimeProvider.DefaultNow) with { Version = 0 },
            TestContext.CurrentContext.CancellationToken);

        var loaded = await store.LoadAsync(TestContext.CurrentContext.CancellationToken);

        Assert.That(loaded, Is.Not.Null);
        Assert.That(loaded.Version, Is.EqualTo(TokenSet.CurrentVersion));
    }

    /// <summary>
    /// A plain envelope is what every non-Windows install writes, and what a cache copied from one
    /// must still read as. It is asserted by hand rather than through <c>SaveAsync</c> because on
    /// Windows that would produce a DPAPI envelope instead.
    /// </summary>
    [Test]
    public async Task PlainEnvelopeLoadsOnEveryPlatform()
    {
        using var temp = new TempTokenDirectory();
        temp.Create();

        const string TokenSetJson =
            """{"version":1,"consumerKeyFingerprint":"abc","accessToken":"plain-access","expiresAtUtc":"2026-08-08T13:00:00+00:00","refreshToken":"plain-refresh"}""";

        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(TokenSetJson));

        await File.WriteAllTextAsync(
            temp.TokenFilePath,
            $$"""{"scheme":"plain","payload":"{{payload}}"}""",
            TestContext.CurrentContext.CancellationToken);

        var loaded = await AuthTestSupport.StoreFor(temp.TokenFilePath).LoadAsync(TestContext.CurrentContext.CancellationToken);

        Assert.That(loaded, Is.Not.Null);
        Assert.That(loaded.AccessToken, Is.EqualTo("plain-access"));
        Assert.That(loaded.RefreshToken, Is.EqualTo("plain-refresh"));
        Assert.That(loaded.ConsumerKeyFingerprint, Is.EqualTo("abc"));
    }

    [Test]
    public async Task WindowsCacheIsDpapiProtectedAndHidesTheAccessToken()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Ignore("DPAPI exists on Windows only.");
            return;
        }

        using var temp = new TempTokenDirectory();

        const string AccessToken = "windows-secret-access-token";

        var store = AuthTestSupport.StoreFor(temp.TokenFilePath);

        await store.SaveAsync(
            AuthTestSupport.TokenSetFor(AccessToken, TestTimeProvider.DefaultNow, refreshToken: "windows-secret-refresh"),
            TestContext.CurrentContext.CancellationToken);

        var envelope = await AuthTestSupport.ReadEnvelopeAsync(temp.TokenFilePath, TestContext.CurrentContext.CancellationToken);

        Assert.That(envelope.Scheme, Is.EqualTo("dpapi"));

        var fileText = await File.ReadAllTextAsync(temp.TokenFilePath, TestContext.CurrentContext.CancellationToken);
        var payload = Encoding.UTF8.GetString(Convert.FromBase64String(envelope.Payload));

        Assert.That(fileText, Does.Not.Contain(AccessToken));
        Assert.That(payload, Does.Not.Contain(AccessToken));
        Assert.That(payload, Does.Not.Contain("windows-secret-refresh"));

        var loaded = await store.LoadAsync(TestContext.CurrentContext.CancellationToken);

        Assert.That(loaded, Is.Not.Null);
        Assert.That(loaded.AccessToken, Is.EqualTo(AccessToken));
    }

    /// <summary>
    /// The atomic write is a temp file plus a rename. If either half leaked, a rotation storm would
    /// litter the directory — and a leftover <c>.tmp</c> would mean a crash could leave the real
    /// file half written.
    /// </summary>
    [Test]
    public async Task RepeatedSavesLeaveExactlyOneFileAndNoTemporaries()
    {
        using var temp = new TempTokenDirectory();

        var store = AuthTestSupport.StoreFor(temp.TokenFilePath);

        for (var i = 0; i < 5; i++)
        {
            await store.SaveAsync(
                AuthTestSupport.TokenSetFor(
                    $"access-{i.ToString(CultureInfo.InvariantCulture)}",
                    TestTimeProvider.DefaultNow),
                TestContext.CurrentContext.CancellationToken);
        }

        Assert.That(Expect.Single(Directory.GetFiles(temp.DirectoryPath)), Is.EqualTo(temp.TokenFilePath));
        Assert.That(Directory.GetFiles(temp.DirectoryPath, "*.tmp"), Is.Empty);

        var loaded = await store.LoadAsync(TestContext.CurrentContext.CancellationToken);

        Assert.That(loaded, Is.Not.Null);
        Assert.That(loaded.AccessToken, Is.EqualTo("access-4"));
    }

    /// <summary>
    /// Every one of these is a real thing that happens to a token file: a truncated write, a
    /// hand-edit, a cache from another user's Windows account, a file from a future layout version.
    /// None of them may throw — the recovery is a sign-in, and a throw here would turn a
    /// recoverable state into a dead server.
    /// </summary>
    [TestCase("", "an empty file")]
    [TestCase("   ", "whitespace only")]
    [TestCase("this is not json", "not JSON at all")]
    [TestCase("{\"scheme\":\"plain\",\"payload\":", "truncated JSON")]
    [TestCase("""{"payload":"e30="}""", "no scheme")]
    [TestCase("""{"scheme":"plain"}""", "no payload")]
    [TestCase("""{"scheme":"plain","payload":""}""", "an empty payload")]
    [TestCase("""{"scheme":"plain","payload":"not-base-64!!"}""", "a payload that is not base64")]
    [TestCase("""{"scheme":"tomorrow","payload":"e30="}""", "an unknown protection scheme")]
    [TestCase("""{"scheme":"plain","payload":"bm90IGpzb24="}""", "a payload that is not JSON")]
    [TestCase("""{"scheme":"plain","payload":"e30="}""", "a payload from another layout version")]
    [TestCase("""{"scheme":"dpapi","payload":"e30="}""", "a DPAPI payload this account cannot decrypt")]
    public async Task DamagedCacheReadsBackAsSignedOut(string content, string description)
    {
        using var temp = new TempTokenDirectory();
        temp.Create();

        await File.WriteAllTextAsync(temp.TokenFilePath, content, TestContext.CurrentContext.CancellationToken);

        var loaded = await AuthTestSupport.StoreFor(temp.TokenFilePath).LoadAsync(TestContext.CurrentContext.CancellationToken);

        Assert.That(loaded is null, Is.True, $"A token cache with {description} must load as null.");
    }

    [Test]
    public async Task DeleteReportsWhetherItRemovedAnythingAndIsIdempotent()
    {
        using var temp = new TempTokenDirectory();

        var store = AuthTestSupport.StoreFor(temp.TokenFilePath);

        Assert.That(await store.DeleteAsync(TestContext.CurrentContext.CancellationToken), Is.False);

        await store.SaveAsync(
            AuthTestSupport.TokenSetFor("access", TestTimeProvider.DefaultNow),
            TestContext.CurrentContext.CancellationToken);

        Assert.That(await store.DeleteAsync(TestContext.CurrentContext.CancellationToken), Is.True);
        Assert.That(await store.DeleteAsync(TestContext.CurrentContext.CancellationToken), Is.False);
        Assert.That(File.Exists(temp.TokenFilePath), Is.False);
        Assert.That(await store.LoadAsync(TestContext.CurrentContext.CancellationToken), Is.Null);
    }

    /// <summary>
    /// Two holders at once would mean two processes spending the same single-use refresh token. The
    /// clock auto-advances so the ten-second timeout is reached in milliseconds rather than in
    /// wall-clock seconds.
    /// </summary>
    [Test]
    public async Task LockIsExclusiveUntilItIsReleased()
    {
        using var temp = new TempTokenDirectory();

        var clock = new TestTimeProvider { AutoAdvance = TimeSpan.FromSeconds(5) };
        var store = AuthTestSupport.StoreFor(temp.TokenFilePath, clock);

        var held = await store.AcquireLockAsync(TestContext.CurrentContext.CancellationToken);

        Assert.That(held, Is.Not.Null);

        // Contended: the second attempt must give up and report it rather than proceed silently.
        Assert.That(await store.AcquireLockAsync(TestContext.CurrentContext.CancellationToken), Is.Null);

        await held.DisposeAsync();

        var reacquired = await store.AcquireLockAsync(TestContext.CurrentContext.CancellationToken);

        Assert.That(reacquired, Is.Not.Null);

        await reacquired.DisposeAsync();
    }

    /// <summary>
    /// Releasing the lock must not delete the file: a waiter that already opened the doomed name
    /// and a newcomer that creates a fresh one would both believe they hold the lock.
    /// </summary>
    [Test]
    public async Task LockFileSurvivesRelease()
    {
        using var temp = new TempTokenDirectory();

        var store = AuthTestSupport.StoreFor(temp.TokenFilePath);

        var held = await store.AcquireLockAsync(TestContext.CurrentContext.CancellationToken);

        Assert.That(held, Is.Not.Null);
        Assert.That(File.Exists(temp.LockFilePath), Is.True);

        held.Dispose();

        Assert.That(File.Exists(temp.LockFilePath), Is.True, "The lock file is deliberately left behind on release.");
    }

    [Test]
    public async Task UnixPermissionsRestrictTheCacheToItsOwner()
    {
        if (OperatingSystem.IsWindows())
        {
            // Windows has no file modes; %LOCALAPPDATA%'s ACLs plus DPAPI stand in for them.
            Assert.Ignore("Unix file modes do not apply on Windows.");
            return;
        }

        using var temp = new TempTokenDirectory();

        var store = AuthTestSupport.StoreFor(temp.TokenFilePath);

        await store.SaveAsync(
            AuthTestSupport.TokenSetFor("access", TestTimeProvider.DefaultNow),
            TestContext.CurrentContext.CancellationToken);

        Assert.That(File.GetUnixFileMode(temp.TokenFilePath), Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite));

        Assert.That(File.GetUnixFileMode(temp.DirectoryPath), Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute));

        var held = await store.AcquireLockAsync(TestContext.CurrentContext.CancellationToken);

        Assert.That(held, Is.Not.Null);
        Assert.That(File.GetUnixFileMode(temp.LockFilePath), Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite));

        await held.DisposeAsync();
    }
}
