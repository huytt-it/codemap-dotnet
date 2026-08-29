using CodeMap.Query.FrontendScan;
using CodeMap.Query.Impact;
using CodeMap.Query.Json;
using CodeMap.Query.Models;

namespace CodeMap.Tests;

/// <summary>
/// Review Fix Pass v1, "nối FE thiếu 1 hop" — scan-fe finds HTTP calls inside Angular services via the
/// receiver-name heuristic, but a service isn't what a user sees; the components that inject it are. Tests the
/// one-level constructor-injection resolution added to ts-call-scan.js. Requires node + a resolvable
/// typescript package (see TypeScriptCallScanner) — skips (Inconclusive) rather than fails when unavailable,
/// same reasoning as ScanFeAndLinkIntegrationTests.
/// </summary>
[TestClass]
public class InjectedByTests
{
    private static string _indexDir = null!;

    [ClassInitialize]
    public static void ClassInit(TestContext _)
    {
        var tsOutcome = TypeScriptCallScanner.Scan(TestPaths.FixtureFrontendWithService);
        if (tsOutcome.SkippedReason != null)
            Assert.Inconclusive($"TypeScript scanning unavailable in this environment: {tsOutcome.SkippedReason}");

        var outDir = TestPaths.NewTempDir();
        ScanFeCommand.Run(new[] { "--root", TestPaths.FixtureFrontendWithService, "--out", outDir });
        _indexDir = Path.Combine(outDir, "index");
    }

    [TestMethod] // the literal fixture requirement: 1 service, 2 components inject it -> both appear
    public void Service_call_injected_by_two_components_lists_both()
    {
        var calls = JsonlReader.Read<FrontendCall>(Path.Combine(_indexDir, "frontend-calls.jsonl"));
        var call = calls.Single(c => c.File.Contains("order-api.service.ts", StringComparison.Ordinal));

        CollectionAssert.AreEquivalent(new[] { "OrderListComponent", "OrderAdminComponent" }, call.InjectedBy);
    }

    [TestMethod]
    public void Resolved_service_call_is_not_flagged_in_diagnostics()
    {
        var diagnostics = JsonUtil.ReadFile<DiagnosticsModel>(Path.Combine(_indexDir, "diagnostics.json"))!;
        Assert.IsFalse(diagnostics.UnresolvedFrontendInjections.Any(u => u.ServiceFile.Contains("order-api.service.ts", StringComparison.Ordinal)));
    }

    [TestMethod] // the other half: nobody injects ReportApiService directly — must not be guessed, must be logged
    public void Service_call_with_no_injecting_component_has_empty_injectedBy_and_is_flagged()
    {
        var calls = JsonlReader.Read<FrontendCall>(Path.Combine(_indexDir, "frontend-calls.jsonl"));
        var call = calls.Single(c => c.File.Contains("report-api.service.ts", StringComparison.Ordinal));
        Assert.AreEqual(0, call.InjectedBy.Count);

        var diagnostics = JsonUtil.ReadFile<DiagnosticsModel>(Path.Combine(_indexDir, "diagnostics.json"))!;
        Assert.IsTrue(diagnostics.UnresolvedFrontendInjections.Any(u => u.ServiceFile.Contains("report-api.service.ts", StringComparison.Ordinal)));
    }

    [TestMethod] // Regression: a call directly inside an @Component needs no resolution and must NOT be flagged (uses the pre-existing SampleFrontend fixture, unrelated to this one)
    public void Call_directly_inside_a_component_is_not_flagged_as_unresolved()
    {
        var tsOutcome = TypeScriptCallScanner.Scan(TestPaths.FixtureFrontend);
        if (tsOutcome.SkippedReason != null) Assert.Inconclusive(tsOutcome.SkippedReason);

        var outDir = TestPaths.NewTempDir();
        ScanFeCommand.Run(new[] { "--root", TestPaths.FixtureFrontend, "--out", outDir });
        var diagnostics = JsonUtil.ReadFile<DiagnosticsModel>(Path.Combine(outDir, "index", "diagnostics.json"))!;

        Assert.AreEqual(0, diagnostics.UnresolvedFrontendInjections.Count);
    }

    [TestMethod]
    public void Compact_renderer_shows_component_names_instead_of_just_the_service_file()
    {
        var result = BuildResultWithScreen(new ReachedScreen(
            "orders", "src/app/orders/order-api.service.ts", 12, "DELETE", "api/orders/{*}", "high", "exact", "M:Api.OrdersController.Delete",
            InjectedByComponents: new() { "OrderListComponent", "OrderAdminComponent" }));

        var markdown = CompactRenderer.Render(result, full: false, meta: null);

        StringAssert.Contains(markdown, "OrderListComponent, OrderAdminComponent");
        StringAssert.Contains(markdown, "(service: src/app/orders/order-api.service.ts:12)");
    }

    [TestMethod]
    public void Compact_renderer_falls_back_to_the_file_when_injectedBy_is_empty()
    {
        var result = BuildResultWithScreen(new ReachedScreen(
            "orders", "src/app/orders/order-list.component.ts", 13, "DELETE", "api/orders/{*}", "high", "exact", "M:Api.OrdersController.Delete",
            InjectedByComponents: new()));

        var markdown = CompactRenderer.Render(result, full: false, meta: null);

        StringAssert.Contains(markdown, "src/app/orders/order-list.component.ts:13");
        Assert.IsFalse(markdown.Contains("(service:"));
    }

    private static ImpactResult BuildResultWithScreen(ReachedScreen screen) => new()
    {
        SymbolId = "M:Target",
        DisplayName = "Target",
        File = "x.cs",
        Line = 1,
        DirectFanIn = 0,
        DepthScanned = 3,
        IsHub = false,
        RiskScore = 0,
        ViaInterfaceCount = 0,
        ViaMediatrCount = 0,
        EntryPoints = new(),
        Screens = new() { screen },
        TestsReached = new(),
        RelatedTickets = new(),
        CoChangingFiles = new(),
        BlindSpots = new(),
        IntermediateCallers = new(),
        ModuleFanIn = new(),
        Predecessors = new(),
    };
}
