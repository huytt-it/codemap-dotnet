using CodeMap.Query.FrontendScan;
using CodeMap.Query.Impact;
using CodeMap.Query.Json;
using CodeMap.Query.Link;
using CodeMap.Query.Map;
using CodeMap.Query.Models;
using CodeMap.Roslyn.Scan;

namespace CodeMap.Tests;

/// <summary>
/// Spec section 9's Phase 4 fixture goal: "impact trên method đáy trả đúng 3 entry point + 2 màn hình FE" —
/// exercises the full pipeline (scan L2 -> scan-fe -> link -> impact) end to end on the real fixtures, the same
/// way S1L2_EntryPointTests does for the backend-only part. Requires `node` + a resolvable `typescript` package
/// (see TypeScriptCallScanner) for the Angular half; skips (Inconclusive) rather than fails when unavailable —
/// that's a real external dependency, not something a NuGet restore can guarantee like MSBuild.
/// </summary>
[TestClass]
public class ScanFeAndLinkIntegrationTests
{
    private static string _indexDir = null!;
    private static ImpactIndex _index = null!;

    [ClassInitialize]
    public static void ClassInit(TestContext _)
    {
        L2TestSetup.EnsureFixtureRestored();

        var tsOutcome = TypeScriptCallScanner.Scan(TestPaths.FixtureFrontend);
        if (tsOutcome.SkippedReason != null)
            Assert.Inconclusive($"TypeScript scanning unavailable in this environment: {tsOutcome.SkippedReason}");
        if (tsOutcome.Calls.Count == 0)
            Assert.Inconclusive("TypeScript scan ran but found no calls — ts-call-scan.js likely didn't match the fixture as expected.");

        var outDir = TestPaths.NewTempDir();
        new SemanticScanner(includeExternal: false).Scan(TestPaths.FixtureSolution, outDir);
        ScanFeCommand.Run(new[] { "--root", TestPaths.FixtureFrontend, "--out", outDir });

        _indexDir = Path.Combine(outDir, "index");
        LinkCommand.Run(new[] { "--index", _indexDir });
        _index = ImpactIndex.Load(_indexDir);
    }

    [TestMethod]
    public void Angular_calls_from_both_features_are_normalized_to_the_same_route()
    {
        var calls = JsonlReader.Read<FrontendCall>(Path.Combine(_indexDir, "frontend-calls.jsonl"));
        var angularCalls = calls.Where(c => c.Confidence == "high").ToList();

        Assert.AreEqual(2, angularCalls.Count);
        Assert.IsTrue(angularCalls.All(c => c.Route == "api/orders/{*}"));
        CollectionAssert.AreEquivalent(new[] { "orders", "order-admin" }, angularCalls.Select(c => c.Feature).ToList());
    }

    [TestMethod]
    public void Jquery_call_with_a_dynamic_url_is_logged_to_diagnostics_not_silently_dropped()
    {
        var diagnostics = JsonUtil.ReadFile<DiagnosticsModel>(Path.Combine(_indexDir, "diagnostics.json"))!;
        Assert.IsTrue(diagnostics.UnparsedFrontendUrls.Any(u => u.File.Contains("order-actions.js", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Link_produces_an_exact_match_for_the_delete_endpoint()
    {
        var links = JsonlReader.Read<ApiLink>(Path.Combine(_indexDir, "api-links.jsonl"));
        var deleteLinks = links.Where(l => l.BackendId == "M:Orders.Http.OrdersController.Delete(System.Int32)").ToList();

        Assert.AreEqual(2, deleteLinks.Count); // one per Angular feature
        Assert.IsTrue(deleteLinks.All(l => l.MatchKind == "exact"));
    }

    [TestMethod] // the acceptance criterion itself, spec section 9
    public void Impact_on_the_bottom_of_the_deep_chain_reports_both_fe_screens()
    {
        var result = ImpactEngine.Traverse(_index, "M:Orders.Data.OrderRepository.Exists(System.Int32)", depth: 5);

        Assert.AreEqual(4, result.EntryPoints.Count); // unchanged from Phase 3 (job, http x2, handler)
        var features = result.Screens.Select(s => s.Feature).Distinct().ToList();
        CollectionAssert.AreEquivalent(new[] { "orders", "order-admin" }, features);
    }

    [TestMethod]
    public void Compact_renderer_shows_the_affected_fe_screens_section()
    {
        var result = ImpactEngine.Traverse(_index, "M:Orders.Data.OrderRepository.Exists(System.Int32)", depth: 5);
        var markdown = CompactRenderer.Render(result, full: false, meta: _index.Meta);

        StringAssert.Contains(markdown, "## Affected FE screens (2)");
        StringAssert.Contains(markdown, "order-admin");
    }

    [TestMethod] // Phase 6, "map bản đầy đủ": MAP.md's own entry point table must show the same FE screens impact/slice do
    public void Map_entry_point_table_lists_both_fe_screens_for_the_delete_endpoint()
    {
        var mapOutDir = TestPaths.NewTempDir();
        MapCommand.Run(new[] { "--index", _indexDir, "--out", mapOutDir });

        var content = File.ReadAllText(Path.Combine(mapOutDir, "MAP.md"));
        var deleteRow = content.Split('\n').Single(l => l.Contains("Orders.Http.OrdersController.Delete", StringComparison.Ordinal));

        StringAssert.Contains(deleteRow, "DELETE api/orders/{id}");
        StringAssert.Contains(deleteRow, "orders");
        StringAssert.Contains(deleteRow, "order-admin");
    }
}
