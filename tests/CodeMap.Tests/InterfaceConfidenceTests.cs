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
