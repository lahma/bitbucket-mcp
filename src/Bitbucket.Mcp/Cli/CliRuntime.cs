using System.Globalization;

using Bitbucket.Mcp.Configuration;

using Microsoft.Extensions.Logging;

namespace Bitbucket.Mcp.Cli;

/// <summary>
/// The few things all three CLI commands need: a logger factory, a Ctrl+C token, and consistent
/// formatting of the values they print.
/// </summary>
/// <remarks>
/// Logging still goes to stderr in CLI mode, exactly as in server mode. The commands' own output —
/// what the user asked for — goes to stdout, which only this namespace may touch (AGENTS.md rule 3).
/// Keeping the two apart is what makes <c>bitbucket-mcp status</c> pipeable while a warning from the
/// token store is still visible.
/// </remarks>
internal static class CliRuntime
{
    /// <summary>Builds the stderr logger factory the CLI commands share.</summary>
    internal static ILoggerFactory CreateLoggerFactory(BitbucketMcpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return LoggerFactory.Create(logging =>
        {
            logging.SetMinimumLevel(options.LogLevel);
            logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        });
    }

    /// <summary>
    /// A token that fires on Ctrl+C, so a <c>login</c> waiting on a browser can be abandoned without
    /// killing the process mid-write to the token cache.
    /// </summary>
    /// <param name="onCancel">Runs on the first Ctrl+C, before the token fires. For a message.</param>
    internal static CancellationTokenSource CreateInterruptTokenSource(Action? onCancel = null)
    {
        var source = new CancellationTokenSource();

        Console.CancelKeyPress += (_, e) =>
        {
            // Cancel the operation instead of terminating: the second Ctrl+C still kills us.
            if (source.IsCancellationRequested)
            {
                return;
            }

            e.Cancel = true;
            onCancel?.Invoke();

            try
            {
                source.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already finished.
            }
        };

        return source;
    }

    /// <summary>Formats an absolute expiry as a UTC timestamp plus how long that is from now.</summary>
    internal static string FormatExpiry(DateTimeOffset expiresAtUtc, DateTimeOffset now)
    {
        var stamp = expiresAtUtc.ToUniversalTime().ToString("u", CultureInfo.InvariantCulture);
        var remaining = expiresAtUtc - now;

        if (remaining <= TimeSpan.Zero)
        {
            return $"{stamp} (expired)";
        }

        var amount = remaining.TotalHours >= 1
            ? $"{remaining.TotalHours.ToString("0.#", CultureInfo.InvariantCulture)} h"
            : $"{Math.Ceiling(remaining.TotalMinutes).ToString("0", CultureInfo.InvariantCulture)} min";

        return $"{stamp} (in {amount})";
    }
}
