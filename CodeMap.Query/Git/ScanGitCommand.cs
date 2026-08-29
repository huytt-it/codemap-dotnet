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
    private static readonly HashSet<string> AllowedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".cs", ".ts", ".js", ".html", ".sql", ".json", ".config" };

    private const int NoiseFileThreshold = 50;
    private const int MinCoChangeTogether = 3;
    private const int TicketPatternProbeCommitCount = 200;

    public static int Run(string[] rawArgs)
    {
        var args = Args.Parse(rawArgs);
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

        var tickets = TicketExtractor.Extract(commits, ticketPattern, NoiseFileThreshold, AllowedExtensions);
        var coChanges = CoChangeCalculator.Compute(commits, NoiseFileThreshold, AllowedExtensions, MinCoChangeTogether);

        var indexDir = Path.Combine(outDir, "index");
        Directory.CreateDirectory(indexDir);
        JsonlWriter.Write(Path.Combine(indexDir, "ticket-files.jsonl"), tickets);
        JsonlWriter.Write(Path.Combine(indexDir, "co-change.jsonl"), coChanges);

        Console.WriteLine(
            $"scan-git done: {commits.Count} commit(s) scanned, {tickets.Count} ticket(s), {coChanges.Count} co-change pair(s).");
        Console.WriteLine($"Output: {indexDir}");
        return 0;
    }
}
