using CodeMap.Query.Impact;
using CodeMap.Query.Models;

namespace CodeMap.Tests;

/// <summary>spec section 7: when IsHub, the flat entry-point list must be replaced by the hub warning — "Liệt kê 200 dòng không giúp gì cho người đọc lẫn cho AI".</summary>
[TestClass]
public class CompactRendererTests
{
    [TestMethod]
    public void Hub_result_renders_the_warning_instead_of_a_flat_entry_point_list()
    {
        var entryPoints = Enumerable.Range(0, 35)
            .Select(i => new ReachedEntryPoint($"M:Caller{i}", $"Caller{i}", "handler", null, null, "Proj.A", 1))
            .ToList();

        var result = BuildResult(isHub: true, entryPoints);
        var markdown = CompactRenderer.Render(result, full: false, meta: null);

        StringAssert.Contains(markdown, "⚠ This symbol is a hub");
        StringAssert.Contains(markdown, "Blast radius is system-wide");
        Assert.IsFalse(markdown.Contains("### Proj.A"), "flat per-module entry point listing must not appear in hub mode");
    }

    [TestMethod]
    public void Full_flag_shows_the_flat_list_even_when_over_the_hub_threshold()
    {
        var entryPoints = Enumerable.Range(0, 35)
            .Select(i => new ReachedEntryPoint($"M:Caller{i}", $"Caller{i}", "handler", null, null, "Proj.A", 1))
            .ToList();

        var result = BuildResult(isHub: true, entryPoints);
        var markdown = CompactRenderer.Render(result, full: true, meta: null);

        StringAssert.Contains(markdown, "### Proj.A (35)");
        Assert.IsFalse(markdown.Contains("⚠ This symbol is a hub"));
    }

    [TestMethod]
    public void Non_hub_result_lists_entry_points_grouped_by_project()
    {
        var entryPoints = new List<ReachedEntryPoint>
        {
            new("M:A", "A", "http", "GET", "api/a", "Proj.A", 1),
            new("M:B", "B", "job", null, null, "Proj.B", 2),
        };

        var result = BuildResult(isHub: false, entryPoints);
        var markdown = CompactRenderer.Render(result, full: false, meta: null);

        StringAssert.Contains(markdown, "## Affected entry points (2)");
        StringAssert.Contains(markdown, "GET");
        StringAssert.Contains(markdown, "api/a");
    }

    [TestMethod] // "sửa renderer (ưu tiên binding thật)" — docs/BENCHMARK-INTERFACE-EXPANSION.md
    public void Unconfirmed_interface_binding_entry_points_render_in_a_separate_deprioritized_section()
    {
        var entryPoints = new List<ReachedEntryPoint>
        {
            new("M:Confirmed", "Confirmed", "http", "GET", "api/a", "Proj.A", 1, IsConfirmedBinding: true),
            new("M:Unconfirmed", "Unconfirmed", "http", "GET", "api/b", "Proj.A", 1, IsConfirmedBinding: false),
        };

        var result = BuildResult(isHub: false, entryPoints);
        var markdown = CompactRenderer.Render(result, full: false, meta: null);

        StringAssert.Contains(markdown, "## Affected entry points (1)"); // headline count excludes the unconfirmed one
        StringAssert.Contains(markdown, "### Other possible implementations (1)");
        StringAssert.Contains(markdown, "Confirmed");
        StringAssert.Contains(markdown, "Unconfirmed");
        Assert.IsTrue(
            markdown.IndexOf("## Affected entry points", StringComparison.Ordinal) <
            markdown.IndexOf("Other possible implementations", StringComparison.Ordinal),
            "the confirmed section must render before the unconfirmed one");
    }

    [TestMethod]
    public void All_entry_points_confirmed_renders_no_other_possible_implementations_section()
    {
        var entryPoints = new List<ReachedEntryPoint> { new("M:A", "A", "http", "GET", "api/a", "Proj.A", 1) };

        var result = BuildResult(isHub: false, entryPoints);
        var markdown = CompactRenderer.Render(result, full: false, meta: null);

        Assert.IsFalse(markdown.Contains("Other possible implementations"));
    }

    [TestMethod]
    public void Header_states_what_direct_fan_in_counts()
    {
        var result = BuildResult(isHub: false, new List<ReachedEntryPoint>(), directFanIn: 3, directCallersInferred: 1);

        var markdown = CompactRenderer.Render(result, full: false, meta: null);

        StringAssert.Contains(markdown, "Direct fan-in: 3 distinct caller(s)");
        StringAssert.Contains(markdown, "1 only through interface/MediatR/override expansion");
    }

    [TestMethod]
    public void Header_does_not_mention_expansion_when_every_caller_is_written_in_code()
    {
        var result = BuildResult(isHub: false, new List<ReachedEntryPoint>(), directFanIn: 3, directCallersInferred: 0);

        var markdown = CompactRenderer.Render(result, full: false, meta: null);

        StringAssert.Contains(markdown, "Direct fan-in: 3 distinct caller(s)");
        Assert.IsFalse(markdown.Contains("only through interface/MediatR expansion"));
    }

    [TestMethod]
    public void Risk_line_counts_the_confirmed_part_and_names_what_was_left_out()
    {
        var entryPoints = new List<ReachedEntryPoint>
        {
            new("M:Ok", "Ok", "http", "GET", "api/a", "Proj.A", 1),
            new("M:Maybe", "Maybe", "http", "GET", "api/b", "Proj.A", 1, IsConfirmedBinding: false),
        };

        var result = BuildResult(isHub: false, entryPoints, viaInterface: 3, viaInterfaceUnconfirmed: 2);
        var markdown = CompactRenderer.Render(result, full: false, meta: null);

        StringAssert.Contains(markdown, "1 entry point(s) · 0 test(s) · 1 interface hop(s)");
        StringAssert.Contains(markdown, "Not counted in the score");
        StringAssert.Contains(markdown, "1 entry point(s), 2 interface hop(s)");
    }

    [TestMethod]
    public void Risk_section_has_no_left_out_note_when_everything_is_confirmed()
    {
        var entryPoints = new List<ReachedEntryPoint> { new("M:Ok", "Ok", "http", "GET", "api/a", "Proj.A", 1) };

        var markdown = CompactRenderer.Render(BuildResult(isHub: false, entryPoints, viaInterface: 2), full: false, meta: null);

        StringAssert.Contains(markdown, "1 entry point(s) · 0 test(s) · 2 interface hop(s)");
        Assert.IsFalse(markdown.Contains("Not counted in the score"));
    }

    [TestMethod]
    public void Hub_warning_counts_confirmed_entry_points_and_still_mentions_the_unconfirmed_ones()
    {
        var entryPoints = Enumerable.Range(0, 35)
            .Select(i => new ReachedEntryPoint($"M:Ok{i}", $"Ok{i}", "handler", null, null, "Proj.A", 1))
            .Concat(Enumerable.Range(0, 3).Select(i => new ReachedEntryPoint($"M:Maybe{i}", $"Maybe{i}", "handler", null, null, "Proj.B", 1, IsConfirmedBinding: false)))
            .ToList();

        var markdown = CompactRenderer.Render(BuildResult(isHub: true, entryPoints), full: false, meta: null);

        StringAssert.Contains(markdown, "Reaches 35 entry point(s) across 1 module(s)");
        StringAssert.Contains(markdown, "3 more entry point(s)");
        StringAssert.Contains(markdown, "--full");
    }

    [TestMethod] // a suffix link is a guess about a base URL the scanner could not see; it must not read like an exact one
    public void Screen_linked_by_route_suffix_is_marked()
    {
        var entryPoints = new List<ReachedEntryPoint> { new("M:A", "A", "http", "GET", "api/orders", "Proj.A", 1) };
        var screens = new List<ReachedScreen> { new("orders", "a.ts", 3, "GET", "orders", "high", "suffix", "M:A", new()) };

        var markdown = CompactRenderer.Render(BuildResult(isHub: false, entryPoints, screens: screens), full: false, meta: null);

        StringAssert.Contains(markdown, "(matched by route suffix only)");
    }

    [TestMethod] // no attribute declared it: it came from {controller}/{action}/{id?}, and the app's route table may say otherwise
    public void Entry_point_with_an_inferred_route_is_marked()
    {
        var entryPoints = new List<ReachedEntryPoint> { new("M:A", "A", "http", "ANY", "orders/index/{id?}", "Proj.A", 1, RouteInferred: true) };

        var markdown = CompactRenderer.Render(BuildResult(isHub: false, entryPoints), full: false, meta: null);

        StringAssert.Contains(markdown, "route inferred from convention");
    }

    [TestMethod] // scan leaves the route null when a template could not be resolved to a constant
    public void Http_entry_point_without_a_resolved_route_says_so()
    {
        var entryPoints = new List<ReachedEntryPoint> { new("M:A", "A", "http", "GET", null, "Proj.A", 1) };

        var markdown = CompactRenderer.Render(BuildResult(isHub: false, entryPoints), full: false, meta: null);

        StringAssert.Contains(markdown, "(route not resolved)");
    }

    [TestMethod]
    public void Risk_line_counts_override_hops()
    {
        var entryPoints = new List<ReachedEntryPoint> { new("M:A", "A", "handler", null, null, "Proj.A", 1) };

        var markdown = CompactRenderer.Render(BuildResult(isHub: false, entryPoints, viaOverride: 3), full: false, meta: null);

        StringAssert.Contains(markdown, "3 override hop(s)");
    }

    [TestMethod] // MCP: the tool opens with its own one-line freshness, so the markdown banner and scope note must not be here
    public void Mcp_style_has_no_banner_and_starts_at_the_heading()
    {
        var entryPoints = new List<ReachedEntryPoint> { new("M:A", "A", "http", "GET", "api/a", "Proj.A", 1) };
        var options = new RenderOptions(RenderStyle.Mcp);

        var markdown = CompactRenderer.Render(BuildResult(isHub: false, entryPoints), full: false, meta: null, options: options);

        StringAssert.StartsWith(markdown, "# Impact: Target");
        Assert.IsFalse(markdown.Contains("<!--"));
        Assert.IsFalse(markdown.Contains("Scope of this file"));
    }

    [TestMethod] // the model has no --full or --depth to pass: it has the tool's detail and depth parameters
    public void Mcp_style_names_the_tool_parameters_in_its_hints()
    {
        var options = new RenderOptions(RenderStyle.Mcp);
        var many = Enumerable.Range(0, 35)
            .Select(i => new ReachedEntryPoint($"M:Ok{i}", $"Ok{i}", "handler", null, null, "Proj.A", 1))
            .Concat(new[] { new ReachedEntryPoint("M:Maybe", "Maybe", "handler", null, null, "Proj.B", 1, IsConfirmedBinding: false) })
            .ToList();
        var hub = CompactRenderer.Render(BuildResult(isHub: true, many), full: false, meta: null, options: options);
        var unclassified = CompactRenderer.Render(
            BuildResult(isHub: false, new(), intermediateCallers: new() { new CallerNode("M:Legacy.Run", "Legacy.Run", "Legacy", 1) }),
            full: false, meta: null, options: options);
        var leaf = CompactRenderer.Render(BuildResult(isHub: false, new()), full: false, meta: null, options: options);

        StringAssert.Contains(hub, "detail=full lists them");
        StringAssert.Contains(hub, "Re-run with detail=full");
        StringAssert.Contains(unclassified, "rerun with `detail=full`");
        StringAssert.Contains(leaf, "increase depth,");
        foreach (var text in new[] { hub, unclassified, leaf })
        {
            Assert.IsFalse(text.Contains("--full"), text);
            Assert.IsFalse(text.Contains("--depth"), text);
        }
    }

    [TestMethod] // everything but the banner and those hints is shared, so the CLI's report cannot change by adding the style
    public void Cli_style_is_the_default_and_Mcp_differs_only_in_banner_and_hints()
    {
        var entryPoints = new List<ReachedEntryPoint> { new("M:A", "A", "http", "GET", "api/a", "Proj.A", 1) };
        var result = BuildResult(isHub: false, entryPoints);

        var byDefault = CompactRenderer.Render(result, full: false, meta: null);
        var cli = CompactRenderer.Render(result, full: false, meta: null, options: new RenderOptions(RenderStyle.Cli));
        var mcp = CompactRenderer.Render(result, full: false, meta: null, options: new RenderOptions(RenderStyle.Mcp));

        Assert.AreEqual(byDefault, cli);
        StringAssert.StartsWith(byDefault, "<!-- codemap v2");
        Assert.AreEqual(byDefault[byDefault.IndexOf("# Impact:", StringComparison.Ordinal)..], mcp); // no hint applies to this result
    }

    private static ImpactResult BuildResult(
        bool isHub, List<ReachedEntryPoint> entryPoints, int? directFanIn = null, int directCallersInferred = 0,
        int viaInterface = 0, int viaInterfaceUnconfirmed = 0, List<ReachedScreen>? screens = null, int viaOverride = 0,
        List<CallerNode>? intermediateCallers = null) => new()
    {
        SymbolId = "M:Target",
        DisplayName = "Target",
        File = "x.cs",
        Line = 1,
        DirectFanIn = directFanIn ?? entryPoints.Count,
        DirectCallersInferred = directCallersInferred,
        DepthScanned = 3,
        IsHub = isHub,
        RiskScore = 5,
        ViaInterfaceCount = viaInterface,
        ViaInterfaceUnconfirmedCount = viaInterfaceUnconfirmed,
        ViaMediatrCount = 0,
        ViaOverrideCount = viaOverride,
        EntryPoints = entryPoints,
        Screens = screens ?? new(),
        TestsReached = new(),
        RelatedTickets = new(),
        CoChangingFiles = new(),
        BlindSpots = new(),
        IntermediateCallers = intermediateCallers ?? new(),
        ModuleFanIn = entryPoints.Where(e => e.IsConfirmedBinding).GroupBy(e => e.Project).ToDictionary(g => g.Key, g => g.Count()),
        Predecessors = new(),
    };
}
