using CodeMap.Query.Impact;
using CodeMap.Roslyn.Scan;

namespace CodeMap.Tests;

/// <summary>
/// Review Fix Pass v1, "sửa renderer (ưu tiên binding thật)" — proves the confirmed/unconfirmed split end to
/// end on the exact real-world shape docs/BENCHMARK-INTERFACE-EXPANSION.md's audit found: the fixture's
/// Orders.Core/ServiceRegistration.cs registers ONLY `services.AddScoped&lt;IOrderService, OrderService&gt;()` —
/// FakeOrderService also implements IOrderService (S1.4) but has no registration anywhere. OrdersController.Delete
/// calls through the IOrderService-typed field, so interface-expand produces a via:"interface" edge to BOTH.
/// </summary>
[TestClass]
public class InterfaceExpansionConfidenceRealPipelineTests
{
    private static ImpactIndex _index = null!;

    [ClassInitialize]
    public static void ClassInit(TestContext _)
    {
        L2TestSetup.EnsureFixtureRestored();
        var outDir = TestPaths.NewTempDir();
        new SemanticScanner(includeExternal: false).Scan(TestPaths.FixtureSolution, outDir);
        _index = ImpactIndex.Load(Path.Combine(outDir, "index"));
    }

    [TestMethod]
    public void Real_di_bound_implementation_is_reached_as_confirmed()
    {
        var result = ImpactEngine.Traverse(_index, "M:Orders.OrderService.Cancel(System.Int32)", depth: 5);

        var ep = result.EntryPoints.SingleOrDefault(e => e.DisplayName.Contains("OrdersController.Delete", StringComparison.Ordinal));
        Assert.IsNotNull(ep, "OrdersController.Delete should reach OrderService.Cancel");
        Assert.IsTrue(ep!.IsConfirmedBinding);
    }

    [TestMethod] // the exact false positive docs/BENCHMARK-INTERFACE-EXPANSION.md's audit found — no explicit registration anywhere for FakeOrderService
    public void Never_registered_sibling_implementation_is_reached_as_unconfirmed()
    {
        var result = ImpactEngine.Traverse(_index, "M:Orders.FakeOrderService.Cancel(System.Int32)", depth: 5);

        var ep = result.EntryPoints.SingleOrDefault(e => e.DisplayName.Contains("OrdersController.Delete", StringComparison.Ordinal));
        Assert.IsNotNull(ep, "OrdersController.Delete should still reach FakeOrderService.Cancel via interface-expand");
        Assert.IsFalse(ep!.IsConfirmedBinding);
    }

    [TestMethod] // taint must propagate transitively: CancelViaMediator reaches FakeOrderService.Cancel.Cancel ONLY through
    // CancelOrderHandler.Handle's unconfirmed interface hop — the mediatr edge itself isn't the problem, but the
    // whole path depends on that earlier unconfirmed hop being real
    public void Unconfirmed_status_propagates_transitively_through_a_confirmed_edge_reached_via_an_unconfirmed_predecessor()
    {
        var result = ImpactEngine.Traverse(_index, "M:Orders.FakeOrderService.Cancel(System.Int32)", depth: 5);

        var handler = result.EntryPoints.Single(e => e.DisplayName.Contains("CancelOrderHandler.Handle", StringComparison.Ordinal));
        Assert.IsFalse(handler.IsConfirmedBinding, "Handle itself is the unconfirmed interface hop");

        var viaMediator = result.EntryPoints.Single(e => e.DisplayName.Contains("CancelViaMediator", StringComparison.Ordinal));
        Assert.IsFalse(viaMediator.IsConfirmedBinding, "reached only via mediatr FROM the unconfirmed Handle — the taint must propagate forward");
    }

    [TestMethod]
    public void Compact_renderer_puts_the_never_registered_implementation_reach_under_full_in_the_unconfirmed_note()
    {
        var result = ImpactEngine.Traverse(_index, "M:Orders.FakeOrderService.Cancel(System.Int32)", depth: 5);
        var markdown = CompactRenderer.Render(result, full: true, meta: _index.Meta);

        StringAssert.Contains(markdown, "Other possible implementations");
    }
}
