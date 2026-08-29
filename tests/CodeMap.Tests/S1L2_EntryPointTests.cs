using CodeMap.Query.Impact;
using CodeMap.Query.Json;
using CodeMap.Query.Models;
using CodeMap.Roslyn.Scan;

namespace CodeMap.Tests;

/// <summary>
/// Phase 3, "Entry point" (spec section 5) + the engine (section 7). Fixture: OrdersController (http, route
/// param), OrderNightlyJob (job), CancelOrderHandler (handler) — all converging on OrderRepository.Exists, the
/// bottom of the 4-tier chain (Controller → Service → Helper → Repository), exactly like section 9 describes.
/// </summary>
[TestClass]
public class S1L2_EntryPointTests
{
    private static List<EntryPoint> _entryPoints = null!;
    private static List<EdgeRecord> _edges = null!;
    private static ImpactIndex _index = null!;

    [ClassInitialize]
    public static void ClassInit(TestContext _)
    {
        L2TestSetup.EnsureFixtureRestored();

        var outDir = TestPaths.NewTempDir();
        new SemanticScanner(includeExternal: false).Scan(TestPaths.FixtureSolution, outDir);

        var indexDir = Path.Combine(outDir, "index");
        _entryPoints = JsonUtil.ReadFile<List<EntryPoint>>(Path.Combine(indexDir, "entrypoints.json")) ?? new();
        _edges = JsonlReader.Read<EdgeRecord>(Path.Combine(indexDir, "edges.jsonl"));
        _index = ImpactIndex.Load(indexDir);
    }

    [TestMethod]
    public void Http_controller_action_produces_a_composed_route_with_the_controller_token_resolved()
    {
        var ep = _entryPoints.Single(e => e.Id == "M:Orders.Http.OrdersController.Delete(System.Int32)");
        Assert.AreEqual("http", ep.Type);
        Assert.AreEqual("DELETE", ep.HttpMethod);
        Assert.AreEqual("api/orders/{id}", ep.Route); // [controller] -> "orders", composed with [HttpDelete("{id}")]
    }

    [TestMethod]
    public void BackgroundService_ExecuteAsync_is_a_job_entry_point()
    {
        var ep = _entryPoints.Single(e => e.Type == "job");
        Assert.AreEqual("M:Orders.Hosting.OrderNightlyJob.ExecuteAsync(System.Threading.CancellationToken)", ep.Id);
    }

    [TestMethod]
    public void MediatR_handler_Handle_method_is_a_handler_entry_point()
    {
        var ep = _entryPoints.Single(e => e.Type == "handler");
        Assert.AreEqual("M:Orders.Mediation.CancelOrderHandler.Handle(Orders.Mediation.CancelOrderCommand)", ep.Id);
    }

    [TestMethod]
    public void Mediator_Send_with_inline_new_produces_a_call_edge_marked_via_mediatr()
    {
        var edge = _edges.Single(e => e.Via == "mediatr");
        Assert.AreEqual("M:Orders.Http.OrdersController.CancelViaMediator(Orders.Mediation.IMediator,System.Int32)", edge.From);
        Assert.AreEqual("M:Orders.Mediation.CancelOrderHandler.Handle(Orders.Mediation.CancelOrderCommand)", edge.To);
        Assert.AreEqual("call", edge.Kind);
    }

    [TestMethod] // spec section 9's fixture design: the bottom of the deep chain must be reachable from all 3 entry point kinds
    public void Bottom_of_the_deep_chain_is_reached_by_all_three_entry_point_kinds()
    {
        var result = ImpactEngine.Traverse(_index, "M:Orders.Data.OrderRepository.Exists(System.Int32)", depth: 5);

        Assert.AreEqual(4, result.EntryPoints.Count);
        CollectionAssert.AreEquivalent(
            new[] { "job", "http", "http", "handler" },
            result.EntryPoints.Select(e => e.Type).ToList());
    }
}
