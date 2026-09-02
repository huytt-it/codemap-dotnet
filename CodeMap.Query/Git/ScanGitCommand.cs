using System.Text.RegularExpressions;
using CodeMap.Query.ArgParsing;
using CodeMap.Query.Config;
using CodeMap.Query.Json;

namespace CodeMap.Query.Git;

/// <summary>
/// `codemap scan-git` — spec section 5, "Thu thập dữ liệu git". No Roslyn, no LibGit2Sharp: just `git log` via
/// Process. Cheapest command in the tool and the highest value for the effort, since it catches relationships
/// static analysis is structurally blind to (reflection, MediatR, FE/BE boundary, stored procedures).
/// </summary>
internal static class ScanGitCommand
{
    // .cshtml/.razor belong here for the same reason .html does: they are edited as part of the same unit of
    // work as the C# behind them, and leaving them out erased the entire edit history of every Razor Page and
    // view — the dominant UI style in the codebases this tool targets.
    private static readonly HashSet<string> AllowedExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        { ".cs", ".cshtml", ".razor", ".ts", ".js", ".html", ".sql", ".json", ".config" };

    /// <summary>
    /// Spec section 5 set this to 50 to drop three things: merge commits, mass renames, and repo-wide
    /// reformatting. GitLogRunner now handles the first two structurally — a merge is the unit of work rather
    /// than noise, and a rename is recognised as a rename — leaving only bulk reformatting for a size cut to
    /// catch. Measured on eShopOnWeb's integration units: p95 is 47 files and p98 is 69, so 50 was discarding
    /// 4% of history including ordinary large pull requests. 100 drops 0.8% — the genuine outliers, up to 241
    /// files — and keeps the rest.
    /// </summary>
    private const int NoiseFileThreshold = 100;
    private const int MinCoChangeTogether = 3;
    private const int TicketPatternProbeCommitCount = 200;

    public static int Run(string[] rawArgs)
    {
        var args = Args.Parse(rawArgs, options: new[] { "repo", "out", "since" }, flags: Array.Empty<string>());
        var repoPath = Path.GetFullPath(args.Require("repo"));
        var outDir = args.Require("out");
        var since = args.GetOrDefault("since");

        if (!Directory.Exists(repoPath))
        {
            Console.Error.WriteLine($"Repo path not found: {repoPath}");
            return 1;
        }

        List<GitCommit> commits;
        try
        {
            commits = GitLogRunner.RunLog(repoPath, since);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        if (commits.Count == 0)
        {
            Console.Error.WriteLine("No commits found (check --since, or that --repo is a git repository with history).");
            return 1;
        }

        CodeMapConfig config;
        try
        {
            config = CodeMapConfig.Load(repoPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        var ticketPattern = new Regex(config.EffectiveTicketPattern);

        // Spec: if the pattern matches nothing in the first 200 commits, this repo's commit convention almost
        // certainly differs from the default — stop and ask for codemap.config.json instead of writing an empty file.
        var probe = commits.Take(TicketPatternProbeCommitCount).ToList();
        if (!probe.Any(c => ticketPattern.IsMatch(c.Message)))
        {
            Console.Error.WriteLine(
                $"No ticket ID matched in the first {probe.Count} commit(s) using pattern '{config.EffectiveTicketPattern}'. " +
                $"This repo's commit convention likely differs from the default — set \"ticketPattern\" in {CodeMapConfig.FileName}. " +
                "Refusing to write an empty ticket-files.jsonl.");
            return 1;
        }

        var indexDir = Path.Combine(outDir, "index");
        Directory.CreateDirectory(indexDir);

        // Before extracting anything: put git's repo-root-relative paths on the same footing as the Roslyn
        // scan's solution-relative ones, or the exact-string join in ImpactEngine/WhereEngine matches nothing.
        var solutionPrefix = GitPathRebaser.ReadSolutionPrefix(indexDir);
        if (solutionPrefix != null)
        {
            commits = commits
                .Select(c => c with { Files = c.Files.Select(f => GitPathRebaser.Rebase(solutionPrefix, f)).ToList() })
                .ToList();
            Console.WriteLine($"Solution is at '{solutionPrefix}/' inside the repo — rebasing git paths onto it.");
        }

        var tickets = TicketExtractor.Extract(commits, ticketPattern, NoiseFileThreshold, AllowedExtensions);
        var coChanges = CoChangeCalculator.Compute(commits, NoiseFileThreshold, AllowedExtensions, MinCoChangeTogether);

        JsonlWriter.Write(Path.Combine(indexDir, "ticket-files.jsonl"), tickets);
        JsonlWriter.Write(Path.Combine(indexDir, "co-change.jsonl"), coChanges);

        Console.WriteLine(
            $"scan-git done: {commits.Count} commit(s) scanned, {tickets.Count} ticket(s), {coChanges.Count} co-change pair(s).");
        Console.WriteLine($"Output: {indexDir}");

        WarnIfNothingJoinsToTheIndex(indexDir, tickets);
        return 0;
    }

    /// <summary>
    /// Both files this command writes are joined to the rest of the index by exact file-path string match. A
    /// join that matches nothing raises no error and produces no empty file — `where` just quietly drops to its
    /// two weakest sources. symbols.jsonl is already on disk, so check the join here, while the cause (a wrong
    /// --repo, a solution scanned from a different root, a case-mismatched path) is still in front of the user.
    /// </summary>
    private static void WarnIfNothingJoinsToTheIndex(string indexDir, List<Models.TicketFileRecord> tickets)
    {
        var symbolsPath = Path.Combine(indexDir, "symbols.jsonl");
        if (!File.Exists(symbolsPath))
        {
            Console.WriteLine("Note: symbols.jsonl not found, so the file paths just written could not be checked against the scan. Run `codemap scan` first for that check.");
            return;
        }

        var scannedFiles = JsonlReader.Read<Models.SymbolRecord>(symbolsPath)
            .Select(s => s.File)
            .ToHashSet(StringComparer.Ordinal);
        if (scannedFiles.Count == 0 || tickets.Count == 0) return;

        if (tickets.Any(t => t.Files.Any(scannedFiles.Contains))) return;

        Console.Error.WriteLine(
            $"WARNING: none of the {tickets.Count} ticket(s) touch a file that `codemap scan` indexed. Ticket history and " +
            "co-change data will be invisible to `where` and `impact`. Usual cause: --repo points at a different " +
            "repository than the scanned solution. Check that meta.json's solutionPath sits inside --repo.");
    }
}
