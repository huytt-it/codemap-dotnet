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
///
/// A second, Japanese commit covers the other realistic deployment: a team whose commit history has no spaces
/// between words. That path has to survive the same round trip (git arg encoding → UTF-8 stdout → jsonl → query
/// tokenizer), so it is pinned here end to end rather than only in `WhereEngineCjkTests`' in-memory data.
/// </summary>
[TestClass]
public class WhereEngineIntegrationTests
{
    private const string JapaneseTicketMessage =
        "TICKET-4900: 注文の削除ができない不具合を修正 - OrderRepository.Delete";

    private static ImpactIndex _index = null!;

    [ClassInitialize]
    public static void ClassInit(TestContext _)
    {
        L2TestSetup.EnsureFixtureRestored(); // registers MSBuildLocator once for the whole test host

        var repoDir = TestPaths.NewTempDir();
        CopyDirectory(Path.GetDirectoryName(TestPaths.FixtureSolution)!, repoDir);
        GitFixtureHelper.InitInPlace(repoDir);
        GitFixtureHelper.Commit(repoDir, "Fix TICKET-4821: hủy đơn hàng khi khách đã thanh toán, sửa OrderService.Cancel");

        // A second commit touching exactly one file, so this ticket maps to OrderRepository.cs alone. The edit is
        // a Japanese comment, which also proves a non-ASCII source file survives the Roslyn scan.
        GitFixtureHelper.Commit(repoDir, JapaneseTicketMessage, ("Orders.Data/OrderRepository.cs", """
            namespace Orders.Data;

            public class OrderRepository
            {
                public bool Exists(int orderId)
                {
                    return orderId > 0;
                }

                // 注文を削除する。呼び出し元は Exists で存在確認を済ませていること。
                public void Delete(int orderId)
                {
                }
            }
            """));

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

    [TestMethod]
    public void Japanese_business_query_finds_the_symbol_named_in_the_japanese_ticket()
    {
        const string query = "注文を削除できない";

        // The point of the bigram tokenizer: this query is NOT a substring of the commit message (different
        // particles, different ending), so whole-run matching — one token per space-free sentence — scores zero.
        Assert.IsFalse(JapaneseTicketMessage.Contains(query, StringComparison.Ordinal));

        var results = WhereEngine.Search(_index, query);

        var top5 = results.Take(5).Select(r => r.SymbolId).ToList();
        CollectionAssert.Contains(top5, "M:Orders.Data.OrderRepository.Delete(System.Int32)");
    }

    [TestMethod]
    public void Japanese_ticket_message_survives_the_round_trip_with_its_characters_intact()
    {
        var results = WhereEngine.Search(_index, "注文を削除できない");
        var top = results.First(r => r.SymbolId == "M:Orders.Data.OrderRepository.Delete(System.Int32)");

        var reason = top.Reasons.Single(r => r.Contains("4900", StringComparison.Ordinal));
        StringAssert.Contains(reason, "注文の削除ができない不具合を修正", "mojibake anywhere in git → jsonl → query would show up here");
    }

    [TestMethod]
    public void A_japanese_query_about_an_unrelated_feature_does_not_reach_the_delete_ticket()
    {
        // Guards the real risk of bigram matching: scoring on incidental character pairs. 在庫 (inventory) shares
        // no content bigram with the 注文/削除 ticket, so it must not surface OrderRepository at all.
        var results = WhereEngine.Search(_index, "在庫数が合わない");

        CollectionAssert.DoesNotContain(
            results.Select(r => r.SymbolId).ToList(),
            "M:Orders.Data.OrderRepository.Delete(System.Int32)");
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
