using CodeMap.Query.Impact;
using CodeMap.Query.Models;
using CodeMap.Roslyn.Scan;

namespace CodeMap.Tests;

/// <summary>
/// Review Fix Pass v1, Task 1 — docs/BENCHMARK-CODEMAP-VS-BASELINE.md's Q2 finding: "Affected entry points (0)"
/// reads as "no impact" when it actually only means "0 RECOGNIZED entry points" — real (just unclassified,
/// e.g. Razor Page) callers can still exist. Both `impact` (CompactRenderer) and `slice` (EvidenceRenderer) must
/// replace the bare "(0)" with an explicit warning whenever real callers were found. Synthetic tests isolate the
/// renderer logic; the last test proves it end to end through a real scan + a real fixture caller type
/// (LegacyOrderTrigger) that isn't a Controller, BackgroundService, or MediatR handler.
/// </summary>
[TestClass]
public class UnclassifiedEntryPointTests
{
    [TestMethod]
    public void Impact_shows_a_warning_not_a_bare_zero_when_unclassified_callers_exist()
    {
        var result = BuildResult(entryPoints: new(), intermediateCallers: new()
        {
            new CallerNode("M:Legacy.Trigger.Run", "Legacy.Trigger.Run", "Legacy", 1),
        });

        var markdown = CompactRenderer.Render(result, full: false, meta: null);

        StringAssert.Contains(markdown, "## Affected entry points: 0 recognized");
        StringAssert.Contains(markdown, "1 caller(s) were reached");
        StringAssert.Contains(markdown, "NOT the same as \"no impact\"");
        Assert.IsFalse(markdown.Contains("## Affected entry points (0)"), "must not fall back to the bare '(0)' header when real callers exist");
    }

    [TestMethod]
    public void Impact_still_shows_the_plain_leaf_message_when_there_really_are_no_callers_at_all()
    {
        var result = BuildResult(entryPoints: new(), intermediateCallers: new());

        var markdown = CompactRenderer.Render(result, full: false, meta: null);

        StringAssert.Contains(markdown, "## Affected entry points (0)");
        StringAssert.Contains(markdown, "this really is a leaf");
        Assert.IsFalse(markdown.Contains("⚠"), "no warning is warranted for a genuine leaf with 0 callers of any kind");
    }

    [TestMethod]
    public void Slice_path_shows_the_nearest_unclassified_caller_instead_of_claiming_unreachable()
    {
        var result = BuildResult(
            entryPoints: new(),
            intermediateCallers: new() { new CallerNode("M:Legacy.Trigger.Run", "Legacy.Trigger.Run", "Legacy", 1) },
            predecessors: new() { ["M:Legacy.Trigger.Run"] = "M:Target" });

        var markdown = EvidenceRenderer.Render(result, BuildEmptyIndex(), new LiveCode(true, "x.cs", 1, "void Target() {}"), meta: null);

        StringAssert.Contains(markdown, "No entry point of a known type reached");
        StringAssert.Contains(markdown, "NOT the same as \"unreachable\"");
        StringAssert.Contains(markdown, "Legacy.Trigger.Run");
        Assert.IsFalse(markdown.Contains("really is unreachable"), "must not claim unreachable when a real unclassified caller was found");
    }

    [TestMethod] // end-to-end proof, real scan + real fixture — not just synthetic renderer input
    public void Real_scan_on_a_method_called_only_by_an_unrecognized_caller_type_triggers_the_warning()
    {
        L2TestSetup.EnsureFixtureRestored();
        var outDir = TestPaths.NewTempDir();
        new SemanticScanner(includeExternal: false).Scan(TestPaths.FixtureSolution, outDir);
        var index = ImpactIndex.Load(Path.Combine(outDir, "index"));

        var result = ImpactEngine.Traverse(index, "M:Orders.Legacy.ArchiveService.Archive", depth: 5);

        Assert.AreEqual(0, result.EntryPoints.Count);
        Assert.IsTrue(result.IntermediateCallers.Count > 0, "LegacyOrderTrigger.Run must show up as a real, unclassified caller");
        TestAssert.Contains(result.IntermediateCallers, c => c.DisplayName.Contains("LegacyOrderTrigger", StringComparison.Ordinal));

        var markdown = CompactRenderer.Render(result, full: false, meta: index.Meta);
        StringAssert.Contains(markdown, "## Affected entry points: 0 recognized");
        StringAssert.Contains(markdown, "⚠");
    }

    private static ImpactResult BuildResult(
        List<ReachedEntryPoint> entryPoints, List<CallerNode> intermediateCallers, Dictionary<string, string>? predecessors = null) => new()
    {
        SymbolId = "M:Target",
        DisplayName = "Target",
        File = "x.cs",
        Line = 1,
        DirectFanIn = intermediateCallers.Count,
        DepthScanned = 3,
        IsHub = false,
        RiskScore = 0,
        ViaInterfaceCount = 0,
        ViaMediatrCount = 0,
        EntryPoints = entryPoints,
        Screens = new(),
        TestsReached = new(),
        RelatedTickets = new(),
        CoChangingFiles = new(),
        BlindSpots = new(),
        IntermediateCallers = intermediateCallers,
        ModuleFanIn = new(),
        Predecessors = predecessors ?? new(),
    };

    private static ImpactIndex BuildEmptyIndex() => new()
    {
        SymbolsById = new(),
        ReverseEdges = new(),
        EntryPointsById = new(),
        Tickets = new(),
        CoChanges = new(),
        FrontendCallsById = new(),
        ApiLinksByBackendId = new(),
        Diagnostics = null,
        Meta = null,
        ConfirmedImplementationTypes = new(),
        InterfaceCallSiteCandidateTypes = new(),
    };
}
