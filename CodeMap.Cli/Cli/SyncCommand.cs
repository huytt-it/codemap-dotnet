using CodeMap.Query.ArgParsing;
using CodeMap.Query.Config;
using CodeMap.Query.FrontendScan;
using CodeMap.Query.Git;
using CodeMap.Query.Link;
using CodeMap.Query.Map;
using CodeMap.Roslyn.Scan;

namespace CodeMap.Cli.Cli;

/// <summary>
/// `codemap sync --project X` (or `--all`) — runs the whole pipeline for one registered project in the order the
/// data dependencies require. Same steps a person would type by hand, and the same failure policy as
/// scripts/nightly-scan.ps1: `scan` failing aborts that project, because every later step would then be
/// operating on a stale or empty index, which is worse than not running at all. `scan-git` and the
/// `scan-fe`/`link` pair are optional enrichment — a repo with no git history or no frontend still gets a
/// usable MAP.md instead of the whole run failing.
/// </summary>
internal static class SyncCommand
{
    public static int Run(string[] rawArgs)
    {
        var args = Args.Parse(rawArgs);
        var all = args.HasFlag("all");
        var projectName = args.GetOrDefault("project");

        if (all == (projectName != null))
            throw new CliUsageException("Pass exactly one of --project <name> or --all.");

        var registry = ProjectRegistry.Discover(args.GetOrDefault("config"))
            ?? throw new CliUsageException(
                $"No {ProjectRegistry.FileName} found in this directory, any parent, or ~/.codemap/. " +
                "`sync` runs the pipeline for a registered project — see README.md for the file format.");

        var targets = all ? registry.Projects : new List<ProjectEntry> { registry.Require(projectName!) };
        if (targets.Count == 0)
        {
            Console.Error.WriteLine($"{registry.SourcePath} defines no projects.");
            return 1;
        }

        var failed = new List<string>();
        foreach (var entry in targets)
        {
            Console.WriteLine($"=== {entry.Name} ===");
            if (!SyncOne(registry, entry)) failed.Add(entry.Name);
            Console.WriteLine();
        }

        if (failed.Count > 0)
        {
            Console.Error.WriteLine($"Failed: {string.Join(", ", failed)}");
            return 1;
        }

        Console.WriteLine(targets.Count == 1 ? "Done." : $"Done — {targets.Count} project(s).");
        return 0;
    }

    private static bool SyncOne(ProjectRegistry registry, ProjectEntry entry)
    {
        var solution = registry.ResolvePath(entry.Solution);
        var outDir = registry.ResolvePath(entry.Output);
        var indexDir = Path.Combine(outDir, "index");
        var repo = registry.RepoOf(entry);

        if (!File.Exists(solution))
        {
            Console.Error.WriteLine($"  solution not found: {solution}");
            return false;
        }

        // The staleness banner compares the index against the CURRENT working directory's git HEAD (the same
        // convention every query command uses), so the whole run has to happen from inside the target repo.
        var previousCwd = Directory.GetCurrentDirectory();
        try
        {
            if (Directory.Exists(repo)) Directory.SetCurrentDirectory(repo);

            if (!Step("scan", () => ScanCommand.Run(new[] { "--solution", solution, "--out", outDir })))
                return false;

            if (Directory.Exists(Path.Combine(repo, ".git")))
                Step("scan-git", () => ScanGitCommand.Run(new[] { "--repo", repo, "--out", outDir }), optional: true);
            else
                Console.WriteLine("  scan-git: skipped (not a git repo)");

            if (entry.Frontend != null)
            {
                var frontend = registry.ResolvePath(entry.Frontend);
                if (Directory.Exists(frontend))
                {
                    if (Step("scan-fe", () => ScanFeCommand.Run(new[] { "--root", frontend, "--out", outDir }), optional: true))
                        Step("link", () => LinkCommand.Run(new[] { "--index", indexDir }), optional: true);
                }
                else
                {
                    Console.Error.WriteLine($"  scan-fe: skipped, frontend path not found: {frontend}");
                }
            }
            else
            {
                Console.WriteLine("  scan-fe: skipped (no frontend configured)");
            }

            // After link, so entry points list their linked FE screens.
            return Step("map", () => MapCommand.Run(new[] { "--index", indexDir, "--out", outDir }));
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCwd);
        }
    }

    private static bool Step(string name, Func<int> run, bool optional = false)
    {
        Console.WriteLine($"  {name}...");
        int exit;
        try
        {
            exit = run();
        }
        catch (Exception ex)
        {
            // An optional step throwing must not take the run down — that is the whole point of it being optional.
            if (!optional) throw;
            Console.Error.WriteLine($"  {name}: skipped ({ex.Message})");
            return false;
        }

        if (exit == 0) return true;

        Console.Error.WriteLine(optional
            ? $"  {name}: failed (exit {exit}) — continuing, this step is optional enrichment"
            : $"  {name}: FAILED (exit {exit}) — aborting this project");
        return false;
    }
}
