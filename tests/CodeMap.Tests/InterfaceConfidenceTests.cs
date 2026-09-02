using CodeMap.Query.Impact;
using CodeMap.Query.Models;

namespace CodeMap.Tests;

/// <summary>
/// Review Fix Pass v1, "sửa renderer (ưu tiên binding thật)" — synthetic unit tests for ImpactEngine's
/// confirmed/unconfirmed classification of via:"interface" edges (docs/BENCHMARK-INTERFACE-EXPANSION.md).
/// Scenario: interface IFoo has 2 implementations reached by the same expanded call site — Impl1 (the real
/// DI-confirmed binding) and Impl2 (structural-only, no registration evidence). See
/// InterfaceExpansionConfidenceRealPipelineTests for the same scenario proven end to end through a real scan.
/// </summary>
[TestClass]
public class InterfaceConfidenceTests
{
    [TestMethod]
    public void Node_reached_via_the_confirmed_implementation_is_marked_confirmed()
    {
        var index = BuildTwoImplIndex();
        var result = ImpactEngine.Traverse(index, "M:Ns.Impl1.Method", depth: 3);

        var caller = result.IntermediateCallers.Single(c => c.Id == "M:Ns.Caller.Do");
        Assert.IsTrue(caller.IsConfirmedBinding);
    }

    [TestMethod]
    public void Node_reached_via_the_unconfirmed_sibling_implementation_is_marked_unconfirmed()
    {
        var index = BuildTwoImplIndex();
        var result = ImpactEngine.Traverse(index, "M:Ns.Impl2.Method", depth: 3);

        var caller = result.IntermediateCallers.Single(c => c.Id == "M:Ns.Caller.Do");
        Assert.IsFalse(caller.IsConfirmedBinding);
    }

    [TestMethod]
    public void Unconfirmed_interface_edge_adds_its_own_blind_spot_line()
    {
        var index = BuildTwoImplIndex();
        var result = ImpactEngine.Traverse(index, "M:Ns.Impl2.Method", depth: 3);

        Assert.IsTrue(result.BlindSpots.Any(b => b.Contains("NOT the one actually DI-bound", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Interface_with_no_di_evidence_at_all_is_not_flagged_unconfirmed()
    {
        // Same shape, but ConfirmedImplementationTypes is empty for this interface entirely (e.g. Scrutor
        // assembly-scanning registration CodeMap can't resolve statically) - "unknown" must not read as "wrong".
        var index = BuildTwoImplIndex(confirmedTypes: new());
        var result = ImpactEngine.Traverse(index, "M:Ns.Impl2.Method", depth: 3);

        var caller = result.IntermediateCallers.Single(c => c.Id == "M:Ns.Caller.Do");
        Assert.IsTrue(caller.IsConfirmedBinding);
        Assert.IsFalse(result.BlindSpots.Any(b => b.Contains("NOT the one actually DI-bound", StringComparison.Ordinal)));
    }

    /// <summary>
    /// Regression test for the BFS-order bug fixed alongside these classifications: a node is only ever
    /// discovered (and enqueued) once, so if the BFS happens to reach it FIRST through an unconfirmed path, a
    /// second, perfectly confirmed path to the same node used to be silently ignored — the result depended on
    /// which of two equally real paths got dequeued first. Here Bridge is reachable both via Q (an unconfirmed
    /// interface hop) and via P (an ordinary confirmed call) at the same depth; the edges are deliberately
    /// ordered so the unconfirmed path (through Q) is discovered first, which is exactly the ordering that
    /// mislabeled Bridge before the fix. Bridge must read as confirmed regardless: a confirmed path exists.
    /// </summary>
    [TestMethod]
    public void Node_reached_via_both_an_unconfirmed_and_a_confirmed_path_is_marked_confirmed()
    {
        var index = BuildDiamondIndex();
        var result = ImpactEngine.Traverse(index, "M:Ns.Root.Method", depth: 3);

        var bridge = result.IntermediateCallers.Single(c => c.Id == "M:Ns.Bridge.Method");
        Assert.IsTrue(bridge.IsConfirmedBinding, "Bridge has a confirmed path via P and must not be tainted just because an unconfirmed path via Q was discovered first");

        var q = result.IntermediateCallers.Single(c => c.Id == "M:Ns.Q.Method");
        Assert.IsFalse(q.IsConfirmedBinding, "Q's only path to Root is the unconfirmed interface edge");
    }

    private static ImpactIndex BuildDiamondIndex()
    {
        SymbolRecord Sym(string id, string name, string containingType) => new()
        {
            Id = id,
            Kind = "Method",
            Name = name,
            ContainingType = containingType,
            Project = "SampleProj",
            File = "x.cs",
            Line = 1,
            Accessibility = "Public",
        };

        var symbols = new[]
        {
            Sym("M:Ns.Root.Method", "Method", "Ns.RootImpl"),
            Sym("M:Ns.Q.Method", "Method", "Ns.Q"),
            Sym("M:Ns.P.Method", "Method", "Ns.P"),
            Sym("M:Ns.Bridge.Method", "Method", "Ns.Bridge"),
        };

        // Order matters here: Q's (unconfirmed) edge into Root is listed before P's (confirmed) one, so the BFS
        // discovers Root's callers in that same order — Q first — which is the ordering that exposed the bug.
        var edges = new List<EdgeRecord>
        {
            new() { From = "M:Ns.Q.Method", To = "M:Ns.Root.Method", Kind = "call", File = "q.cs", Line = 1, Via = "interface" },
            new() { From = "M:Ns.P.Method", To = "M:Ns.Root.Method", Kind = "call", File = "p.cs", Line = 1 },
            new() { From = "M:Ns.Bridge.Method", To = "M:Ns.Q.Method", Kind = "call", File = "bridge.cs", Line = 1 },
            new() { From = "M:Ns.Bridge.Method", To = "M:Ns.P.Method", Kind = "call", File = "bridge.cs", Line = 2 },
        };

        return new ImpactIndex
        {
            SymbolsById = symbols.ToDictionary(s => s.Id, StringComparer.Ordinal),
            ReverseEdges = edges.GroupBy(e => e.To, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal),
            EntryPointsById = new(),
            Tickets = new(),
            CoChanges = new(),
            FrontendCallsById = new(),
            ApiLinksByBackendId = new(),
            Diagnostics = null,
            Meta = null,
            // Q's interface call structurally could reach either RootImpl or OtherImpl; DI evidence confirms it
            // actually binds to OtherImpl, so the edge into Root (whose type is RootImpl) is a known-wrong answer.
            ConfirmedImplementationTypes = new(StringComparer.Ordinal) { "Ns.OtherImpl" },
            InterfaceCallSiteCandidateTypes = new(StringComparer.Ordinal)
            {
                ["M:Ns.Q.Method|q.cs|1"] = new() { "Ns.RootImpl", "Ns.OtherImpl" },
            },
        };
    }

    private static ImpactIndex BuildTwoImplIndex(HashSet<string>? confirmedTypes = null)
    {
        SymbolRecord Sym(string id, string name, string containingType) => new()
        {
            Id = id,
            Kind = "Method",
            Name = name,
            ContainingType = containingType,
            Project = "SampleProj",
            File = "x.cs",
            Line = 1,
            Accessibility = "Public",
        };

        var symbols = new[]
        {
            Sym("M:Ns.Caller.Do", "Do", "Ns.Caller"),
            Sym("M:Ns.Impl1.Method", "Method", "Ns.Impl1"),
            Sym("M:Ns.Impl2.Method", "Method", "Ns.Impl2"),
        };

        var edges = new List<EdgeRecord>
        {
            new() { From = "M:Ns.Caller.Do", To = "M:Ns.Impl1.Method", Kind = "call", File = "caller.cs", Line = 10, Via = "interface" },
            new() { From = "M:Ns.Caller.Do", To = "M:Ns.Impl2.Method", Kind = "call", File = "caller.cs", Line = 10, Via = "interface" },
        };

        return new ImpactIndex
        {
            SymbolsById = symbols.ToDictionary(s => s.Id, StringComparer.Ordinal),
            ReverseEdges = edges.GroupBy(e => e.To, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal),
            EntryPointsById = new(),
            Tickets = new(),
            CoChanges = new(),
            FrontendCallsById = new(),
            ApiLinksByBackendId = new(),
            Diagnostics = null,
            Meta = null,
            ConfirmedImplementationTypes = confirmedTypes ?? new(StringComparer.Ordinal) { "Ns.Impl1" },
            InterfaceCallSiteCandidateTypes = new(StringComparer.Ordinal)
            {
                ["M:Ns.Caller.Do|caller.cs|10"] = new() { "Ns.Impl1", "Ns.Impl2" },
            },
        };
    }
}
