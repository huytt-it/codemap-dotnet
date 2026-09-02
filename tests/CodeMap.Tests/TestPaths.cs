namespace CodeMap.Tests;

/// <summary>Finds the repo root (containing CodeMap.slnx) from the test run directory, Debug or Release.</summary>
internal static class TestPaths
{
    public static string RepoRoot { get; } = FindRepoRoot();
    public static string FixtureSolution { get; } = Path.Combine(RepoRoot, "tests", "Fixtures", "SampleSolution", "SampleSolution.sln");
    /// <summary>The same two projects as FixtureSolution, in the .slnx format the .NET 10 SDK emits by default — one of them inside a solution folder, so parsing has to recurse.</summary>
    public static string FixtureSolutionSlnx { get; } = Path.Combine(RepoRoot, "tests", "Fixtures", "SampleSolution", "SampleSolution.slnx");
    public static string FixtureFrontend { get; } = Path.Combine(RepoRoot, "tests", "Fixtures", "SampleFrontend");
    public static string FixtureFrontendWithService { get; } = Path.Combine(RepoRoot, "tests", "Fixtures", "SampleFrontendWithService");

    private static string FindRepoRoot()
    {
        // CodeMap.slnx, not a docs/ file: docs/ is excluded from the public repo except for two specific files
        // (see .gitignore), so a marker inside it makes every test fail on a clean clone with "repo root not
        // found" instead of the real assertion failure it should be reporting.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CodeMap.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not find the repo root (CodeMap.slnx) from " + AppContext.BaseDirectory);
    }

    public static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "codemap-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
