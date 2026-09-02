using CodeMap.Query.ArgParsing;
using CodeMap.Query.Config;
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
                "sync" => SyncCommand.Run(rest),
                "projects" => ProjectsCommand.Run(rest),
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

            Registered projects (needs a codemap.projects.json — see README):
              codemap projects [--config <file>]
              codemap sync     (--project <name> | --all) [--config <file>]

            Build an index by hand:
              codemap scan     --solution <path.sln|path.slnx> --out <dir> [--syntax-only] [--include-external]
              codemap scan-fe  --root <frontend dir> --out <dir>
              codemap scan-git --repo <path> --out <dir> [--since 2024-01-01]
              codemap link     <index>
              codemap map      <index> --out <dir>

            Query an index:
              codemap find     <index> --query <text>
              codemap where    <index> --query "<business description>"
              codemap impact   <index> --symbol <docId> [--depth 5] [--full] [--out <file.md>]
              codemap slice    <index> --symbol <docId> [--depth 3] [--out <file.md>]

            <index> above means either --index <dir> or --project <name> (resolved via codemap.projects.json).

            See README.md to get started, or docs/CODEMAP-SPEC.md for the design.
            """);
    }
}
