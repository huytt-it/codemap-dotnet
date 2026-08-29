using System.Diagnostics;
using System.Text;

namespace CodeMap.Query.Git;

/// <summary>
/// Runs `git log --name-only` via Process (spec section 2 dependency policy: no LibGit2Sharp) and parses the
/// output into commits. Uses a control-character delimiter (\x01) ahead of each commit's header line instead of
/// relying on blank-line splitting, since blank-line behavior around `--pretty=format:` + `--name-only` is
/// finicky (no leading blank before the first commit, one between the rest).
/// </summary>
internal static class GitLogRunner
{
    private const char CommitDelimiter = '\x01';

    public static List<GitCommit> RunLog(string repoPath, string? since)
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
        psi.ArgumentList.Add("--name-only");
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
        var output = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"'git log' failed (exit {process.ExitCode}): {stderr.Trim()}");

        return ParseOutput(output);
    }

    private static List<GitCommit> ParseOutput(string output)
    {
        var commits = new List<GitCommit>();

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

            commits.Add(new GitCommit(header[0], header[1], header[2], lines.Skip(1).ToList()));
        }

        return commits;
    }
}
