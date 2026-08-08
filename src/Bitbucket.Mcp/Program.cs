// The whole entry point: argv dispatch lives in CliDispatcher (D15) so that it stays testable.
return await Bitbucket.Mcp.Cli.CliDispatcher.RunAsync(args).ConfigureAwait(false);
