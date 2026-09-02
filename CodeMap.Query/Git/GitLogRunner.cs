using System.Diagnostics;
using System.Text;

namespace CodeMap.Query.Git;

/// <summary>
/// Runs `git log` via Process (spec section 2 dependency policy: no LibGit2Sharp) and parses the output into
/// commits. Uses a control-character delimiter (\x01) ahead of each commit's header line instead of relying on
/// blank-line splitting, since blank-line behavior around `--pretty=format:` + a diff format is finicky (no
/// leading blank before the first commit, one between the rest).
///
/// Three things this asks git for beyond a plain listing, each measured on the eShopOnWeb history:
///
/// * `--first-parent --diff-merges=first-parent` — a plain `git log` emits NO file list for a merge commit, so
///   every merge silently contributed nothing. 36 of that repo's 150 tickets (23%) exist only in merge
///   messages, and a team whose ticket ID lives in the branch name ("Merge pull request #456 from
///   org/SHO_1234-fix") loses the ticket entirely. Walking the first-parent line instead counts each
///   integration exactly once, so reading merge diffs cannot double-count the branch commits underneath.
/// * `--name-status -M` — rename detection. 53% of the paths in that repo's history no longer exist, and a
///   path that no longer exists can never join to symbols.jsonl, which only holds files that do. Renames are
///   resolved to today's name below, which reconnects a file's history across the rename instead of splitting
///   it in two.
/// </summary>
internal static class GitLogRunner
{
    private const char CommitDelimiter = '\x01';

    private static readonly string[] PreferredArgs =
        { "--first-parent", "--diff-merges=first-parent", "--name-status", "-M" };

    /// <summary>`--diff-merges` arrived in git 2.31. On anything older, merge commits list no files — the old
    /// behavior — but rename detection and everything else still work.</summary>
    private static readonly string[] LegacyArgs = { "--name-status", "-M" };

    public static List<GitCommit> RunLog(string repoPath, string? since)
    {
        var (exitCode, output, stderr) = Run(repoPath, since, PreferredArgs);

        if (exitCode != 0 && LooksLikeUnsupportedOption(stderr))
        {
            Console.Error.WriteLine(
                "Note: this git is too old for --diff-merges (needs 2.31+); merge commits will contribute no files.");
            (exitCode, output, stderr) = Run(repoPath, since, LegacyArgs);
        }

        if (exitCode != 0)
            throw new InvalidOperationException($"'git log' failed (exit {exitCode}): {stderr.Trim()}");

        return ResolveRenames(ParseOutput(output));
    }

    private static bool LooksLikeUnsupportedOption(string stderr)
        => stderr.Contains("unknown option", StringComparison.OrdinalIgnoreCase)
           || stderr.Contains("unrecognized argument", StringComparison.OrdinalIgnoreCase);

    private static (int ExitCode, string Output, string Error) Run(string repoPath, string? since, string[] logArgs)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = repoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            // Commit messages can be (and per spec section 4's own example, "fix hủy đơn khi đã thanh toán", are
            // expected to be) non-ASCII. Git writes UTF-8 to stdout regardless of platform; without an explicit
            // encoding here, .NET on Windows falls back to the console/OEM codepage and mangles it.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("log");
        foreach (var a in logArgs) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add($"--pretty=format:{CommitDelimiter}%H|%ad|%s");
        psi.ArgumentList.Add("--date=short");
        if (!string.IsNullOrWhiteSpace(since))
            psi.ArgumentList.Add($"--since={since}");

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start 'git'.");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Could not run 'git' (is it installed and on PATH?): {ex.Message}", ex);
        }

        // Read both streams concurrently, not sequentially: `git log` output can be large enough to fill the
        // stdout pipe buffer, and reading it synchronously before stderr would deadlock if git also blocks on
        // writing to a full stderr buffer at the same time.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, stdoutTask.GetAwaiter().GetResult(), stderrTask.GetAwaiter().GetResult());
    }

    /// <summary>
    /// Rewrites every historical path to the name the file carries today. git log runs newest-first, so by the
    /// time a rename old -> new is read, `new` has already been through any later rename of its own — mapping
    /// `old` onto whatever `new` currently resolves to therefore chains renames correctly in a single pass.
    /// A path reused after a rename resolves correctly too: commits are rewritten as they are read, so each one
    /// sees the map as it stood at that point in history.
    /// </summary>
    private static List<GitCommit> ResolveRenames(List<RawCommit> commits)
    {
        var currentName = new Dictionary<string, string>(StringComparer.Ordinal);
        var result = new List<GitCommit>(commits.Count);

        foreach (var commit in commits)
        {
            var files = new List<string>(commit.Changes.Count);
            foreach (var change in commit.Changes)
            {
                var name = currentName.TryGetValue(change.Path, out var mapped) ? mapped : change.Path;
                files.Add(name);
                if (change.RenamedFrom != null) currentName[change.RenamedFrom] = name;
            }

            result.Add(new GitCommit(commit.Hash, commit.Date, commit.Message, files));
        }

        return result;
    }

    private static List<RawCommit> ParseOutput(string output)
    {
        var commits = new List<RawCommit>();

        foreach (var block in output.Split(CommitDelimiter, StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = block.Split('\n')
                .Select(l => l.TrimEnd('\r'))
                .Where(l => l.Length > 0)
                .ToList();
            if (lines.Count == 0) continue;

            // maxCount 3: a commit subject containing '|' must not get split further.
            var header = lines[0].Split('|', 3);
            if (header.Length < 3) continue;

            var changes = lines.Skip(1).Select(ParseChange).OfType<RawChange>().ToList();
            commits.Add(new RawCommit(header[0], header[1], header[2], changes));
        }

        return commits;
    }

    /// <summary>`--name-status` rows are tab-separated: "M\tpath", or "R096\told\tnew" for a rename or copy.</summary>
    private static RawChange? ParseChange(string line)
    {
        var parts = line.Split('\t');
        return parts.Length switch
        {
            2 => new RawChange(parts[1], null),
            // A copy (C) leaves the source in place, so it is not a rename and must not rewrite history.
            3 => new RawChange(parts[2], parts[0].StartsWith('R') ? parts[1] : null),
            _ => null,
        };
    }

    private sealed record RawChange(string Path, string? RenamedFrom);

    private sealed record RawCommit(string Hash, string Date, string Message, List<RawChange> Changes);
}
