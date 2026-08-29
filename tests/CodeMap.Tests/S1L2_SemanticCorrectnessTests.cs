using CodeMap.Query.Json;
using CodeMap.Query.Models;
using CodeMap.Roslyn.Scan;

namespace CodeMap.Tests;

/// <summary>
/// Phase 2 correctness: L2 scan (MSBuildWorkspace + real SemanticModel) against tests/Fixtures/SampleSolution.
/// Covers call/new/read/write edges, di.json (both sources), and the expand-via-interface pass — spec section 5's
/// "phần quan trọng nhất".
/// </summary>
[TestClass]
public class S1L2_SemanticCorrectnessTests
{
    private readonly List<SymbolRecord> _symbols;
    private readonly List<EdgeRecord> _edges;
    private readonly Dictionary<string, List<string>> _di;
    private readonly DiagnosticsModel _diagnostics;

    public S1L2_SemanticCorrectnessTests()
    {
        L2TestSetup.EnsureFixtureRestored();

        var outDir = TestPaths.NewTempDir();
        new SemanticScanner(includeExternal: false).Scan(TestPaths.FixtureSolution, outDir);

        _symbols = JsonlReader.Read<SymbolRecord>(Path.Combine(outDir, "index", "symbols.jsonl"));
        _edges = JsonlReader.Read<EdgeRecord>(Path.Combine(outDir, "index", "edges.jsonl"));
        _di = JsonUtil.ReadFile<Dictionary<string, List<string>>>(Path.Combine(outDir, "index", "di.json")) ?? new();
        _diagnostics = JsonUtil.ReadFile<DiagnosticsModel>(Path.Combine(outDir, "index", "diagnostics.json"))!;
    }

    [TestMethod]
    public void No_project_is_degraded_once_the_fixture_is_restored()
    {
        Assert.AreEqual(0, _diagnostics.DegradedProjects.Count);
    }

    [TestMethod]
    public void Cross_project_call_edges_are_extracted()
    {
        TestAssert.Contains(_edges, e =>
            e.Kind == "call" &&
            e.From == "M:Orders.OrderHelper.EnsureExists(Orders.Data.OrderRepository,System.Int32)" &&
            e.To == "M:Orders.Data.OrderRepository.Exists(System.Int32)");

        TestAssert.Contains(_edges, e =>
            e.Kind == "call" &&
            e.From == "M:Orders.OrderService.Cancel(System.Int32)" &&
            e.To == "M:Orders.Data.OrderRepository.Delete(System.Int32)");
    }

    [TestMethod]
    public void Target_typed_new_produces_a_new_edge_from_its_own_field()
    {
        TestAssert.Contains(_edges, e =>
            e.Kind == "new" &&
            e.From == "F:Orders.OrderService._repository" &&
            e.To == "M:Orders.Data.OrderRepository.#ctor");
    }

    [TestMethod]
    public void Explicit_this_member_access_produces_read_and_write_edges()
    {
        const string prop = "P:Orders.OrderConsumer.LastCancelSucceeded";
        const string from = "M:Orders.OrderConsumer.CancelOrder(System.Int32)";

        TestAssert.Contains(_edges, e => e.Kind == "write" && e.From == from && e.To == prop);
        TestAssert.Contains(_edges, e => e.Kind == "read" && e.From == from && e.To == prop);
    }

    [TestMethod]
    public void Di_registration_call_itself_is_also_recorded_as_a_normal_call_edge()
    {
        TestAssert.Contains(_edges, e =>
            e.Kind == "call" &&
            e.From == "M:Orders.ServiceRegistration.Configure(Orders.FakeServiceCollection)" &&
            e.To.StartsWith("M:Orders.FakeServiceCollection.AddScoped", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Di_json_merges_the_structural_AllInterfaces_source_and_the_DI_registration_source()
    {
        Assert.IsTrue(_di.TryGetValue("T:Orders.IOrderService", out var impls));
        // Structural (AllInterfaces): both OrderService and FakeOrderService. DI registration (AddScoped<IOrderService, OrderService>): OrderService only.
        // Merged + deduped -> both, each appearing exactly once.
        CollectionAssert.AreEqual(new[] { "T:Orders.FakeOrderService", "T:Orders.OrderService" }, impls);
    }

    [TestMethod] // spec section 5, "Expand qua interface — phần quan trọng nhất"
    public void Call_through_an_interface_expands_to_every_implementation_and_keeps_the_original_edge()
    {
        const string from = "M:Orders.OrderConsumer.CancelOrder(System.Int32)";
        const string interfaceMember = "M:Orders.IOrderService.Cancel(System.Int32)";

        // Original edge to the interface member itself must survive.
        TestAssert.Contains(_edges, e => e.Kind == "call" && e.From == from && e.To == interfaceMember && e.Via == null);

        // Duplicated onto BOTH implementations, each marked via:"interface".
        TestAssert.Contains(_edges, e => e.Kind == "call" && e.From == from &&
            e.To == "M:Orders.OrderService.Cancel(System.Int32)" && e.Via == "interface");
        TestAssert.Contains(_edges, e => e.Kind == "call" && e.From == from &&
            e.To == "M:Orders.FakeOrderService.Cancel(System.Int32)" && e.Via == "interface");
    }
}
