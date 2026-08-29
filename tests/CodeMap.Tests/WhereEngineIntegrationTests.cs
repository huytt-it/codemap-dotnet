using CodeMap.Query.Git;
using CodeMap.Query.Impact;
using CodeMap.Query.Where;
using CodeMap.Roslyn.Scan;

namespace CodeMap.Tests;

/// <summary>
/// Spec section 9's Phase 5 acceptance criterion, verified through a genuinely real pipeline (not just synthetic
/// data): a real git repo, a real commit with a Vietnamese business-language message, a real L2 Roslyn scan, and
/// a real `scan-git` run — because `tests/Fixtures/SampleSolution` itself isn't tracked by this repo's own git
/// (there isn't one), a full copy is committed into a fresh temp git repo so file paths line up between the
/// Roslyn scan (solution-dir-relative) and scan-git (repo-root-relative): the copy's own root becomes both.
/// </summary>
[TestClass]
public class WhereEngineIntegrationTests
{
    private static ImpactIndex _index = null!;

    [ClassInitialize]
    public static void ClassInit(TestContext _)
    {
        L2TestSetup.EnsureFixtureRestored(); // registers MSBuildLocator once for the whole test host

        var repoDir = TestPaths.NewTempDir();
        CopyDirectory(Path.GetDirectoryName(TestPaths.FixtureSolution)!, repoDir);
        GitFixtureHelper.InitInPlace(repoDir);
        GitFixtureHelper.Commit(repoDir, "Fix TICKET-4821: hủy đơn hàng khi khách đã thanh toán, sửa OrderService.Cancel");

        RestoreSolution(repoDir);

        var outDir = TestPaths.NewTempDir();
        new SemanticScanner(includeExternal: false).Scan(Path.Combine(repoDir, "SampleSolution.sln"), outDir);

        var indexDir = Path.Combine(outDir, "index");
        var scanGitExit = ScanGitCommand.Run(new[] { "--repo", repoDir, "--out", outDir });
        Assert.AreEqual(0, scanGitExit, "scan-git should succeed against the freshly committed temp repo");

        _index = ImpactIndex.Load(indexDir);
    }

    [TestMethod] // the literal acceptance criterion, spec section 9: "tra 'hủy đơn hàng' trả về OrderService.Cancel trong 5 kết quả đầu"
    public void Vietnamese_business_query_finds_OrderService_Cancel_in_the_top_5()
    {
        var results = WhereEngine.Search(_index, "hủy đơn hàng");

        var top5 = results.Take(5).Select(r => r.SymbolId).ToList();
        CollectionAssert.Contains(top5, "M:Orders.OrderService.Cancel(System.Int32)");
    }

    [TestMethod]
    public void Top_result_names_the_matching_ticket_as_a_reason()
    {
        var results = WhereEngine.Search(_index, "hủy đơn hàng");
        var top = results.First(r => r.SymbolId == "M:Orders.OrderService.Cancel(System.Int32)");

        Assert.IsTrue(top.Reasons.Any(r => r.Contains("4821", StringComparison.Ordinal)));
    }

    private static void RestoreSolution(string repoDir)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("dotnet", "restore")
        {
            WorkingDirectory = repoDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = System.Diagnostics.Process.Start(psi)!;
        process.WaitForExit(120_000);
        Assert.AreEqual(0, process.ExitCode, "dotnet restore on the copied fixture should succeed");
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        foreach (var dir in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(dir);
            if (name is "bin" or "obj") continue;
            var relative = Path.GetRelativePath(sourceDir, dir);
            if (relative.Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj")) continue;
            Directory.CreateDirectory(Path.Combine(destDir, relative));
        }

        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, file);
            if (relative.Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj")) continue;
            var destPath = Path.Combine(destDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            File.Copy(file, destPath, overwrite: true);
        }
    }
}
