using Bitbucket.Mcp.Cli;


namespace Bitbucket.Mcp.Tests;

public class SmokeTests
{
    [Test]
    public async Task HelpSucceedsAndDescribesEveryCliMode()
    {
        var exitCode = await CliDispatcher.RunAsync(["--help"]);

        Assert.That(exitCode, Is.EqualTo(CliDispatcher.ExitSuccess));
        Assert.That(CliDispatcher.UsageText, Does.Contain("serve"));
        Assert.That(CliDispatcher.UsageText, Does.Contain("login"));
        Assert.That(CliDispatcher.UsageText, Does.Contain("logout"));
        Assert.That(CliDispatcher.UsageText, Does.Contain("status"));
    }

    [Test]
    public async Task UnknownArgumentExitsWithUsageCode()
    {
        var exitCode = await CliDispatcher.RunAsync(["not-a-command"]);

        Assert.That(exitCode, Is.EqualTo(CliDispatcher.ExitUsage));
    }
}
