namespace CodeMap.Tests;

/// <summary>Finds the repo root (containing docs/CODEMAP-SPEC.md) from the test run directory, Debug or Release.</summary>
internal static class TestPaths
{
    public static string RepoRoot { get; } = FindRepoRoot();
    public static string FixtureSolution { get; } = Path.Combine(RepoRoot, "tests", "Fixtures", "SampleSolution", "SampleSolution.sln");
    public static string FixtureFrontend { get; } = Path.Combine(RepoRoot, "tests", "Fixtures", "SampleFrontend");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "docs", "CODEMAP-SPEC.md")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not find the repo root (docs/CODEMAP-SPEC.md) from " + AppContext.BaseDirectory);
    }

    public static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "codemap-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
