using System.Security.Cryptography;
using CodeMap.Roslyn.Scan;

namespace CodeMap.Tests;

/// <summary>Tier S4 (docs/TEST-PLAN.md): hard constraints from spec sections 1 & 10 — must never be violated, not even once.</summary>
[TestClass]
public class S4_HardConstraintTests
{
    [TestMethod] // S4.1 — "Only reads the target solution. Never modifies it, never creates files inside the indexed solution."
    public void Scan_never_writes_any_file_into_the_target_solution()
    {
        var solutionDir = Path.GetDirectoryName(TestPaths.FixtureSolution)!;
        var before = HashAllFiles(solutionDir);

        var outDir = TestPaths.NewTempDir(); // output goes OUTSIDE the target solution — the correct real-world usage
        new SyntaxOnlyScanner(false).Scan(TestPaths.FixtureSolution, outDir);

        var after = HashAllFiles(solutionDir);

        Assert.AreEqual(before.Count, after.Count); // no new files, no deleted files
        foreach (var (path, hash) in before)
            Assert.AreEqual(hash, after[path]); // no file content was modified
    }

    private static Dictionary<string, string> HashAllFiles(string root)
    {
        using var sha = SHA256.Create();
        var result = new Dictionary<string, string>();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file);
            result[rel] = Convert.ToHexString(sha.ComputeHash(File.ReadAllBytes(file)));
        }

        return result;
    }
}
