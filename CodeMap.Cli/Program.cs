Console.OutputEncoding = System.Text.Encoding.UTF8;

// No MSBuild/Roslyn bootstrap here: it's lazy-initialized inside ScanCommand's L2 branch only
// (spec section 2) so every other command can run on a machine with no Build Tools installed.
return CodeMap.Cli.Cli.CliApp.Run(args);
