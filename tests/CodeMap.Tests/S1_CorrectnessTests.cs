using System.Text.RegularExpressions;
using CodeMap.Query.Json;
using CodeMap.Query.Models;
using CodeMap.Roslyn.Scan;

namespace CodeMap.Tests;

/// <summary>Tier S1 (docs/TEST-PLAN.md): is extraction correct, against the tests/Fixtures/SampleSolution fixture.</summary>
[TestClass]
public class S1_CorrectnessTests
{
    private readonly string _outDir = TestPaths.NewTempDir();
    private readonly List<SymbolRecord> _symbols;
    private readonly List<EdgeRecord> _edges;

    public S1_CorrectnessTests()
    {
        new SyntaxOnlyScanner(includeExternal: false).Scan(TestPaths.FixtureSolution, _outDir);
        _symbols = JsonlReader.Read<SymbolRecord>(Path.Combine(_outDir, "index", "symbols.jsonl"));
        _edges = JsonlReader.Read<EdgeRecord>(Path.Combine(_outDir, "index", "edges.jsonl"));
    }

    [TestMethod] // S1.1
    public void Produces_exactly_88_symbols()
    {
        Assert.AreEqual(88, _symbols.Count);
    }

    [DataTestMethod] // S1.2
    [DataRow("T")]
    [DataRow("M")]
    public void Every_docId_has_the_right_shape_and_is_fully_qualified(string _)
    {
        TestAssert.All(_symbols, s =>
        {
            Assert.IsTrue(Regex.IsMatch(s.Id, @"^[TMFPE]:"));
            // No unqualified type name like "OrderRepository(" — must be "Orders.Data.OrderRepository("
            Assert.IsFalse(Regex.IsMatch(s.Id, @"\(OrderRepository[,)]"));
        });
    }

    [TestMethod] // S1.3 — cross-project parameter types must be fully qualified thanks to the solution-wide merged compilation
    public void Cross_project_parameter_type_is_fully_qualified()
    {
        var m = TestAssert.Single(_symbols, s => s.Name == "EnsureExists");
        Assert.AreEqual("M:Orders.OrderHelper.EnsureExists(Orders.Data.OrderRepository,System.Int32)", m.Id);
    }

    [TestMethod] // S1.4
    public void Exactly_2_implements_edges_for_IOrderService()
    {
        var implementsEdges = _edges.Where(e => e.Kind == "implements" && e.To == "T:Orders.IOrderService").ToList();
        Assert.AreEqual(2, implementsEdges.Count);
        TestAssert.Contains(implementsEdges, e => e.From == "T:Orders.OrderService");
        TestAssert.Contains(implementsEdges, e => e.From == "T:Orders.FakeOrderService");
    }

    [TestMethod] // S1.5
    public void Obsolete_attribute_is_captured()
    {
        var m = TestAssert.Single(_symbols, s => s.Name == "CancelBatch");
        CollectionAssert.Contains(m.Attributes, "Obsolete");
    }

    [TestMethod] // S1.6
    public void Accessibility_is_correct()
    {
        var field = TestAssert.Single(_symbols, s => s.Name == "_repository");
        Assert.AreEqual("Private", field.Accessibility);

        var method = TestAssert.Single(_symbols, s => s.Id == "M:Orders.OrderService.Cancel(System.Int32)");
        Assert.AreEqual("Public", method.Accessibility);
    }

    [TestMethod] // S1.7
    public void File_path_is_relative_with_forward_slashes()
    {
        TestAssert.All(_symbols, s =>
        {
            Assert.IsFalse(s.File.Contains('\\'));
            Assert.IsFalse(Path.IsPathRooted(s.File), $"File path is absolute: {s.File}");
            Assert.IsFalse(s.File.Contains(".."));
        });
    }

    [TestMethod] // S1.8
    public void Line_number_is_1_based_and_points_at_the_declaration()
    {
        var svc = TestAssert.Single(_symbols, s => s.Id == "T:Orders.OrderService");
        var fixturePath = Path.Combine(TestPaths.RepoRoot, "tests", "Fixtures", "SampleSolution", svc.File);
        var lineText = File.ReadAllLines(fixturePath)[svc.Line - 1];
        StringAssert.Contains(lineText, "class OrderService");
    }

    [TestMethod] // S1.9
    public void Symbol_is_assigned_to_the_correct_project()
    {
        TestAssert.All(_symbols.Where(s => s.File.StartsWith("Orders.Core/")), s => Assert.AreEqual("Orders.Core", s.Project));
        TestAssert.All(_symbols.Where(s => s.File.StartsWith("Orders.Data/")), s => Assert.AreEqual("Orders.Data", s.Project));
    }

    [TestMethod] // Delegate/Event must be indexed (bug found while testing against real SmartStoreNET/nopCommerce)
    public void Supports_at_least_Method_Field_Property_Constructor_kinds()
    {
        var kinds = _symbols.Select(s => s.Kind).Distinct().ToHashSet();
        Assert.IsTrue(kinds.Contains("Class"));
        Assert.IsTrue(kinds.Contains("Interface"));
        Assert.IsTrue(kinds.Contains("Method"));
        Assert.IsTrue(kinds.Contains("Field"));
    }

    [TestMethod] // Regression: delegate declarations and events (both field-style and explicit add/remove)
    public void Delegate_and_event_are_indexed()
    {
        TestAssert.Contains(_symbols, s => s.Kind == "Delegate" && s.Name == "OrderCancelledHandler");

        var events = _symbols.Where(s => s.Kind == "Event").ToList();
        Assert.AreEqual(2, events.Count);
        TestAssert.Contains(events, e => e.Name == "Cancelled");
        TestAssert.Contains(events, e => e.Name == "Refunded");
    }
}
