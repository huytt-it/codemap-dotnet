using System.Diagnostics;

namespace CodeMap.Tests;

/// <summary>Builds a small real git repo in a temp dir, one commit at a time, for scan-git tests.</summary>
internal static class GitFixtureHelper
{
    public static string NewRepo()
    {
        var dir = TestPaths.NewTempDir();
        InitInPlace(dir);
        return dir;
    }

    /// <summary>Same `git init` + identity config as NewRepo(), but for a directory that already has files in it (e.g. a copy of a fixture solution) instead of an empty temp dir.</summary>
    public static void InitInPlace(string dir)
    {
        RunGit(dir, "init", "-q");
        RunGit(dir, "config", "user.email", "test@codemap.local");
        RunGit(dir, "config", "user.name", "CodeMap Test");
    }

    public static void Commit(string repoDir, string message, params (string FileName, string Content)[] files)
    {
        foreach (var (fileName, content) in files)
        {
            var path = Path.Combine(repoDir, fileName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        RunGit(repoDir, "add", "-A");
        RunGit(repoDir, "commit", "-q", "-m", message);
    }

    /// <summary>Commits N throwaway files at once, to trigger the "bulk commit" noise filter (spec: commits touching more than 50 files).</summary>
    public static void CommitManyFiles(string repoDir, string message, int fileCount)
    {
        var files = Enumerable.Range(0, fileCount)
            .Select(i => ($"bulk/File{i}.cs", $"class File{i} {{}}"))
            .ToArray();
        Commit(repoDir, message, files);
    }

    private static void RunGit(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit(30_000);

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderrTask.GetAwaiter().GetResult()}");
        _ = stdoutTask.GetAwaiter().GetResult();
    }
}
