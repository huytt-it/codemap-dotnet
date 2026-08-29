using CodeMap.Query.Map;
using CodeMap.Query.Models;

namespace CodeMap.Tests;

/// <summary>Phase 6, "map bản đầy đủ" (spec section 8): the entry point table grouped by project, with FE screens joined in via api-links.jsonl — the one piece MAP.md was still missing after Phase 1-5 (impact/slice already had it, MAP.md didn't).</summary>
[TestClass]
public class MapGeneratorTests
{
    [TestMethod]
    public void Entry_points_are_grouped_by_project_with_linked_fe_screens_listed()
    {
        var symbols = new List<SymbolRecord>
        {
            Sym("M:Api.OrdersController.Delete(System.Int32)", "Delete", "Api.OrdersController", "Api"),
            Sym("M:Jobs.NightlyJob.ExecuteAsync(System.Threading.CancellationToken)", "ExecuteAsync", "Jobs.NightlyJob", "Jobs"),
        };
        var entryPoints = new List<EntryPoint>
        {
            new("M:Api.OrdersController.Delete(System.Int32)", "http", "DELETE", "api/orders/{id}"),
            new("M:Jobs.NightlyJob.ExecuteAsync(System.Threading.CancellationToken)", "job"),
        };
        var frontendCalls = new List<FrontendCall> { new("fe:x.ts:1", "x.ts", 1, "DELETE", "'/x'", "api/orders/{*}", "orders", "high", new()) };
        var apiLinks = new List<ApiLink> { new("fe:x.ts:1", "M:Api.OrdersController.Delete(System.Int32)", "exact") };

        var generator = new MapGenerator(symbols, new List<EdgeRecord>(), diagnostics: null, entryPoints: entryPoints, frontendCalls: frontendCalls, apiLinks: apiLinks);
        var markdown = generator.BuildMapMarkdown(preservedHumanBlock: null);

        StringAssert.Contains(markdown, "## Entry Points (2)");
        StringAssert.Contains(markdown, "### Api (1)");
        StringAssert.Contains(markdown, "DELETE api/orders/{id}");
        StringAssert.Contains(markdown, "orders"); // the linked FE feature
        StringAssert.Contains(markdown, "### Jobs (1)");
    }

    [TestMethod]
    public void Entry_point_with_no_linked_fe_call_shows_a_dash()
    {
        var symbols = new List<SymbolRecord> { Sym("M:Jobs.NightlyJob.ExecuteAsync(System.Threading.CancellationToken)", "ExecuteAsync", "Jobs.NightlyJob", "Jobs") };
        var entryPoints = new List<EntryPoint> { new("M:Jobs.NightlyJob.ExecuteAsync(System.Threading.CancellationToken)", "job") };

        var generator = new MapGenerator(symbols, new List<EdgeRecord>(), diagnostics: null, entryPoints: entryPoints);
        var markdown = generator.BuildMapMarkdown(preservedHumanBlock: null);

        StringAssert.Contains(markdown, "| job | - | Jobs.NightlyJob.ExecuteAsync(System.Threading.CancellationToken) | - |");
    }

    [TestMethod]
    public void No_entrypoints_json_shows_an_explicit_none_message_not_an_empty_section()
    {
        var generator = new MapGenerator(new List<SymbolRecord>(), new List<EdgeRecord>(), diagnostics: null);
        var markdown = generator.BuildMapMarkdown(preservedHumanBlock: null);

        StringAssert.Contains(markdown, "## Entry Points (0)");
        StringAssert.Contains(markdown, "run `codemap scan` first");
    }

    [TestMethod] // extends S3_ContractTests' S3.5 stress test to the new section specifically
    public void MapMd_never_exceeds_500_lines_even_with_thousands_of_entry_points()
    {
        var symbols = new List<SymbolRecord>();
        var entryPoints = new List<EntryPoint>();
        for (var p = 0; p < 50; p++)
        {
            for (var i = 0; i < 40; i++)
            {
                var id = $"M:Proj{p}.Controller{p}.Action{i}(System.Int32)";
                symbols.Add(Sym(id, $"Action{i}", $"Proj{p}.Controller{p}", $"Proj{p}"));
                entryPoints.Add(new EntryPoint(id, "http", "GET", $"api/proj{p}/action{i}"));
            }
        }

        var generator = new MapGenerator(symbols, new List<EdgeRecord>(), diagnostics: null, entryPoints: entryPoints);
        var markdown = generator.BuildMapMarkdown(preservedHumanBlock: null);
        var lineCount = markdown.Split('\n').Length;

        Assert.IsTrue(lineCount <= 500, $"MAP.md has {lineCount} lines, exceeding the spec section 8 hard constraint of 500.");
    }

    private static SymbolRecord Sym(string id, string name, string containingType, string project) => new()
    {
        Id = id,
        Kind = "Method",
        Name = name,
        ContainingType = containingType,
        Project = project,
        File = "x.cs",
        Line = 1,
        Accessibility = "Public",
    };
}
