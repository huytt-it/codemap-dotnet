using System.Text.Json;
using CodeMap.Query.Json;
using CodeMap.Query.Map;
using CodeMap.Query.Models;
using CodeMap.Roslyn.Scan;

namespace CodeMap.Tests;

/// <summary>Tier S3 (docs/TEST-PLAN.md): does the output match the spec section 4 schema, and is MAP.md really always ≤ 500 lines.</summary>
[TestClass]
public class S3_ContractTests
{
    [TestMethod] // S3.1 + S3.2 — valid JSONL, camelCase field names per spec section 4
    public void Symbols_and_edges_are_valid_camelCase_jsonl()
    {
        var outDir = TestPaths.NewTempDir();
        new SyntaxOnlyScanner(false).Scan(TestPaths.FixtureSolution, outDir);

        var symbolLines = File.ReadAllLines(Path.Combine(outDir, "index", "symbols.jsonl"))
            .Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        Assert.IsTrue(symbolLines.Count > 0);
        foreach (var line in symbolLines)
        {
            using var doc = JsonDocument.Parse(line); // throws if not valid JSON
            var root = doc.RootElement;
            foreach (var expected in new[] { "id", "kind", "name", "project", "file", "line", "accessibility", "attributes" })
                Assert.IsTrue(root.TryGetProperty(expected, out _), $"Missing field '{expected}' (or wrong case) in: {line}");
        }

        var edgeLines = File.ReadAllLines(Path.Combine(outDir, "index", "edges.jsonl"))
            .Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        Assert.IsTrue(edgeLines.Count > 0);
        foreach (var line in edgeLines)
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            foreach (var expected in new[] { "from", "to", "kind", "file", "line" })
                Assert.IsTrue(root.TryGetProperty(expected, out _), $"Missing field '{expected}' in: {line}");
        }
    }

    [TestMethod] // S3.3
    public void Edge_kind_is_always_within_the_allowed_enum()
    {
        var outDir = TestPaths.NewTempDir();
        new SyntaxOnlyScanner(false).Scan(TestPaths.FixtureSolution, outDir);

        var edges = JsonlReader.Read<EdgeRecord>(Path.Combine(outDir, "index", "edges.jsonl"));
        var allowed = new[] { "call", "new", "implements", "inherits", "read", "write" };
        TestAssert.All(edges, e => Assert.IsTrue(allowed.Contains(e.Kind)));
    }

    [TestMethod] // S3.4 — diagnostics.json always exists and is valid JSON, even when empty
    public void Diagnostics_json_always_exists_and_is_valid()
    {
        var outDir = TestPaths.NewTempDir();
        new SyntaxOnlyScanner(false).Scan(TestPaths.FixtureSolution, outDir);

        var path = Path.Combine(outDir, "index", "diagnostics.json");
        Assert.IsTrue(File.Exists(path));
        var diag = JsonUtil.ReadFile<DiagnosticsModel>(path);
        Assert.IsNotNull(diag);
    }

    [TestMethod] // S3.5 — HARD constraint: MAP.md ≤ 500 lines, even with thousands of unresolved-inheritance entries (simulating a big repo)
    public void MapMd_never_exceeds_500_lines_even_with_thousands_of_blind_spots()
    {
        var symbols = new List<SymbolRecord>();
        var edges = new List<EdgeRecord>();
        var diagnostics = new DiagnosticsModel();

        // Simulates what was actually observed on SmartStoreNET: 642 unresolved base types once pushed MAP.md
        // to 714 lines. Here we build 2000 entries across 400 distinct base type names to make sure this test
        // doesn't "just barely pass" by coincidentally landing under some threshold.
        for (var i = 0; i < 2000; i++)
        {
            diagnostics.UnresolvedInheritance.Add(new UnresolvedInheritance(
                $"Proj{i % 50}", $"src/File{i}.cs", i, $"T:Proj{i % 50}.From{i}", $"BaseType{i % 400}", "simulated reason"));
        }
        for (var i = 0; i < 300; i++)
            diagnostics.DegradedProjects.Add(new DegradedProject($"Proj{i}", "simulated reason"));

        var generator = new MapGenerator(symbols, edges, diagnostics);
        var markdown = generator.BuildMapMarkdown(preservedHumanBlock: null);
        var lineCount = markdown.Split('\n').Length;

        Assert.IsTrue(lineCount <= 500, $"MAP.md has {lineCount} lines, exceeding the spec section 8 hard constraint of 500.");
    }

    [TestMethod] // S3.6 — hand-written notes between human:start/end survive a regenerate
    public void Hand_written_notes_survive_regenerate()
    {
        var indexDir = TestPaths.NewTempDir();
        new SyntaxOnlyScanner(false).Scan(TestPaths.FixtureSolution, indexDir);

        var mapOutDir = TestPaths.NewTempDir();
        MapCommand.Run(new[] { "--index", Path.Combine(indexDir, "index"), "--out", mapOutDir });

        var mapPath = Path.Combine(mapOutDir, "MAP.md");
        var original = File.ReadAllText(mapPath);
        var withNote = original.Replace(
            "(hand-written notes go here — preserved across regenerate)",
            "USER-OWNED NOTE - MUST SURVIVE REGENERATE");
        File.WriteAllText(mapPath, withNote);

        MapCommand.Run(new[] { "--index", Path.Combine(indexDir, "index"), "--out", mapOutDir });

        var regenerated = File.ReadAllText(mapPath);
        StringAssert.Contains(regenerated, "USER-OWNED NOTE - MUST SURVIVE REGENERATE");
    }

    [TestMethod] // spec section 7.5 — every markdown artifact opens with the staleness banner
    public void MapMd_opens_with_the_staleness_banner()
    {
        var indexDir = TestPaths.NewTempDir();
        new SyntaxOnlyScanner(false).Scan(TestPaths.FixtureSolution, indexDir);

        var mapOutDir = TestPaths.NewTempDir();
        MapCommand.Run(new[] { "--index", Path.Combine(indexDir, "index"), "--out", mapOutDir });

        var content = File.ReadAllText(Path.Combine(mapOutDir, "MAP.md"));
        StringAssert.StartsWith(content, "<!-- codemap v2 ·");
        StringAssert.Contains(content, "Scope of this file");
    }

    [TestMethod] // S3.7 — modules/ produces exactly 1 file per project
    public void Modules_produces_exactly_1_file_per_project()
    {
        var indexDir = TestPaths.NewTempDir();
        new SyntaxOnlyScanner(false).Scan(TestPaths.FixtureSolution, indexDir);

        var mapOutDir = TestPaths.NewTempDir();
        MapCommand.Run(new[] { "--index", Path.Combine(indexDir, "index"), "--out", mapOutDir });

        var moduleFiles = Directory.GetFiles(Path.Combine(mapOutDir, "modules"), "*.md");
        Assert.AreEqual(2, moduleFiles.Length); // Orders.Core.md, Orders.Data.md
        TestAssert.Contains(moduleFiles, f => Path.GetFileName(f) == "Orders.Core.md");
        TestAssert.Contains(moduleFiles, f => Path.GetFileName(f) == "Orders.Data.md");
    }
}
