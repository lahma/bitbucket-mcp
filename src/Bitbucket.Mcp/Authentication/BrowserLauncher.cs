using System.ComponentModel;
using System.Diagnostics;

using Microsoft.Extensions.Logging;

namespace Bitbucket.Mcp.Authentication;

/// <summary>
/// Opens a URL in the user's default browser, or reports that it could not.
/// </summary>
/// <remarks>
/// <para>
/// One explicit branch per operating system, because there is no portable way to do this and the
/// three mechanisms fail differently. Windows needs <see cref="ProcessStartInfo.UseShellExecute"/>
/// so that the shell resolves the registered <c>http</c> handler; macOS has <c>open</c>; Linux has
/// <c>xdg-open</c>, which is absent often enough (a bare container, a minimal desktop) that its
/// failure has to be an ordinary outcome rather than an exception.
/// </para>
/// <para>
/// Failure is therefore a <see langword="false"/> return, not a throw. The caller keeps the loopback
/// listener up and logs the authorization URL so the user can paste it into a browser themselves —
/// which is also what happens over SSH, where a browser would otherwise open on the wrong machine.
/// </para>
/// </remarks>
internal static class BrowserLauncher
{
    /// <summary>
    /// Tries to open <paramref name="url"/>. Returns whether the launch was handed off successfully;
    /// a browser that starts and then crashes is indistinguishable from one that worked, which is
    /// why the caller waits on the loopback callback rather than on this.
    /// </summary>
    /// <param name="url">An absolute <c>http</c> or <c>https</c> URL.</param>
    /// <param name="logger">Logger. Everything it writes goes to stderr.</param>
    internal static bool TryOpen(string url, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentNullException.ThrowIfNull(logger);

        // This URL is built by this process and never received from anywhere. The check is here so
        // that stays true: a shell handler will happily launch things that are not browsers.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Only absolute http and https URLs can be opened.", nameof(url));
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                // UseShellExecute is what makes this open the default browser rather than try to
                // execute the URL as a program. A null Process is still success: the shell may have
                // handed the URL to an already-running browser instead of starting one.
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start("open", url)?.Dispose();
            }
            else
            {
                Process.Start("xdg-open", url)?.Dispose();
            }

            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            // No handler registered for http, no xdg-open on PATH, no desktop session at all.
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(ex, "Could not launch a browser for {Url}.", url);
            }

            return false;
        }
    }
}
