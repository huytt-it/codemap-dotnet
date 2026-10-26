using CodeMap.Query.ArgParsing;
using CodeMap.Query.Config;
using CodeMap.Query.FrontendScan;
using CodeMap.Query.Git;
using CodeMap.Query.Json;
using CodeMap.Query.Link;
using CodeMap.Query.Map;
using CodeMap.Query.Models;
using CodeMap.Roslyn.Scan;

namespace CodeMap.Cli.Cli;

/// <summary>
/// `codemap sync --project X` (or `--all`) — runs the whole pipeline for one registered project in the order the
/// data dependencies require. Same steps a person would type by hand, and the same failure policy as
/// scripts/nightly-scan.ps1: `scan` failing aborts that project, because every later step would then be
/// operating on a stale or empty index, which is worse than not running at all. `scan-git` and the
/// `scan-fe`/`link` pair are optional enrichment — a repo with no git history or no frontend still gets a
/// usable MAP.md instead of the whole run failing.
///
/// The steps do not write into Output. They write into a staging directory next to it (<see cref="SyncWorkspace"/>),
/// and only when `scan` and `map` have both succeeded is the finished index swapped in, with
/// <c>index/sync-complete.json</c> (<see cref="SyncMarker"/>) inside it. Run by hand, `scan` and the other
/// commands still write straight into their `--out` and leave no marker — only `sync` produces a verified index.
/// Run in place, as `sync` used to, a reader could see meta.json from the new scan beside the old scan-git
/// output, and a step dropped from the registry (say `frontend`) left its previous files in the index for good.
/// </summary>
internal static class SyncCommand
{
    public static int Run(string[] rawArgs)
    {
        var args = Args.Parse(rawArgs, options: new[] { "project", "config" }, flags: new[] { "all" });
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
            if (!SyncOne(registry, entry, SyncSteps.Real)) failed.Add(entry.Name);
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

    internal static bool SyncOne(ProjectRegistry registry, ProjectEntry entry, SyncSteps steps)
    {
        var solution = registry.ResolvePath(entry.Solution);
        var outDir = registry.ResolvePath(entry.Output);
        var repo = registry.RepoOf(entry);

        if (!File.Exists(solution))
        {
            Console.Error.WriteLine($"  solution not found: {solution}");
            return false;
        }

        SyncWorkspace workspace;
        try
        {
            workspace = SyncWorkspace.Open(outDir, steps.PublishRetryDelays);
        }
        catch (Exception ex) when (ex is SyncPublishException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"  cannot start: {ex.Message}");
            return false;
        }

        using (workspace)
        {
            var previousIndex = Path.Combine(outDir, "index");
            Console.WriteLine($"  building in {workspace.StagingDir}; {previousIndex} is replaced only once scan and map have finished");

            // The staleness banner compares the index against the CURRENT working directory's git HEAD (the same
            // convention every query command uses), so the whole run has to happen from inside the target repo.
            var previousCwd = Directory.GetCurrentDirectory();
            try
            {
                if (Directory.Exists(repo)) Directory.SetCurrentDirectory(repo);

                var results = new List<SyncStepResult>();
                if (!RunPipeline(entry, registry, solution, repo, workspace, steps, results))
                {
                    Console.Error.WriteLine($"  the previous index at {previousIndex} was left as it was");
                    return false;
                }

                WriteMarker(entry, workspace.StagingIndexDir, results);

                try
                {
                    workspace.PublishIndex();
                }
                catch (SyncPublishException ex)
                {
                    Console.Error.WriteLine($"  publish FAILED: {ex.Message}");
                    return false;
                }

                Console.WriteLine($"  published: {previousIndex}");

                try
                {
                    workspace.PublishDocs();
                }
                catch (SyncPublishException ex)
                {
                    Console.Error.WriteLine(
                        $"  the index is up to date, but MAP.md / modules/ are not: {ex.Message}. Re-run sync to refresh them.");
                    return false;
                }

                return true;
            }
            finally
            {
                Directory.SetCurrentDirectory(previousCwd);
            }
        }
    }

    /// <summary>Runs the steps into the staging directory, recording each outcome. False when a required step (scan, map) did not succeed.</summary>
    private static bool RunPipeline(
        ProjectEntry entry, ProjectRegistry registry, string solution, string repo, SyncWorkspace workspace,
        SyncSteps steps, List<SyncStepResult> results)
    {
        var stagingDir = workspace.StagingDir;
        var indexDir = workspace.StagingIndexDir;

        if (!Step("scan", () => steps.Scan(new[] { "--solution", solution, "--out", stagingDir }), results, indexDir))
            return false;

        if (Directory.Exists(Path.Combine(repo, ".git")))
        {
            Step("scan-git", () => steps.ScanGit(new[] { "--repo", repo, "--out", stagingDir }), results, indexDir,
                optional: true, ownedFiles: new[] { "ticket-files.jsonl", "co-change.jsonl" });
        }
        else
        {
            Skip("scan-git", "not a git repo", results);
        }

        if (entry.Frontend == null)
        {
            Skip("scan-fe", "no frontend configured", results);
            Skip("link", "no frontend configured", results);
        }
        else
        {
            var frontend = registry.ResolvePath(entry.Frontend);
            if (!Directory.Exists(frontend))
            {
                Skip("scan-fe", $"frontend path not found: {frontend}", results, toStderr: true);
                Skip("link", "scan-fe did not run", results);
            }
            else if (Step("scan-fe", () => steps.ScanFe(new[] { "--root", frontend, "--out", stagingDir }), results, indexDir,
                         optional: true, ownedFiles: new[] { "frontend-calls.jsonl" }))
            {
                Step("link", () => steps.Link(new[] { "--index", indexDir }), results, indexDir,
                    optional: true, ownedFiles: new[] { "api-links.jsonl" });
            }
            else
            {
                Skip("link", "scan-fe did not complete", results);
            }
        }

        // After link, so entry points list their linked FE screens.
        return Step("map", () => steps.Map(new[] { "--index", indexDir, "--out", stagingDir }), results, indexDir);
    }

    /// <summary>Last thing before the swap, and into the staging index, so the marker exists exactly when the directory it sits in is complete.</summary>
    private static void WriteMarker(ProjectEntry entry, string stagingIndexDir, List<SyncStepResult> results)
    {
        MetaModel? meta = null;
        try
        {
            var metaPath = Path.Combine(stagingIndexDir, "meta.json");
            if (File.Exists(metaPath)) meta = JsonUtil.ReadFile<MetaModel>(metaPath);
        }
        catch
        {
            // scan wrote it a moment ago; if it is unreadable, map would already have said so. The marker just goes without.
        }

        SyncMarker.Write(stagingIndexDir, new SyncMarker
        {
            SyncId = Guid.NewGuid().ToString("N"),
            CompletedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            Project = entry.Name,
            GitCommit = meta?.GitCommit,
            MetaIndexedAt = meta?.IndexedAt,
            Steps = results,
        });
    }

    private static void Skip(string name, string reason, List<SyncStepResult> results, bool toStderr = false)
    {
        results.Add(new SyncStepResult { Name = name, Status = SyncStepResult.Skipped, Detail = reason });
        (toStderr ? Console.Error : Console.Out).WriteLine($"  {name}: skipped ({reason})");
    }

    /// <summary>
    /// Runs one step and records how it went. True only for a clean success. When an optional step fails, the files
    /// it owns are deleted from staging: a half-written output must not be published as if the step had finished,
    /// and "the file is absent" is what the marker's "failed" means to a reader.
    /// </summary>
    private static bool Step(
        string name, Func<int> run, List<SyncStepResult> results, string stagingIndexDir,
        bool optional = false, string[]? ownedFiles = null)
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
            Discard(stagingIndexDir, ownedFiles);
            results.Add(new SyncStepResult { Name = name, Status = SyncStepResult.Failed, Detail = ex.Message });
            return false;
        }

        if (exit == 0)
        {
            results.Add(new SyncStepResult { Name = name, Status = SyncStepResult.Ok });
            return true;
        }

        if (name == "scan-git" && exit == ScanGitCommand.TicketsRefusedExitCode)
        {
            // co-change.jsonl is complete and useful; only the ticket half is missing, and scan-git already said why.
            const string detail = "co-change.jsonl written; ticket-files.jsonl not written because the ticket pattern matched nothing";
            Console.Error.WriteLine($"  {name}: partial (exit {exit}) — {detail}");
            Discard(stagingIndexDir, new[] { "ticket-files.jsonl" });
            results.Add(new SyncStepResult { Name = name, Status = SyncStepResult.Partial, Detail = detail });
            return false;
        }

        Console.Error.WriteLine(optional
            ? $"  {name}: failed (exit {exit}) — continuing, this step is optional enrichment"
            : $"  {name}: FAILED (exit {exit}) — aborting this project");
        if (optional)
        {
            Discard(stagingIndexDir, ownedFiles);
            results.Add(new SyncStepResult { Name = name, Status = SyncStepResult.Failed, Detail = $"exit {exit}" });
        }

        return false;
    }

    private static void Discard(string indexDir, string[]? files)
    {
        foreach (var file in files ?? Array.Empty<string>())
        {
            try
            {
                File.Delete(Path.Combine(indexDir, file));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"  could not remove half-written {file}: {ex.Message}");
            }
        }
    }
}

/// <summary>The five commands `sync` chains, as delegates so a test can stand in for the slow or failing ones.</summary>
internal sealed record SyncSteps(
    Func<string[], int> Scan, Func<string[], int> ScanGit, Func<string[], int> ScanFe, Func<string[], int> Link, Func<string[], int> Map)
{
    public static SyncSteps Real { get; } =
        new(ScanCommand.Run, ScanGitCommand.Run, ScanFeCommand.Run, LinkCommand.Run, MapCommand.Run);

    /// <summary>Pauses between attempts to rename into place. On Windows a directory cannot be renamed while any process holds a file in it open; a scanner or an editor usually lets go within a second.</summary>
    public IReadOnlyList<TimeSpan> PublishRetryDelays { get; init; } =
        new[] { TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(800) };
}
