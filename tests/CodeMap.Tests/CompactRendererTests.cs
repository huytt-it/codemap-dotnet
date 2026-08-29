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

    private static ImpactResult BuildResult(bool isHub, List<ReachedEntryPoint> entryPoints) => new()
    {
        SymbolId = "M:Target",
        DisplayName = "Target",
        File = "x.cs",
        Line = 1,
        DirectFanIn = entryPoints.Count,
        DepthScanned = 3,
        IsHub = isHub,
        RiskScore = 5,
        ViaInterfaceCount = 0,
        ViaMediatrCount = 0,
        EntryPoints = entryPoints,
        Screens = new(),
        TestsReached = new(),
        RelatedTickets = new(),
        CoChangingFiles = new(),
        BlindSpots = new(),
        IntermediateCallers = new(),
        ModuleFanIn = entryPoints.GroupBy(e => e.Project).ToDictionary(g => g.Key, g => g.Count()),
        Predecessors = new(),
    };
}
