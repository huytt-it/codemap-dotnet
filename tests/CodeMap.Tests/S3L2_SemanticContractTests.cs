using System.Text.Json;
using System.Text.RegularExpressions;
using CodeMap.Query.Json;
using CodeMap.Query.Models;
using CodeMap.Roslyn.Scan;

namespace CodeMap.Tests;

/// <summary>Phase 2 contract: di.json schema (spec section 4) and edges.jsonl's `via` field, from an L2 scan.</summary>
[TestClass]
public class S3L2_SemanticContractTests
{
    private readonly string _indexDir;

    public S3L2_SemanticContractTests()
    {
        L2TestSetup.EnsureFixtureRestored();

        var outDir = TestPaths.NewTempDir();
        new SemanticScanner(includeExternal: false).Scan(TestPaths.FixtureSolution, outDir);
        _indexDir = Path.Combine(outDir, "index");
    }

    [TestMethod]
    public void Di_json_is_an_object_mapping_docId_to_an_array_of_docIds()
    {
        var raw = File.ReadAllText(Path.Combine(_indexDir, "di.json"));
        using var doc = JsonDocument.Parse(raw); // throws if not valid JSON
        Assert.AreEqual(JsonValueKind.Object, doc.RootElement.ValueKind);

        foreach (var property in doc.RootElement.EnumerateObject())
        {
            Assert.IsTrue(Regex.IsMatch(property.Name, "^T:")); // interface/type docId as key
            Assert.AreEqual(JsonValueKind.Array, property.Value.ValueKind);
            foreach (var item in property.Value.EnumerateArray())
                Assert.IsTrue(Regex.IsMatch(item.GetString()!, "^T:"));
        }
    }

    [TestMethod]
    public void Via_field_is_only_present_and_only_interface_on_expanded_edges()
    {
        var edges = JsonlReader.Read<EdgeRecord>(Path.Combine(_indexDir, "edges.jsonl"));
        TestAssert.All(edges, e => Assert.IsTrue(e.Via == null || e.Via == "interface" || e.Via == "mediatr"));
        TestAssert.Contains(edges, e => e.Via == "interface"); // the fixture's expand-via-interface case must actually fire
    }

    [TestMethod]
    public void Edge_kind_is_still_within_the_full_L2_enum()
    {
        var edges = JsonlReader.Read<EdgeRecord>(Path.Combine(_indexDir, "edges.jsonl"));
        var allowed = new[] { "call", "new", "implements", "inherits", "read", "write" };
        TestAssert.All(edges, e => Assert.IsTrue(allowed.Contains(e.Kind)));
    }
}
