using CodeMap.Query.ArgParsing;
using CodeMap.Query.Find;
using CodeMap.Query.FrontendScan;
using CodeMap.Query.Git;
using CodeMap.Query.Impact;
using CodeMap.Query.Link;
using CodeMap.Query.Map;
using CodeMap.Query.Where;
using CodeMap.Roslyn.Scan;

namespace CodeMap.Cli.Cli;

internal static class CliApp
{
    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        var command = args[0];
        var rest = args[1..];

        try
        {
            return command switch
            {
                "scan" => ScanCommand.Run(rest),
                "scan-fe" => ScanFeCommand.Run(rest),
                "scan-git" => ScanGitCommand.Run(rest),
                "link" => LinkCommand.Run(rest),
                "map" => MapCommand.Run(rest),
                "find" => FindCommand.Run(rest),
                "where" => WhereCommand.Run(rest),
                "impact" => ImpactCommand.Run(rest),
                "slice" => SliceCommand.Run(rest),
                "-h" or "--help" or "help" => PrintUsageOk(),
                _ => UnknownCommand(command),
            };
        }
        catch (CliUsageException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static int NotImplemented(string command, string phase)
    {
        Console.Error.WriteLine($"'{command}' is not implemented yet — coming in {phase}.");
        return 1;
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command: '{command}'");
        PrintUsage();
        return 1;
    }

    private static int PrintUsageOk()
    {
        PrintUsage();
        return 0;
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("""
            codemap — .NET codebase index tool for AI (offline, CLI, static output)

            Usage:
              codemap scan     --solution <path.sln> --out <dir> [--syntax-only] [--include-external]
              codemap scan-fe  --root <frontend dir> --out <dir>
              codemap scan-git --repo <path> --out <dir> [--since 2024-01-01]
              codemap link     --index <dir>
              codemap find     --index <dir> --query <text>
              codemap where    --index <dir> --query "<business description>"
              codemap impact   --index <dir> --symbol <docId> [--depth 5] [--full] [--out <file.md>]
              codemap slice    --index <dir> --symbol <docId> [--depth 3] [--out <file.md>]
              codemap map      --index <dir> --out <dir>

            All commands above are implemented. See README.md to get started, or docs/CODEMAP-SPEC.md for the design.
            """);
    }
}
