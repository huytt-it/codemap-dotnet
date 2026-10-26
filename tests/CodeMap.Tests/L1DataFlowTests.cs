using CodeMap.Query.Impact;
using CodeMap.Query.Json;
using CodeMap.Query.Models;
using CodeMap.Roslyn.Scan;

namespace CodeMap.Tests;

/// <summary>
/// `scan --syntax-only` (L1) must feed impact the same kinds of data L2 does — call/new/read/write edges, entry
/// points, DI — not just declarations plus inherits/implements. The merged L1 compilation still binds calls
/// between the solution's own types, which is everything these assertions need. Mirrors the L2 assertions in
/// S1L2_EntryPointTests against the same fixture.
/// </summary>
[TestClass]
public class L1DataFlowTests
{
    private static string _indexDir = null!;
    private static List<EdgeRecord> _edges = null!;

    [ClassInitialize]
    public static void ClassInit(TestContext _)
    {
        var outDir = TestPaths.NewTempDir();
        new SyntaxOnlyScanner(includeExternal: false).Scan(TestPaths.FixtureSolution, outDir);

        _indexDir = Path.Combine(outDir, "index");
        _edges = JsonlReader.Read<EdgeRecord>(Path.Combine(_indexDir, "edges.jsonl"));
    }

    [TestMethod]
    public void Call_edges_are_emitted_at_L1_including_the_interface_expansion()
    {
        const string from = "M:Orders.Http.OrdersController.Delete(System.Int32)";
        TestAssert.Contains(_edges, e => e.From == from && e.To == "M:Orders.IOrderService.Cancel(System.Int32)" && e.Kind == "call" && e.Via == null);
        TestAssert.Contains(_edges, e => e.From == from && e.To == "M:Orders.OrderService.Cancel(System.Int32)" && e.Kind == "call" && e.Via == "interface");
    }

    [TestMethod]
    public void New_read_and_write_edges_are_emitted_at_L1()
    {
        TestAssert.Contains(_edges, e => e.Kind == "new" && e.To == "M:Orders.Mediation.CancelOrderCommand.#ctor");
        TestAssert.Contains(_edges, e => e.Kind == "read" && e.To == "P:Orders.Mediation.CancelOrderCommand.OrderId");
        TestAssert.Contains(_edges, e => e.Kind == "write" && e.To == "P:Orders.OrderConsumer.LastCancelSucceeded");
    }

    [TestMethod]
    public void Entry_points_are_written_at_L1()
    {
        var entryPoints = JsonUtil.ReadFile<List<EntryPoint>>(Path.Combine(_indexDir, "entrypoints.json"));
        Assert.IsNotNull(entryPoints, "entrypoints.json is missing");

        var http = TestAssert.Single(entryPoints, e => e.Id == "M:Orders.Http.OrdersController.Delete(System.Int32)");
        Assert.AreEqual("http", http.Type);
        Assert.AreEqual("DELETE", http.HttpMethod);
        Assert.AreEqual("api/orders/{id}", http.Route);
        TestAssert.Contains(entryPoints, e => e.Type == "job" && e.Id == "M:Orders.Hosting.OrderNightlyJob.ExecuteAsync(System.Threading.CancellationToken)");
        TestAssert.Contains(entryPoints, e => e.Type == "handler" && e.Id == "M:Orders.Mediation.CancelOrderHandler.Handle(Orders.Mediation.CancelOrderCommand)");
    }

    [TestMethod]
    public void Mediator_Send_produces_a_via_mediatr_call_edge_at_L1()
    {
        var edge = TestAssert.Single(_edges, e => e.Via == "mediatr");
        Assert.AreEqual("M:Orders.Http.OrdersController.CancelViaMediator(Orders.Mediation.IMediator,System.Int32)", edge.From);
        Assert.AreEqual("M:Orders.Mediation.CancelOrderHandler.Handle(Orders.Mediation.CancelOrderCommand)", edge.To);
    }

    [TestMethod]
    public void Di_registrations_are_written_at_L1()
    {
        var confirmed = JsonUtil.ReadFile<Dictionary<string, List<string>>>(Path.Combine(_indexDir, "di-confirmed.json"));
        Assert.IsNotNull(confirmed, "di-confirmed.json is missing");
        CollectionAssert.Contains(confirmed["T:Orders.IOrderService"], "T:Orders.OrderService"); // fluent AddScoped
        CollectionAssert.Contains(confirmed["T:Orders.IPricingService"], "T:Orders.PricingService"); // [Injectable] via codemap.config.json

        Assert.IsTrue(File.Exists(Path.Combine(_indexDir, "di.json")), "di.json is missing");
    }

    [TestMethod] // the end-to-end symptom: before this, impact at L1 found no caller at all for anything
    public void Bottom_of_the_deep_chain_is_reached_by_all_three_entry_point_kinds_at_L1()
    {
        var result = ImpactEngine.Traverse(ImpactIndex.Load(_indexDir), "M:Orders.Data.OrderRepository.Exists(System.Int32)", depth: 5);

        CollectionAssert.AreEquivalent(
            new[] { "job", "http", "http", "handler" },
            result.EntryPoints.Select(e => e.Type).ToList());
    }
}
