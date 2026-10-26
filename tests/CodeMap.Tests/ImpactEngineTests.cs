using CodeMap.Query.Impact;
using CodeMap.Query.Models;

namespace CodeMap.Tests;

/// <summary>
/// ImpactEngine unit tests against synthetic in-memory data — no scan needed. Used specifically for the hub
/// threshold (spec section 7: "nếu số entry point đạt tới được vượt 30"), which would need 31+ real fixture
/// entry points to exercise via an actual scan; building the graph directly is far cheaper and just as valid
/// since the engine is a pure function of (index, symbolId, depth).
/// </summary>
[TestClass]
public class ImpactEngineTests
{
    [TestMethod]
    public void Direct_fan_in_counts_only_depth_1_callers()
    {
        var index = BuildIndex(
            symbols: new[] { "T:A", "T:B", "T:C", "T:D" },
            edges: new[] { ("T:B", "T:A"), ("T:C", "T:B"), ("T:D", "T:B") }, // B calls A; C and D call B
            entryPoints: Array.Empty<string>());

        var result = ImpactEngine.Traverse(index, "T:A", depth: 5);

        Assert.AreEqual(1, result.DirectFanIn); // only B calls A directly
    }

    [TestMethod]
    public void Depth_limits_how_far_the_traversal_reaches()
    {
        var index = BuildIndex(
            symbols: new[] { "T:A", "T:B", "T:C" },
            edges: new[] { ("T:B", "T:A"), ("T:C", "T:B") }, // C -> B -> A
            entryPoints: new[] { "T:C" });

        var shallow = ImpactEngine.Traverse(index, "T:A", depth: 1);
        var deep = ImpactEngine.Traverse(index, "T:A", depth: 5);

        Assert.AreEqual(0, shallow.EntryPoints.Count); // T:C is at depth 2, out of reach at depth 1
        Assert.AreEqual(1, deep.EntryPoints.Count);
    }

    [TestMethod] // spec section 7: "nếu số entry point đạt tới được vượt 30, bỏ hẳn phần liệt kê, thay bằng cảnh báo hub"
    public void More_than_30_reached_entry_points_triggers_hub_mode()
    {
        var symbols = new List<string> { "T:Target" };
        var edges = new List<(string From, string To)>();
        var entryPoints = new List<string>();

        for (var i = 0; i < 31; i++)
        {
            var callerId = $"T:Caller{i}";
            symbols.Add(callerId);
            edges.Add((callerId, "T:Target"));
            entryPoints.Add(callerId);
        }

        var index = BuildIndex(symbols, edges, entryPoints);
        var result = ImpactEngine.Traverse(index, "T:Target", depth: 3);

        Assert.AreEqual(31, result.EntryPoints.Count);
        Assert.IsTrue(result.IsHub);
    }

    [TestMethod]
    public void Exactly_30_reached_entry_points_does_not_trigger_hub_mode()
    {
        var symbols = new List<string> { "T:Target" };
        var edges = new List<(string From, string To)>();
        var entryPoints = new List<string>();

        for (var i = 0; i < 30; i++)
        {
            var callerId = $"T:Caller{i}";
            symbols.Add(callerId);
            edges.Add((callerId, "T:Target"));
            entryPoints.Add(callerId);
        }

        var index = BuildIndex(symbols, edges, entryPoints);
        var result = ImpactEngine.Traverse(index, "T:Target", depth: 3);

        Assert.AreEqual(30, result.EntryPoints.Count);
        Assert.IsFalse(result.IsHub);
    }

    [TestMethod]
    public void Project_named_like_a_test_project_is_bucketed_as_a_test_not_an_intermediate_caller()
    {
        var index = BuildIndex(
            symbols: new[] { "T:A", "T:TestMethod" },
            edges: new[] { ("T:TestMethod", "T:A") },
            entryPoints: Array.Empty<string>(),
            projectOverrides: new Dictionary<string, string> { ["T:TestMethod"] = "MyApp.Tests" });

        var result = ImpactEngine.Traverse(index, "T:A", depth: 3);

        Assert.AreEqual(1, result.TestsReached.Count);
        Assert.AreEqual(0, result.IntermediateCallers.Count);
    }

    [DataTestMethod]
    [DataRow("MyApp.Tests")]
    [DataRow("MyApp.Test")]
    [DataRow("MyApp.UnitTests")]
    [DataRow("MyApp.IntegrationTests")]
    [DataRow("OrderServiceTests")]
    [DataRow("MyApp.Tests.Common")]
    [DataRow("myapp.tests")]
    public void Project_names_with_a_test_segment_are_tests(string project)
    {
        var index = BuildIndex(
            symbols: new[] { "T:A", "T:Caller" },
            edges: new[] { ("T:Caller", "T:A") },
            entryPoints: Array.Empty<string>(),
            projectOverrides: new Dictionary<string, string> { ["T:Caller"] = project });

        var result = ImpactEngine.Traverse(index, "T:A", depth: 3);

        Assert.AreEqual(1, result.TestsReached.Count, project);
        Assert.AreEqual(0, result.IntermediateCallers.Count, project);
    }

    [DataTestMethod] // "Test" appears inside the word, not as a segment of the name
    [DataRow("Attestation")]
    [DataRow("LatestNews")]
    [DataRow("Contest.Core")]
    [DataRow("MyApp.Contests")]
    [DataRow("Orders.Core")]
    public void Project_names_that_merely_contain_the_letters_test_are_not_tests(string project)
    {
        var index = BuildIndex(
            symbols: new[] { "T:A", "T:Caller" },
            edges: new[] { ("T:Caller", "T:A") },
            entryPoints: Array.Empty<string>(),
            projectOverrides: new Dictionary<string, string> { ["T:Caller"] = project });

        var result = ImpactEngine.Traverse(index, "T:A", depth: 3);

        Assert.AreEqual(0, result.TestsReached.Count, project);
        Assert.AreEqual(1, result.IntermediateCallers.Count, project);
    }

    // git's index and the project file the Roslyn scan read can spell one path differently ("Orders/Svc.cs" vs
    // "orders/svc.cs"). Where the file system folds case they are the same file and must join; where it does not
    // they are two files and must not.
    private static bool PathsJoin => FilePathComparison.Comparison == StringComparison.OrdinalIgnoreCase;

    [TestMethod]
    public void Ticket_files_join_to_the_symbols_file_the_way_the_file_system_compares_paths()
    {
        var index = BuildIndex(
            symbols: new[] { "T:A" }, edges: Array.Empty<(string, string)>(), entryPoints: Array.Empty<string>(),
            tickets: new[] { new TicketFileRecord("ABC-1", new List<string>(), "2026-01-01", "fix", new List<string> { "T:a.cs" }) });

        var result = ImpactEngine.Traverse(index, "T:A", depth: 3);

        Assert.AreEqual(PathsJoin ? 1 : 0, result.RelatedTickets.Count);
    }

    [TestMethod]
    public void Co_change_files_join_to_the_symbols_file_the_way_the_file_system_compares_paths()
    {
        var index = BuildIndex(
            symbols: new[] { "T:A" }, edges: Array.Empty<(string, string)>(), entryPoints: Array.Empty<string>(),
            coChanges: new[] { new CoChangeRecord("T:a.cs", "other.sql", 5, 6, 7, 0.8) });

        var result = ImpactEngine.Traverse(index, "T:A", depth: 3);

        Assert.AreEqual(PathsJoin ? 1 : 0, result.CoChangingFiles.Count);
    }

    [TestMethod]
    public void Symbols_by_file_looks_up_with_the_same_comparison()
    {
        var index = BuildIndex(symbols: new[] { "T:A" }, edges: Array.Empty<(string, string)>(), entryPoints: Array.Empty<string>());

        Assert.AreEqual(PathsJoin ? 1 : 0, index.SymbolsByFile["T:a.cs"].Count());
    }

    [TestMethod]
    public void GetPathToRoot_reconstructs_the_shortest_discovered_chain()
    {
        var index = BuildIndex(
            symbols: new[] { "T:A", "T:B", "T:C" },
            edges: new[] { ("T:B", "T:A"), ("T:C", "T:B") },
            entryPoints: new[] { "T:C" });

        var result = ImpactEngine.Traverse(index, "T:A", depth: 5);
        var path = result.GetPathToRoot("T:C");

        CollectionAssert.AreEqual(new[] { "T:C", "T:B", "T:A" }, path);
    }

    [TestMethod]
    public void Unknown_symbol_id_does_not_throw_and_notes_it_in_blind_spots()
    {
        var index = BuildIndex(symbols: new[] { "T:A" }, edges: Array.Empty<(string, string)>(), entryPoints: Array.Empty<string>());

        var result = ImpactEngine.Traverse(index, "T:DoesNotExist", depth: 3);

        Assert.AreEqual(0, result.DirectFanIn);
        Assert.IsTrue(result.BlindSpots.Any(b => b.Contains("not found", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod] // spec section 7's "màn hình FE" field — joined in via api-links.jsonl, not part of the BFS itself
    public void Http_entry_point_with_a_linked_frontend_call_reports_a_screen()
    {
        var index = BuildIndex(
            symbols: new[] { "T:A", "T:Ctrl" },
            edges: new[] { ("T:Ctrl", "T:A") },
            entryPoints: new[] { "T:Ctrl" },
            entryPointType: "http",
            frontendCalls: new[] { new FrontendCall("fe:x.ts:1", "x.ts", 1, "GET", "'/api/a'", "api/a", "orders", "high", new()) },
            apiLinks: new[] { new ApiLink("fe:x.ts:1", "T:Ctrl", "exact") });

        var result = ImpactEngine.Traverse(index, "T:A", depth: 5);

        Assert.AreEqual(1, result.Screens.Count);
        Assert.AreEqual("orders", result.Screens[0].Feature);
    }

    [TestMethod]
    public void Low_confidence_screen_adds_a_blind_spot_note()
    {
        var index = BuildIndex(
            symbols: new[] { "T:A", "T:Ctrl" },
            edges: new[] { ("T:Ctrl", "T:A") },
            entryPoints: new[] { "T:Ctrl" },
            entryPointType: "http",
            frontendCalls: new[] { new FrontendCall("fe:x.js:1", "x.js", 1, "GET", "'/api/a'", "api/a", "legacy", "low", new()) },
            apiLinks: new[] { new ApiLink("fe:x.js:1", "T:Ctrl", "exact") });

        var result = ImpactEngine.Traverse(index, "T:A", depth: 5);

        Assert.IsTrue(result.BlindSpots.Any(b => b.Contains("low-confidence", StringComparison.OrdinalIgnoreCase)));
    }

    private static ImpactIndex BuildIndex(
        IEnumerable<string> symbols, IEnumerable<(string From, string To)> edges, IEnumerable<string> entryPoints,
        Dictionary<string, string>? projectOverrides = null, string entryPointType = "handler",
        IEnumerable<FrontendCall>? frontendCalls = null, IEnumerable<ApiLink>? apiLinks = null,
        IEnumerable<TicketFileRecord>? tickets = null, IEnumerable<CoChangeRecord>? coChanges = null)
    {
        var symbolsById = symbols.ToDictionary(
            id => id,
            id => new SymbolRecord
            {
                Id = id,
                Kind = "Method",
                Name = id,
                Project = projectOverrides?.GetValueOrDefault(id) ?? "TestProject",
                File = $"{id}.cs",
                Line = 1,
                Accessibility = "Public",
            },
            StringComparer.Ordinal);

        var edgeRecords = edges.Select(e => new EdgeRecord { From = e.From, To = e.To, Kind = "call", File = "x.cs", Line = 1 }).ToList();

        return new ImpactIndex
        {
            SymbolsById = symbolsById,
            ReverseEdges = edgeRecords.GroupBy(e => e.To, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal),
            EntryPointsById = entryPoints.ToDictionary(id => id, id => new EntryPoint(id, entryPointType), StringComparer.Ordinal),
            Tickets = (tickets ?? Enumerable.Empty<TicketFileRecord>()).ToList(),
            CoChanges = (coChanges ?? Enumerable.Empty<CoChangeRecord>()).ToList(),
            FrontendCallsById = (frontendCalls ?? Enumerable.Empty<FrontendCall>()).ToDictionary(c => c.Id, StringComparer.Ordinal),
            ApiLinksByBackendId = (apiLinks ?? Enumerable.Empty<ApiLink>()).GroupBy(l => l.BackendId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal),
            Diagnostics = null,
            Meta = null,
            ConfirmedImplementationTypes = new(),
            InterfaceCallSiteCandidateTypes = new(),
        };
    }
}
