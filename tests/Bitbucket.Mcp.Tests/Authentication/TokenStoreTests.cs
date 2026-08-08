using System.Globalization;
using System.Text;

using Bitbucket.Mcp.Authentication;
using Bitbucket.Mcp.Configuration;

using Xunit;

namespace Bitbucket.Mcp.Tests.Authentication;

/// <summary>
/// The token cache is the one piece of mutable state two processes share, and it holds a single-use
/// refresh token. These tests pin the four properties the rest of the auth subsystem relies on:
/// writes are atomic, a damaged file reads back as "signed out" instead of throwing, the
/// cross-process lock is genuinely exclusive, and the file is not readable by other users.
/// </summary>
public sealed class TokenStoreTests
{
    [Fact]
    public void FilePathsComeFromTheConfiguredOverride()
    {
        using var temp = new TempTokenDirectory();

        var store = new TokenStore(
            new BitbucketMcpOptions { TokenFilePath = temp.TokenFilePath },
            AuthTestSupport.Loggers);

        Assert.Equal(temp.TokenFilePath, store.FilePath);
        Assert.Equal(temp.DirectoryPath, store.DirectoryPath);
        Assert.Equal(temp.LockFilePath, store.LockFilePath);
        Assert.Equal("tokens.lock", Path.GetFileName(store.LockFilePath));
    }

    [Fact]
    public void OverrideIsResolvedToAnAbsolutePathAndTrimmed()
    {
        var resolved = TokenStore.ResolveTokenFilePath("  relative-tokens.json  ");

        Assert.True(Path.IsPathFullyQualified(resolved), $"'{resolved}' should be fully qualified.");
        Assert.Equal("relative-tokens.json", Path.GetFileName(resolved));
    }

    [Fact]
    public void ConstructionTouchesNoDisk()
    {
        using var temp = new TempTokenDirectory();

        _ = new TokenStore(new BitbucketMcpOptions { TokenFilePath = temp.TokenFilePath }, AuthTestSupport.Loggers);

        Assert.False(Directory.Exists(temp.DirectoryPath), "Constructing the store must not create its directory.");
    }

    [Fact]
    public async Task MissingCacheLoadsAsNothing()
    {
        using var temp = new TempTokenDirectory();

        var store = AuthTestSupport.StoreFor(temp.TokenFilePath);

        Assert.Null(await store.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SaveThenLoadRoundTripsEveryField()
    {
        using var temp = new TempTokenDirectory();

        var store = AuthTestSupport.StoreFor(temp.TokenFilePath);

        var saved = AuthTestSupport.TokenSetFor(
            "access-token-value",
            new DateTimeOffset(2026, 8, 8, 13, 30, 0, TimeSpan.Zero),
            refreshToken: "refresh-current",
            previousRefreshToken: "refresh-previous");

        await store.SaveAsync(saved, TestContext.Current.CancellationToken);

        var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(loaded);
        Assert.Equal(TokenSet.CurrentVersion, loaded.Version);
        Assert.Equal(saved.ConsumerKeyFingerprint, loaded.ConsumerKeyFingerprint);
        Assert.Equal("access-token-value", loaded.AccessToken);
        Assert.Equal(saved.ExpiresAtUtc, loaded.ExpiresAtUtc);
        Assert.Equal("refresh-current", loaded.RefreshToken);
        Assert.Equal("refresh-previous", loaded.PreviousRefreshToken);
        Assert.Equal(saved.Scopes, loaded.Scopes);
    }

    [Fact]
    public async Task SaveStampsTheCurrentLayoutVersionEvenWhenTheCallerDidNot()
    {
        using var temp = new TempTokenDirectory();

        var store = AuthTestSupport.StoreFor(temp.TokenFilePath);

        await store.SaveAsync(
            AuthTestSupport.TokenSetFor("access", TestTimeProvider.DefaultNow) with { Version = 0 },
            TestContext.Current.CancellationToken);

        var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(loaded);
        Assert.Equal(TokenSet.CurrentVersion, loaded.Version);
    }

    /// <summary>
    /// A plain envelope is what every non-Windows install writes, and what a cache copied from one
    /// must still read as. It is asserted by hand rather than through <c>SaveAsync</c> because on
    /// Windows that would produce a DPAPI envelope instead.
    /// </summary>
    [Fact]
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
            TestContext.Current.CancellationToken);

        var loaded = await AuthTestSupport.StoreFor(temp.TokenFilePath).LoadAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(loaded);
        Assert.Equal("plain-access", loaded.AccessToken);
        Assert.Equal("plain-refresh", loaded.RefreshToken);
        Assert.Equal("abc", loaded.ConsumerKeyFingerprint);
    }

    [Fact]
    public async Task WindowsCacheIsDpapiProtectedAndHidesTheAccessToken()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DPAPI exists on Windows only.");

        using var temp = new TempTokenDirectory();

        const string AccessToken = "windows-secret-access-token";

        var store = AuthTestSupport.StoreFor(temp.TokenFilePath);

        await store.SaveAsync(
            AuthTestSupport.TokenSetFor(AccessToken, TestTimeProvider.DefaultNow, refreshToken: "windows-secret-refresh"),
            TestContext.Current.CancellationToken);

        var envelope = await AuthTestSupport.ReadEnvelopeAsync(temp.TokenFilePath, TestContext.Current.CancellationToken);

        Assert.Equal("dpapi", envelope.Scheme);

        var fileText = await File.ReadAllTextAsync(temp.TokenFilePath, TestContext.Current.CancellationToken);
        var payload = Encoding.UTF8.GetString(Convert.FromBase64String(envelope.Payload));

        Assert.DoesNotContain(AccessToken, fileText, StringComparison.Ordinal);
        Assert.DoesNotContain(AccessToken, payload, StringComparison.Ordinal);
        Assert.DoesNotContain("windows-secret-refresh", payload, StringComparison.Ordinal);

        var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(loaded);
        Assert.Equal(AccessToken, loaded.AccessToken);
    }

    /// <summary>
    /// The atomic write is a temp file plus a rename. If either half leaked, a rotation storm would
    /// litter the directory — and a leftover <c>.tmp</c> would mean a crash could leave the real
    /// file half written.
    /// </summary>
    [Fact]
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
                TestContext.Current.CancellationToken);
        }

        Assert.Equal(temp.TokenFilePath, Assert.Single(Directory.GetFiles(temp.DirectoryPath)));
        Assert.Empty(Directory.GetFiles(temp.DirectoryPath, "*.tmp"));

        var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(loaded);
        Assert.Equal("access-4", loaded.AccessToken);
    }

    /// <summary>
    /// Every one of these is a real thing that happens to a token file: a truncated write, a
    /// hand-edit, a cache from another user's Windows account, a file from a future layout version.
    /// None of them may throw — the recovery is a sign-in, and a throw here would turn a
    /// recoverable state into a dead server.
    /// </summary>
    [Theory]
    [InlineData("", "an empty file")]
    [InlineData("   ", "whitespace only")]
    [InlineData("this is not json", "not JSON at all")]
    [InlineData("{\"scheme\":\"plain\",\"payload\":", "truncated JSON")]
    [InlineData("""{"payload":"e30="}""", "no scheme")]
    [InlineData("""{"scheme":"plain"}""", "no payload")]
    [InlineData("""{"scheme":"plain","payload":""}""", "an empty payload")]
    [InlineData("""{"scheme":"plain","payload":"not-base-64!!"}""", "a payload that is not base64")]
    [InlineData("""{"scheme":"tomorrow","payload":"e30="}""", "an unknown protection scheme")]
    [InlineData("""{"scheme":"plain","payload":"bm90IGpzb24="}""", "a payload that is not JSON")]
    [InlineData("""{"scheme":"plain","payload":"e30="}""", "a payload from another layout version")]
    [InlineData("""{"scheme":"dpapi","payload":"e30="}""", "a DPAPI payload this account cannot decrypt")]
    public async Task DamagedCacheReadsBackAsSignedOut(string content, string description)
    {
        using var temp = new TempTokenDirectory();
        temp.Create();

        await File.WriteAllTextAsync(temp.TokenFilePath, content, TestContext.Current.CancellationToken);

        var loaded = await AuthTestSupport.StoreFor(temp.TokenFilePath).LoadAsync(TestContext.Current.CancellationToken);

        Assert.True(loaded is null, $"A token cache with {description} must load as null.");
    }

    [Fact]
    public async Task DeleteReportsWhetherItRemovedAnythingAndIsIdempotent()
    {
        using var temp = new TempTokenDirectory();

        var store = AuthTestSupport.StoreFor(temp.TokenFilePath);

        Assert.False(await store.DeleteAsync(TestContext.Current.CancellationToken));

        await store.SaveAsync(
            AuthTestSupport.TokenSetFor("access", TestTimeProvider.DefaultNow),
            TestContext.Current.CancellationToken);

        Assert.True(await store.DeleteAsync(TestContext.Current.CancellationToken));
        Assert.False(await store.DeleteAsync(TestContext.Current.CancellationToken));
        Assert.False(File.Exists(temp.TokenFilePath));
        Assert.Null(await store.LoadAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Two holders at once would mean two processes spending the same single-use refresh token. The
    /// clock auto-advances so the ten-second timeout is reached in milliseconds rather than in
    /// wall-clock seconds.
    /// </summary>
    [Fact]
    public async Task LockIsExclusiveUntilItIsReleased()
    {
        using var temp = new TempTokenDirectory();

        var clock = new TestTimeProvider { AutoAdvance = TimeSpan.FromSeconds(5) };
        var store = AuthTestSupport.StoreFor(temp.TokenFilePath, clock);

        var held = await store.AcquireLockAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(held);

        // Contended: the second attempt must give up and report it rather than proceed silently.
        Assert.Null(await store.AcquireLockAsync(TestContext.Current.CancellationToken));

        await held.DisposeAsync();

        var reacquired = await store.AcquireLockAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(reacquired);

        await reacquired.DisposeAsync();
    }

    /// <summary>
    /// Releasing the lock must not delete the file: a waiter that already opened the doomed name
    /// and a newcomer that creates a fresh one would both believe they hold the lock.
    /// </summary>
    [Fact]
    public async Task LockFileSurvivesRelease()
    {
        using var temp = new TempTokenDirectory();

        var store = AuthTestSupport.StoreFor(temp.TokenFilePath);

        var held = await store.AcquireLockAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(held);
        Assert.True(File.Exists(temp.LockFilePath));

        held.Dispose();

        Assert.True(File.Exists(temp.LockFilePath), "The lock file is deliberately left behind on release.");
    }

    [Fact]
    public async Task UnixPermissionsRestrictTheCacheToItsOwner()
    {
        if (OperatingSystem.IsWindows())
        {
            // Windows has no file modes; %LOCALAPPDATA%'s ACLs plus DPAPI stand in for them.
            Assert.Skip("Unix file modes do not apply on Windows.");
            return;
        }

        using var temp = new TempTokenDirectory();

        var store = AuthTestSupport.StoreFor(temp.TokenFilePath);

        await store.SaveAsync(
            AuthTestSupport.TokenSetFor("access", TestTimeProvider.DefaultNow),
            TestContext.Current.CancellationToken);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(temp.TokenFilePath));

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(temp.DirectoryPath));

        var held = await store.AcquireLockAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(held);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(temp.LockFilePath));

        await held.DisposeAsync();
    }
}
