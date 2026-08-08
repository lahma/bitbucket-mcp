using System.Globalization;

using Microsoft.Extensions.Logging;

namespace Bitbucket.Mcp.Configuration;

/// <summary>
/// Every knob the server has, read once from environment variables. There are deliberately no
/// configuration files and no <c>Microsoft.Extensions.Configuration</c> providers (D3): an MCP
/// client launches the binary with an environment block and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="FromEnvironment()"/> never throws. A malformed number or log level falls back to the
/// documented default, because failing startup over a typo would leave the MCP client with a dead
/// server and no way to see why — stdout is the protocol channel, so there is nowhere to complain.
/// Anything genuinely unusable (missing credentials) surfaces on first use as an actionable error.
/// </para>
/// </remarks>
internal sealed record BitbucketMcpOptions
{
    /// <summary>Default loopback port for the OAuth callback listener.</summary>
    internal const int DefaultOAuthCallbackPort = 33418;

    /// <summary>Default loopback host for the OAuth callback listener.</summary>
    internal const string DefaultOAuthCallbackHost = "127.0.0.1";

    /// <summary>Default bound, in seconds, on a blocking browser sign-in.</summary>
    internal const int DefaultAuthTimeoutSeconds = 180;

    /// <summary>Default per-file cap when rendering a unified diff.</summary>
    internal const int DefaultMaxLinesPerFile = 400;

    /// <summary>Default cap on the total number of diff lines returned by one call.</summary>
    internal const int DefaultMaxDiffLines = 4000;

    /// <summary>Default minimum log level.</summary>
    internal const LogLevel DefaultLogLevel = LogLevel.Information;

    /// <summary><c>BITBUCKET_ACCESS_TOKEN</c> — bearer token; highest precedence, bypasses OAuth.</summary>
    internal string? AccessToken { get; init; }

    /// <summary><c>BITBUCKET_EMAIL</c> — Atlassian account email, paired with <see cref="ApiToken"/>.</summary>
    internal string? Email { get; init; }

    /// <summary>
    /// <c>BITBUCKET_API_TOKEN</c> — Atlassian API token; with <see cref="Email"/> forms
    /// <c>Basic base64(email:token)</c>, the second precedence. (App passwords are dead — removed
    /// 2026-07-28 — and are never implemented here.)
    /// </summary>
    internal string? ApiToken { get; init; }

    /// <summary><c>BITBUCKET_OAUTH_KEY</c> — OAuth consumer key (browser flow).</summary>
    internal string? OAuthKey { get; init; }

    /// <summary><c>BITBUCKET_OAUTH_SECRET</c> — OAuth consumer secret (browser flow, D13).</summary>
    internal string? OAuthSecret { get; init; }

    /// <summary>
    /// <c>BITBUCKET_OAUTH_CALLBACK_PORT</c> — must match the consumer's registered callback URL.
    /// </summary>
    internal int OAuthCallbackPort { get; init; } = DefaultOAuthCallbackPort;

    /// <summary>
    /// <c>BITBUCKET_OAUTH_CALLBACK_HOST</c> — must match the consumer's registered callback URL.
    /// Bitbucket may insist on <c>localhost</c> rather than <c>127.0.0.1</c> (risk R3); the
    /// listener is dual-stack either way.
    /// </summary>
    internal string OAuthCallbackHost { get; init; } = DefaultOAuthCallbackHost;

    /// <summary>
    /// <c>BITBUCKET_DEFAULT_WORKSPACE</c> — optional default for the <c>workspace</c> tool
    /// parameter. This is the workspace <em>URL segment</em>, not its display name.
    /// </summary>
    internal string? DefaultWorkspace { get; init; }

    /// <summary>
    /// <c>BITBUCKET_MCP_TOKEN_FILE</c> — overrides the per-OS token cache location.
    /// <see langword="null"/> means "use the per-OS default", which the token store resolves.
    /// </summary>
    internal string? TokenFilePath { get; init; }

    /// <summary>
    /// <c>BITBUCKET_MCP_NO_BROWSER</c> — when set, never launch a browser; fail with instructions
    /// instead. For headless or remote sessions where a browser would open on the wrong machine.
    /// </summary>
    internal bool NoBrowser { get; init; }

    /// <summary>
    /// <c>BITBUCKET_MCP_AUTH_TIMEOUT_SECONDS</c> — bound on a blocking browser sign-in triggered
    /// from inside a tool call.
    /// </summary>
    internal int AuthTimeoutSeconds { get; init; } = DefaultAuthTimeoutSeconds;

    /// <summary><c>BITBUCKET_MCP_LOG_LEVEL</c> — minimum level for the stderr logger.</summary>
    internal LogLevel LogLevel { get; init; } = DefaultLogLevel;

    /// <summary><c>BITBUCKET_MCP_MAX_LINES_PER_FILE</c> — diff truncation default, per file.</summary>
    internal int MaxLinesPerFile { get; init; } = DefaultMaxLinesPerFile;

    /// <summary><c>BITBUCKET_MCP_MAX_DIFF_LINES</c> — diff truncation default, per response.</summary>
    internal int MaxDiffLines { get; init; } = DefaultMaxDiffLines;

    /// <summary>Reads the options from the process environment.</summary>
    internal static BitbucketMcpOptions FromEnvironment() =>
        FromEnvironment(static name => Environment.GetEnvironmentVariable(name));

    /// <summary>
    /// Reads the options from an arbitrary variable source. Tests use this overload so they never
    /// have to mutate the (process-wide, test-parallelism-hostile) real environment.
    /// </summary>
    /// <param name="read">Returns the raw value of a variable, or <see langword="null"/> if unset.</param>
    internal static BitbucketMcpOptions FromEnvironment(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);

        return new BitbucketMcpOptions
        {
            AccessToken = ReadString(read, "BITBUCKET_ACCESS_TOKEN"),
            Email = ReadString(read, "BITBUCKET_EMAIL"),
            ApiToken = ReadString(read, "BITBUCKET_API_TOKEN"),
            OAuthKey = ReadString(read, "BITBUCKET_OAUTH_KEY"),
            OAuthSecret = ReadString(read, "BITBUCKET_OAUTH_SECRET"),
            OAuthCallbackPort = ReadInt32(read, "BITBUCKET_OAUTH_CALLBACK_PORT", DefaultOAuthCallbackPort, 1, 65535),
            OAuthCallbackHost = ReadString(read, "BITBUCKET_OAUTH_CALLBACK_HOST") ?? DefaultOAuthCallbackHost,
            DefaultWorkspace = ReadString(read, "BITBUCKET_DEFAULT_WORKSPACE"),
            TokenFilePath = ReadString(read, "BITBUCKET_MCP_TOKEN_FILE"),
            NoBrowser = ReadBoolean(read, "BITBUCKET_MCP_NO_BROWSER", defaultValue: false),
            AuthTimeoutSeconds = ReadInt32(read, "BITBUCKET_MCP_AUTH_TIMEOUT_SECONDS", DefaultAuthTimeoutSeconds, 1, 3600),
            LogLevel = ReadLogLevel(read, "BITBUCKET_MCP_LOG_LEVEL", DefaultLogLevel),
            MaxLinesPerFile = ReadInt32(read, "BITBUCKET_MCP_MAX_LINES_PER_FILE", DefaultMaxLinesPerFile, 1, 100_000),
            MaxDiffLines = ReadInt32(read, "BITBUCKET_MCP_MAX_DIFF_LINES", DefaultMaxDiffLines, 1, 1_000_000),
        };
    }

    /// <summary>Trims and normalises an unset or all-whitespace variable to <see langword="null"/>.</summary>
    private static string? ReadString(Func<string, string?> read, string name)
    {
        var raw = read(name);
        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
    }

    /// <summary>Parses an integer, falling back to <paramref name="defaultValue"/> when unparsable or out of range.</summary>
    private static int ReadInt32(Func<string, string?> read, string name, int defaultValue, int min, int max)
    {
        var raw = ReadString(read, name);

        if (raw is null || !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return defaultValue;
        }

        return value < min || value > max ? defaultValue : value;
    }

    /// <summary>
    /// Parses a boolean flag. <c>1/true/yes/on</c> and <c>0/false/no/off</c> are accepted (either
    /// case); anything else falls back to <paramref name="defaultValue"/>.
    /// </summary>
    private static bool ReadBoolean(Func<string, string?> read, string name, bool defaultValue)
    {
        var raw = ReadString(read, name);

        return raw?.ToUpperInvariant() switch
        {
            "1" or "TRUE" or "YES" or "ON" => true,
            "0" or "FALSE" or "NO" or "OFF" => false,
            _ => defaultValue,
        };
    }

    /// <summary>Parses a <see cref="Microsoft.Extensions.Logging.LogLevel"/> name, falling back to the default.</summary>
    private static LogLevel ReadLogLevel(Func<string, string?> read, string name, LogLevel defaultValue)
    {
        var raw = ReadString(read, name);

        // Enum.TryParse also accepts raw numbers, so IsDefined is what actually rejects "42".
        if (raw is null || !Enum.TryParse<LogLevel>(raw, ignoreCase: true, out var level) || !Enum.IsDefined(level))
        {
            return defaultValue;
        }

        return level;
    }
}
