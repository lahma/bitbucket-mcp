using Bitbucket.Mcp.Cli;

using Xunit;

namespace Bitbucket.Mcp.Tests;

public class SmokeTests
{
    [Fact]
    public async Task HelpSucceedsAndDescribesEveryCliMode()
    {
        var exitCode = await CliDispatcher.RunAsync(["--help"]);

        Assert.Equal(CliDispatcher.ExitSuccess, exitCode);
        Assert.Contains("serve", CliDispatcher.UsageText, StringComparison.Ordinal);
        Assert.Contains("login", CliDispatcher.UsageText, StringComparison.Ordinal);
        Assert.Contains("logout", CliDispatcher.UsageText, StringComparison.Ordinal);
        Assert.Contains("status", CliDispatcher.UsageText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownArgumentExitsWithUsageCode()
    {
        var exitCode = await CliDispatcher.RunAsync(["not-a-command"]);

        Assert.Equal(CliDispatcher.ExitUsage, exitCode);
    }
}
