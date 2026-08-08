using System.Security.Cryptography;
using System.Text.Json;

using Bitbucket.Mcp.Configuration;
using Bitbucket.Mcp.Http.Models;

using Microsoft.Extensions.Logging;

namespace Bitbucket.Mcp.Authentication;

/// <summary>
/// Reads and writes the OAuth token cache: one small JSON file, encrypted at rest on Windows,
/// permission-protected everywhere else.
/// </summary>
/// <remarks>
/// <para>
/// Bitbucket refresh tokens are single-use, so the file is a piece of mutable shared state that two
/// processes — an MCP server and a <c>bitbucket-mcp login</c> in a terminal — can reach at the same
/// time. Three properties follow from that and are the reason this class exists rather than a pair
/// of <c>File.ReadAllText</c> calls:
/// </para>
/// <list type="number">
/// <item><description>
/// <b>Writes are atomic.</b> A temporary file in the same directory is written, flushed all the way
/// to the device, and then moved over the destination. A reader therefore sees either the old token
/// set or the new one, never a half-written file — and a crash mid-write cannot destroy a working
/// refresh token.
/// </description></item>
/// <item><description>
/// <b>Rotation is serialised.</b> <see cref="AcquireLockAsync"/> hands out a cross-process
/// exclusive lock so that two processes do not spend the same single-use refresh token
/// simultaneously. Failing to get it is not fatal — see the method's remarks.
/// </description></item>
/// <item><description>
/// <b>A damaged file is not an error.</b> Anything that fails to decode — truncated, hand-edited,
/// DPAPI-encrypted by a different user, written by a future layout version — reads back as "no
/// cached token". The recovery is a sign-in, which the caller can do; throwing would only turn a
/// recoverable state into a dead server.
/// </description></item>
/// </list>
/// <para>
/// Nothing here runs at startup. Constructing the store resolves a path and touches no disk.
/// </para>
/// </remarks>
internal sealed class TokenStore
{
    /// <summary>Name of the cache file inside the per-OS directory.</summary>
    internal const string TokenFileName = "tokens.json";

    /// <summary>Name of the cross-process lock file, kept beside the cache.</summary>
    internal const string LockFileName = "tokens.lock";

    /// <summary>Directory (and file) name under the per-OS state root.</summary>
    private const string ApplicationDirectoryName = "bitbucket-mcp";

    /// <summary>How long to keep retrying the cross-process lock before giving up.</summary>
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Pause between lock attempts.</summary>
    private static readonly TimeSpan LockRetryDelay = TimeSpan.FromMilliseconds(50);

    /// <summary><c>rwx------</c> — the token directory is nobody else's business.</summary>
    private const UnixFileMode DirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary><c>rw-------</c> — set at creation, before any content exists in the file.</summary>
    private const UnixFileMode FileMode600 = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;

    /// <param name="options">Supplies <c>BITBUCKET_MCP_TOKEN_FILE</c>, when the user set one.</param>
    /// <param name="loggerFactory">Source of the logger. Everything it writes goes to stderr.</param>
    /// <param name="timeProvider">
    /// Clock and delay source for the lock backoff; <see langword="null"/> means
    /// <see cref="TimeProvider.System"/>.
    /// </param>
    internal TokenStore(BitbucketMcpOptions options, ILoggerFactory loggerFactory, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _logger = loggerFactory.CreateLogger<TokenStore>();
        _timeProvider = timeProvider ?? TimeProvider.System;

        FilePath = ResolveTokenFilePath(options.TokenFilePath);
        DirectoryPath = Path.GetDirectoryName(FilePath) ?? Path.GetFullPath(".");
        LockFilePath = Path.Combine(DirectoryPath, LockFileName);
    }

    /// <summary>
    /// Absolute path of the token cache file. Printed by the <c>status</c> command, which is the
    /// only way a user can find out where their tokens went.
    /// </summary>
    internal string FilePath { get; }

    /// <summary>Directory holding <see cref="FilePath"/> and the lock file.</summary>
    internal string DirectoryPath { get; }

    /// <summary>Absolute path of the cross-process lock file.</summary>
    internal string LockFilePath { get; }

    /// <summary>
    /// Where the token cache lives when <c>BITBUCKET_MCP_TOKEN_FILE</c> is not set: the
    /// conventional per-user, per-OS location for machine-local state that is not configuration and
    /// not a cache that may be wiped.
    /// </summary>
    /// <param name="overridePath">
    /// The value of <c>BITBUCKET_MCP_TOKEN_FILE</c>, or <see langword="null"/>. Interpreted as the
    /// full path of the file itself, not of a directory.
    /// </param>
    internal static string ResolveTokenFilePath(string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return Path.GetFullPath(overridePath.Trim());
        }

        if (OperatingSystem.IsWindows())
        {
            // %LOCALAPPDATA%, whose ACLs already restrict it to this user — which, together with
            // DPAPI, is what stands in for the 0600 mode used elsewhere.
            var localAppData = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolderOption.DoNotVerify);

            if (!string.IsNullOrEmpty(localAppData))
            {
                return Path.Combine(localAppData, ApplicationDirectoryName, TokenFileName);
            }
        }

        var home = Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolderOption.DoNotVerify);

        if (OperatingSystem.IsWindows())
        {
            // %LOCALAPPDATA% is unset — a service account, or a stripped-down environment block.
            // The profile directory is the only other place that is certainly the user's.
            return Path.Combine(home, ApplicationDirectoryName, TokenFileName);
        }

        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(home, "Library", "Application Support", ApplicationDirectoryName, TokenFileName);
        }

        // Linux and everything else: XDG. State, not cache and not config - it must survive a
        // reboot and it is not something the user hand-edits.
        var stateHome = Environment.GetEnvironmentVariable("XDG_STATE_HOME");

        var root = string.IsNullOrWhiteSpace(stateHome)
            ? Path.Combine(home, ".local", "state")
            : stateHome.Trim();

        return Path.Combine(root, ApplicationDirectoryName, TokenFileName);
    }

    /// <summary>
    /// Reads the cached token set, or <see langword="null"/> when there is nothing usable — which
    /// covers "no file", "unreadable file" and "file from another layout version" alike.
    /// </summary>
    /// <remarks>
    /// This method does not throw for a bad file. The one thing it will surface is a cancellation.
    /// </remarks>
    internal async ValueTask<TokenSet?> LoadAsync(CancellationToken cancellationToken)
    {
        byte[] fileBytes;

        try
        {
            fileBytes = await File.ReadAllBytesAsync(FilePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read the token cache at {Path}; treating it as empty.", FilePath);
            return null;
        }

        if (fileBytes.Length == 0)
        {
            return null;
        }

        TokenFileEnvelope? envelope;

        try
        {
            envelope = JsonSerializer.Deserialize(fileBytes, BitbucketWireJsonContext.Default.TokenFileEnvelope);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "The token cache at {Path} is not valid JSON; treating it as empty.", FilePath);
            return null;
        }

        if (envelope is null || string.IsNullOrEmpty(envelope.Payload))
        {
            _logger.LogWarning("The token cache at {Path} is empty or malformed; treating it as empty.", FilePath);
            return null;
        }

        var payload = TryDecodePayload(envelope);

        if (payload is null)
        {
            return null;
        }

        TokenSet? tokens;

        try
        {
            tokens = JsonSerializer.Deserialize(payload, BitbucketWireJsonContext.Default.TokenSet);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "The token cache at {Path} did not decode to a token set; treating it as empty.", FilePath);
            return null;
        }

        if (tokens is null)
        {
            return null;
        }

        if (tokens.Version != TokenSet.CurrentVersion)
        {
            _logger.LogWarning(
                "The token cache at {Path} has layout version {Version}, not {Expected}; treating it as empty.",
                FilePath,
                tokens.Version,
                TokenSet.CurrentVersion);

            return null;
        }

        return tokens;
    }

    /// <summary>
    /// Writes <paramref name="tokens"/> atomically, stamping the current layout version.
    /// </summary>
    /// <remarks>
    /// Callers persist a rotated refresh token with this <em>before</em> the new access token is
    /// handed out, so that a crash in between loses at most an access token and never the refresh
    /// chain.
    /// </remarks>
    /// <exception cref="IOException">The file could not be written.</exception>
    internal async ValueTask SaveAsync(TokenSet tokens, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        var payload = JsonSerializer.SerializeToUtf8Bytes(
            tokens with { Version = TokenSet.CurrentVersion },
            BitbucketWireJsonContext.Default.TokenSet);

        var envelope = Protect(payload);

        var fileBytes = JsonSerializer.SerializeToUtf8Bytes(envelope, BitbucketWireJsonContext.Default.TokenFileEnvelope);

        EnsureDirectory();

        // Same directory as the destination, so the move below is a rename within one volume and
        // therefore atomic. A temp directory would make it a copy, which is not.
        var tempPath = Path.Combine(DirectoryPath, $"{TokenFileName}.{Path.GetRandomFileName()}.tmp");

        try
        {
            await using (var stream = CreateOwnerOnlyFile(tempPath))
            {
                await stream.WriteAsync(fileBytes, cancellationToken).ConfigureAwait(false);

                // flushToDisk: the point of the temp-then-move dance is that the destination is
                // never a partial file; that only holds if the content reached the device before
                // the rename did.
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, FilePath, overwrite: true);
        }
        catch
        {
            TryDeleteTempFile(tempPath);
            throw;
        }
    }

    /// <summary>
    /// Deletes the cached tokens, returning whether a file was actually removed. Used by
    /// <c>logout</c> and by the refresh state machine once a refresh token is proven dead.
    /// </summary>
    /// <remarks>Never throws for a file that is missing, locked or unreadable — it only reports.</remarks>
    internal ValueTask<bool> DeleteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (!File.Exists(FilePath))
            {
                return ValueTask.FromResult(false);
            }

            File.Delete(FilePath);
            return ValueTask.FromResult(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not delete the token cache at {Path}.", FilePath);
            return ValueTask.FromResult(false);
        }
    }

    /// <summary>
    /// Takes the cross-process lock, or returns <see langword="null"/> if it could not be taken
    /// within ten seconds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The lock is an exclusively opened file beside the cache. It exists so that two processes do
    /// not spend the same single-use refresh token at once; the loser waits, re-reads the cache,
    /// and finds the winner's freshly rotated token already there.
    /// </para>
    /// <para>
    /// A <see langword="null"/> return is deliberately <em>not</em> an error. A stale lock — left by
    /// a process that was killed on a platform where the handle outlives it, or held by a sign-in
    /// waiting on a browser — must not permanently break authentication. The caller logs a warning
    /// and proceeds; the worst case is one wasted refresh token, which the
    /// <see cref="TokenSet.PreviousRefreshToken"/> fallback is there to recover from.
    /// </para>
    /// </remarks>
    internal async ValueTask<TokenStoreLock?> AcquireLockAsync(CancellationToken cancellationToken)
    {
        try
        {
            EnsureDirectory();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not create the token directory {Path}; proceeding without a lock.", DirectoryPath);
            return null;
        }

        var deadline = _timeProvider.GetUtcNow() + LockTimeout;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return new TokenStoreLock(CreateOwnerOnlyLockFile(LockFilePath));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (_timeProvider.GetUtcNow() >= deadline)
                {
                    _logger.LogWarning(
                        ex,
                        "Could not acquire the token lock at {Path} within {Timeout}; proceeding without it.",
                        LockFilePath,
                        LockTimeout);

                    return null;
                }
            }

            await Task.Delay(LockRetryDelay, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Wraps the serialised token set in the self-describing envelope, encrypting it with DPAPI
    /// where that exists.
    /// </summary>
    /// <remarks>
    /// On Unix the payload is plain and the protection is the file mode. DPAPI has no portable
    /// equivalent worth hand-rolling: a key derived from anything the process can read is
    /// obfuscation, and the alternative (a platform keyring) is a dependency this project will not
    /// take.
    /// </remarks>
    private TokenFileEnvelope Protect(byte[] payload)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var protectedBytes = ProtectedData.Protect(payload, optionalEntropy: null, DataProtectionScope.CurrentUser);

                return new TokenFileEnvelope
                {
                    Scheme = TokenFileEnvelope.SchemeDpapi,
                    Payload = Convert.ToBase64String(protectedBytes),
                };
            }
            catch (CryptographicException ex)
            {
                // Seen in unusual profiles (a roaming profile without a master key). A readable
                // token file beats no token file, and the directory ACLs still apply.
                _logger.LogWarning(ex, "DPAPI protection failed; storing the token cache unencrypted at {Path}.", FilePath);
            }
        }

        return new TokenFileEnvelope
        {
            Scheme = TokenFileEnvelope.SchemePlain,
            Payload = Convert.ToBase64String(payload),
        };
    }

    /// <summary>
    /// Turns an envelope back into the serialised token set, or <see langword="null"/> when it
    /// cannot be decoded here — a DPAPI file copied to Linux, or to another Windows account.
    /// </summary>
    private byte[]? TryDecodePayload(TokenFileEnvelope envelope)
    {
        byte[] raw;

        try
        {
            raw = Convert.FromBase64String(envelope.Payload);
        }
        catch (FormatException ex)
        {
            _logger.LogWarning(ex, "The token cache at {Path} has a malformed payload; treating it as empty.", FilePath);
            return null;
        }

        if (string.Equals(envelope.Scheme, TokenFileEnvelope.SchemePlain, StringComparison.Ordinal))
        {
            return raw;
        }

        if (!string.Equals(envelope.Scheme, TokenFileEnvelope.SchemeDpapi, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "The token cache at {Path} uses unknown protection scheme '{Scheme}'; treating it as empty.",
                FilePath,
                envelope.Scheme);

            return null;
        }

        if (!OperatingSystem.IsWindows())
        {
            _logger.LogWarning(
                "The token cache at {Path} is DPAPI-protected and cannot be read on this platform; treating it as empty.",
                FilePath);

            return null;
        }

        try
        {
            return ProtectedData.Unprotect(raw, optionalEntropy: null, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException ex)
        {
            _logger.LogWarning(
                ex,
                "The token cache at {Path} could not be decrypted (it belongs to another user or machine); treating it as empty.",
                FilePath);

            return null;
        }
    }

    /// <summary>Creates the token directory, restricted to the owner where the OS supports it.</summary>
    private void EnsureDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(DirectoryPath);
            return;
        }

        // The mode applies to directories this call creates; an existing directory keeps whatever
        // the user gave it, which is theirs to decide.
        Directory.CreateDirectory(DirectoryPath, DirectoryMode);
    }

    /// <summary>
    /// Creates a new file that only the owner can read, with the mode applied at creation so that
    /// no window exists in which the file is both world-readable and non-empty.
    /// </summary>
    private static FileStream CreateOwnerOnlyFile(string path)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };

        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = FileMode600;
        }

        var stream = new FileStream(path, options);

        if (!OperatingSystem.IsWindows())
        {
            // Belt and braces: UnixCreateMode covers creation, and this covers the case where the
            // file somehow already carried a laxer mode. Still before a single byte is written.
            File.SetUnixFileMode(stream.SafeFileHandle, FileMode600);
        }

        return stream;
    }

    /// <summary>
    /// Opens the lock file exclusively. <see cref="FileShare.None"/> is the whole mechanism: the
    /// second process to try it gets an <see cref="IOException"/>.
    /// </summary>
    /// <remarks>
    /// The file is left behind on release, deliberately. Deleting it on close would let a waiter
    /// that opened the doomed name and a newcomer that created a fresh one hold "the lock" at the
    /// same time. An empty leftover file costs nothing, and a killed process still releases the
    /// lock — the OS drops the handle with the process.
    /// </remarks>
    private static FileStream CreateOwnerOnlyLockFile(string path)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.OpenOrCreate,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
        };

        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = FileMode600;
        }

        return new FileStream(path, options);
    }

    private void TryDeleteTempFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Could not remove the temporary token file {Path}.", path);
            }
        }
    }
}

/// <summary>
/// A held cross-process token lock. Disposing it releases the lock; there is nothing else to do
/// with it.
/// </summary>
internal sealed class TokenStoreLock : IDisposable, IAsyncDisposable
{
    private FileStream? _stream;

    internal TokenStoreLock(FileStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
    }

    /// <inheritdoc />
    public void Dispose() => Interlocked.Exchange(ref _stream, null)?.Dispose();

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        var stream = Interlocked.Exchange(ref _stream, null);

        if (stream is not null)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }
}
