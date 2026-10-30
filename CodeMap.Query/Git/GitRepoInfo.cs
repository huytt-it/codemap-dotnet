using System.Diagnostics;
using System.Text;

namespace CodeMap.Query.Git;

/// <summary>
/// Read-only git queries shared by `scan`/`scan-fe` (writing meta.json's gitCommit/gitBranch at index time) and
/// every query command (spec section 7.5 staleness banner: comparing meta.json against the live repo). Every
/// method is best-effort: git not installed, not a git repo, or any other failure just returns null — git state
/// is an enrichment, never a reason to crash a scan or a query.
/// </summary>
public static class GitRepoInfo
{
    public static string? TryGetHeadCommit(string repoPath) => RunSingleLine(repoPath, "rev-parse", "HEAD");

    public static string? TryGetBranch(string repoPath) => RunSingleLine(repoPath, "rev-parse", "--abbrev-ref", "HEAD");

    public static string? TryGetRepoRoot(string repoPath) => RunSingleLine(repoPath, "rev-parse", "--show-toplevel");

    /// <summary>Files that differ between <paramref name="fromCommit"/> and <paramref name="toCommit"/>, the current HEAD unless a commit is named (spec section 7.5). Committed changes only — see <see cref="TryGetUncommittedFiles"/> for the rest. Paths are relative to the repo root.</summary>
    public static List<string>? TryGetChangedFilesSince(string repoPath, string fromCommit, string toCommit = "HEAD")
        => ParsePaths(Run(repoPath, "-c", "core.quotepath=false", "diff", "--name-only", fromCommit, toCommit));

    /// <summary>
    /// Tracked files whose staged or unstaged content differs from HEAD. A scan describes the tree as of its
    /// commit, so an edit that is not committed yet is just as stale as one that is, and HEAD alone cannot see it.
    /// Untracked files are not listed: a file the index has symbols for was tracked when it was scanned.
    /// Paths are relative to the repo root.
    /// </summary>
    public static List<string>? TryGetUncommittedFiles(string repoPath)
        => ParsePaths(Run(repoPath, "-c", "core.quotepath=false", "diff", "--name-only", "HEAD"));

    // quotepath=false: by default git wraps a path with non-ASCII bytes in quotes and octal-escapes it
    // ("\346\263\250..."), so a Japanese or Vietnamese file name would never equal the name the index recorded.
    private static List<string>? ParsePaths(string? gitOutput)
        => gitOutput?.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .ToList();

    /// <summary>How many commits <paramref name="toCommit"/> (HEAD unless named) is ahead of <paramref name="fromCommit"/> (spec section 7.5 staleness banner).</summary>
    public static int? TryGetCommitCountSince(string repoPath, string fromCommit, string toCommit = "HEAD")
    {
        var output = Run(repoPath, "rev-list", "--count", $"{fromCommit}..{toCommit}");
        return int.TryParse(output?.Trim(), out var n) ? n : null;
    }

    private static string? RunSingleLine(string repoPath, params string[] args)
    {
        var output = Run(repoPath, args)?.Trim();
        return string.IsNullOrEmpty(output) ? null : output;
    }

    private static string? Run(string repoPath, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = repoPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                // File paths (from `diff --name-only`) can be non-ASCII — same reasoning as GitLogRunner.
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var process = Process.Start(psi);
            if (process == null) return null;

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(15_000)) return null;
            _ = stderrTask.GetAwaiter().GetResult();

            return process.ExitCode == 0 ? stdoutTask.GetAwaiter().GetResult() : null;
        }
        catch
        {
            return null;
        }
    }
}
