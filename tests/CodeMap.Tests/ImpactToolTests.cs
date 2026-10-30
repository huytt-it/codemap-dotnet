using System.Text.Json;
using CodeMap.Mcp;
using CodeMap.Query.Config;
using CodeMap.Query.Git;
using CodeMap.Query.Reporting;
using ModelContextProtocol;

namespace CodeMap.Tests;

/// <summary>
/// MCP M1, <c>codemap_impact</c>: `codemap impact`'s report behind a tool call. The report itself is
/// CompactRenderer's and is tested there; what is tested here is what the tool adds: a one-line freshness in place of
/// the markdown banner, parameter-style hints, and a symbol lookup that asks only when it has to. That the report
/// really is the CLI's, byte for byte, is the protocol test in McpSpikeTests.
/// </summary>
[TestClass]
public class ImpactToolTests
{
    private const string Refund = "M:Orders.Svc.Refund";
    private const string RefundAll = "M:Orders.Svc.RefundAll";
    private const string CancelInt = "M:Orders.Svc.Cancel(System.Int32)";
    private const string CancelString = "M:Orders.Svc.Cancel(System.String)";

    [TestMethod]
    public void Opens_with_one_freshness_line_instead_of_the_markdown_banner()
    {
        var p = ImpactProject.Build();

        var lines = Lines(p.Tool().Render(Refund, null, 5, "compact"));

        Assert.AreEqual($"codemap · app · HEAD {p.Head[..7]} · index fresh", lines[0]);
        Assert.AreEqual("", lines[1]);
        StringAssert.StartsWith(lines[2], "# Impact:");
        Assert.IsFalse(string.Join("\n", lines).Contains("<!--"));
        Assert.IsTrue(string.Join("\n", lines).Contains("POST   api/orders"), "the entry point that calls Refund is listed");
    }

    [TestMethod] // the model has no --full to pass; this is the hint that tells it how to get the rest
    public void Hints_name_the_tool_parameters()
    {
        var p = ImpactProject.Build();

        var text = p.Tool().Render(RefundAll, null, 5, "compact");

        StringAssert.Contains(text, "## Affected entry points: 0 recognized");
        StringAssert.Contains(text, "`detail=full`");
        Assert.IsFalse(text.Contains("--full"));
    }

    [TestMethod] // detail=full is the CLI's --full: it adds the intermediate callers, the compact report does not have them
    public void Detail_full_adds_the_intermediate_callers()
    {
        var p = ImpactProject.Build();

        var compact = p.Tool().Render(RefundAll, null, 5, "compact");
        var full = p.Tool().Render(RefundAll, null, 5, "FULL");

        Assert.IsFalse(compact.Contains("## Intermediate callers"));
        StringAssert.Contains(full, "## Intermediate callers (1)");
        StringAssert.Contains(full, "Trigger.Run");
        Assert.ThrowsException<McpException>(() => p.Tool().Render(RefundAll, null, 5, "everything"));
    }

    [TestMethod] // the schema says 1 to 10, but a schema is a hint to the client, not a check
    public void Depth_is_clamped_to_one_through_ten()
    {
        var p = ImpactProject.Build();

        StringAssert.Contains(p.Tool().Render(Refund, null, 99, "compact"), "Depth scanned: 10");
        StringAssert.Contains(p.Tool().Render(Refund, null, 0, "compact"), "Depth scanned: 1");
    }

    [TestMethod] // only the changes to files the answer depends on count against its freshness
    public void Freshness_counts_only_the_files_the_answer_depends_on()
    {
        var p = ImpactProject.Build();
        File.WriteAllText(Path.Combine(p.Repo, "Orders", "Other.cs"), "class Other {}");

        StringAssert.EndsWith(Lines(p.Tool().Render(Refund, null, 5, "compact"))[0], "index fresh");

        File.WriteAllText(Path.Combine(p.Repo, "Orders", "Svc.cs"), "class Svc { void Refund() {} }");

        StringAssert.EndsWith(
            Lines(p.Tool().Render(Refund, null, 5, "compact"))[0],
            "at the scanned commit, 1 relevant file(s) changed (1 uncommitted)");
    }

    [TestMethod] // the name `Svc.Cancel` is ambiguous (below); a docId names exactly one overload and is not searched for
    public void A_docId_is_used_as_it_is()
    {
        var p = ImpactProject.Build();

        var text = p.Tool().Render(CancelInt, null, 5, "compact");

        StringAssert.Contains(text, "File: Orders/Svc.cs:10");
    }

    // `Refund` also matches `RefundAll`, but only as a substring: an exact name outranks it, so the model gets the
    // answer instead of a question it could only answer one way.
    [TestMethod]
    public void A_name_that_has_one_best_match_resolves_even_when_weaker_matches_exist()
    {
        var p = ImpactProject.Build();

        var text = p.Tool().Render("Refund", null, 5, "compact");

        StringAssert.Contains(text, "File: Orders/Svc.cs:30");
    }

    [TestMethod] // overloads share a name, so nothing says which one: ask, and say what the choices are
    public void Several_symbols_at_the_top_score_are_ambiguous_and_listed_with_their_locations()
    {
        var p = ImpactProject.Build();

        var ex = Assert.ThrowsException<CodeMapToolException>(() => p.Tool().Render("Cancel", null, 5, "compact"));

        Assert.AreEqual(CodeMapToolException.AmbiguousSymbol, ex.Code);
        StringAssert.StartsWith(ex.Message, "AMBIGUOUS_SYMBOL: ");
        StringAssert.Contains(ex.Message, $"- {CancelInt} — Orders/Svc.cs:10");
        StringAssert.Contains(ex.Message, $"- {CancelString} — Orders/Svc.cs:20");
        Assert.IsFalse(ex.Message.Contains("Refund"), "a weaker match is not a candidate");
    }

    [TestMethod]
    public void A_docId_that_is_not_indexed_names_the_closest_symbols_and_how_to_refresh()
    {
        var p = ImpactProject.Build();

        var ex = Assert.ThrowsException<CodeMapToolException>(
            () => p.Tool().Render("M:Orders.Svc.Refund(System.Int32)", null, 5, "compact")); // the index has Refund without parameters

        Assert.AreEqual(CodeMapToolException.SymbolNotFound, ex.Code);
        StringAssert.Contains(ex.Message, $"- {Refund} — Orders/Svc.cs:30");
        StringAssert.Contains(ex.Message, $"- {RefundAll} — Orders/Svc.cs:40");
        StringAssert.Contains(ex.Message, "codemap sync --project app");
    }

    [TestMethod]
    public void A_name_that_matches_nothing_says_so_and_points_to_text_search()
    {
        var p = ImpactProject.Build();

        var ex = Assert.ThrowsException<CodeMapToolException>(() => p.Tool().Render("Nowhere.Missing", null, 5, "compact"));

        Assert.AreEqual(CodeMapToolException.SymbolNotFound, ex.Code);
        StringAssert.Contains(ex.Message, "No similar names either.");
        StringAssert.Contains(ex.Message, "text search");
    }

    private static string[] Lines(string text) => text.TrimEnd('\n').Split('\n');

    /// <summary>root/repo (git, one commit), root/out/index (an index as of that commit), root/codemap.projects.json naming it "app".</summary>
    private sealed record ImpactProject(string Root, string Repo, string Head)
    {
        public static ImpactProject Build()
        {
            var root = TestPaths.NewTempDir();
            var repo = Path.Combine(root, "repo");
            Directory.CreateDirectory(repo);
            GitFixtureHelper.InitInPlace(repo);
            GitFixtureHelper.Commit(repo, "initial", ("App.sln", ""), ("Orders/Svc.cs", "class Svc {}"));
            var head = GitRepoInfo.TryGetHeadCommit(repo)!;

            var indexDir = Path.Combine(root, "out", "index");
            Directory.CreateDirectory(indexDir);
            RegisteredProject.WriteJsonl(Path.Combine(indexDir, "symbols.jsonl"),
                RegisteredProject.Symbol(CancelInt, "Cancel", "Orders.Svc", "Orders/Svc.cs", 10),
                RegisteredProject.Symbol(CancelString, "Cancel", "Orders.Svc", "Orders/Svc.cs", 20),
                RegisteredProject.Symbol(Refund, "Refund", "Orders.Svc", "Orders/Svc.cs", 30),
                RegisteredProject.Symbol(RefundAll, "RefundAll", "Orders.Svc", "Orders/Svc.cs", 40),
                RegisteredProject.Symbol("M:Orders.Ctl.Post", "Post", "Orders.Ctl", "Orders/Ctl.cs", 5),
                RegisteredProject.Symbol("M:Legacy.Trigger.Run", "Run", "Legacy.Trigger", "Legacy/Trigger.cs", 7));
            // Post is an HTTP entry point and calls Refund; Run is nothing CodeMap recognizes and calls RefundAll.
            RegisteredProject.WriteJsonl(Path.Combine(indexDir, "edges.jsonl"),
                new { from = "M:Orders.Ctl.Post", to = Refund, kind = "call", file = "Orders/Ctl.cs", line = 6 },
                new { from = "M:Legacy.Trigger.Run", to = RefundAll, kind = "call", file = "Legacy/Trigger.cs", line = 8 });
            File.WriteAllText(Path.Combine(indexDir, "entrypoints.json"), JsonSerializer.Serialize(new[]
            {
                new { id = "M:Orders.Ctl.Post", type = "http", httpMethod = "POST", route = "api/orders" },
            }));
            File.WriteAllText(Path.Combine(indexDir, "meta.json"), JsonSerializer.Serialize(new
            {
                indexedAt = "2026-10-30T07:59:00Z", gitCommit = head, solutionPath = "App.sln", projectCount = 1, symbolCount = 6, edgeCount = 2,
            }));

            File.WriteAllText(Path.Combine(root, ProjectRegistry.FileName), """
                { "projects": [ { "name": "app", "solution": "repo/App.sln", "output": "out" } ] }
                """);

            return new ImpactProject(root, Path.GetFullPath(repo), head);
        }

        public ImpactTool Tool()
            => new(new ToolContext(
                new ProjectResolver(null, Repo, Path.Combine(Root, "home"), Path.Combine(Root, "home")),
                new IndexCache(),
                new CommittedChangesCache()));
    }
}
