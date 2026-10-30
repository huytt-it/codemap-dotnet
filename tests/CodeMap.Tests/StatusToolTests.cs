using System.Text.Json;
using CodeMap.Mcp;
using CodeMap.Query.Config;
using CodeMap.Query.Git;
using CodeMap.Query.Json;
using CodeMap.Query.Models;
using CodeMap.Query.Reporting;

namespace CodeMap.Tests;

/// <summary>
/// MCP M1, <c>codemap_status</c>: does CodeMap cover this workspace, and how far can its index be trusted right now.
/// It answers in about ten lines, opens with the same freshness line as every other tool, and ends with the one
/// thing to do next. Unlike the other tools it reports "not indexed" and "no index" as its answer instead of
/// failing: saying whether CodeMap applies here is the whole point of the call.
/// </summary>
[TestClass]
public class StatusToolTests
{
    [TestMethod]
    public void Fresh_synced_project_reports_where_everything_came_from_and_nothing_to_do()
    {
        var p = StatusProject.Build();

        var lines = Lines(p.Tool(workspace: p.Repo).Render(project: null));

        Assert.AreEqual($"codemap · app · HEAD {p.Head[..7]} · index fresh", lines[0]);
        StringAssert.Contains(Line(lines, "workspace"), $"{p.Repo} (from --workspace)");
        StringAssert.Contains(Line(lines, "registry"), p.Config);
        StringAssert.Contains(Line(lines, "project"), "app: Order app");
        StringAssert.Contains(Line(lines, "project"), "inside its solution folder");
        StringAssert.Contains(Line(lines, "index"), "complete `codemap sync` at 2026-10-30T08:00:00Z");
        StringAssert.Contains(Line(lines, "index"), "scan-fe skipped (no frontend configured)");
        StringAssert.Contains(Line(lines, "contents"), "2 symbol(s) · 1 edge(s) · 1 entry point(s)");
        StringAssert.Contains(Line(lines, "tickets"), "ja");
        StringAssert.Contains(Line(lines, "next"), "nothing to do");
        Assert.IsTrue(lines.Length <= 12, $"{lines.Length} lines:\n{string.Join("\n", lines)}");
    }

    [TestMethod]
    public void Index_behind_head_and_built_by_hand_asks_for_a_sync()
    {
        var p = StatusProject.Build(marker: false);
        GitFixtureHelper.Commit(p.Repo, "second", ("Orders/Ctl.cs", "class Ctl {}"));

        var lines = Lines(p.Tool(workspace: p.Repo).Render(project: null));

        StringAssert.Contains(lines[0], "1 commit(s) behind, 1 file(s) changed");
        StringAssert.Contains(Line(lines, "index"), "no sync-complete.json");
        StringAssert.Contains(Line(lines, "next"), "run `codemap sync --project app`");
    }

    [TestMethod] // "not covered" is an answer, not a failure: the agent should go on with normal search
    public void Unindexed_workspace_is_reported_not_thrown()
    {
        var p = StatusProject.Build();
        var elsewhere = TestPaths.NewTempDir();

        var text = p.Tool(workspace: elsewhere).Render(project: null);

        StringAssert.StartsWith(text, "codemap · not available for this workspace");
        StringAssert.Contains(text, $"{elsewhere} (from --workspace)");
        StringAssert.Contains(text, "NOT_INDEXED");
    }

    [TestMethod]
    public void Missing_index_names_the_project_and_the_command_that_builds_it()
    {
        var p = StatusProject.Build();
        Directory.Delete(p.IndexDir, recursive: true);

        var lines = Lines(p.Tool(workspace: p.Repo, indexes: new IndexCache(swapRetryDelays: Array.Empty<TimeSpan>())).Render(project: null));

        Assert.AreEqual("codemap · app · no index", lines[0]);
        StringAssert.Contains(Line(lines, "project"), "app");
        StringAssert.Contains(Line(lines, "index"), "INDEX_MISSING");
        StringAssert.Contains(Line(lines, "next"), "run `codemap sync --project app`");
    }

    [TestMethod] // the client did not substitute the variable: say so, or the user never learns why cwd was used
    public void Workspace_warnings_are_shown()
    {
        var p = StatusProject.Build();

        var text = p.Tool(workspace: "${workspaceFolder}", cwd: p.Repo).Render(project: null);

        StringAssert.Contains(text, "(from cwd)");
        StringAssert.Contains(text, "${workspaceFolder}");
    }

    // eShopOnWeb squash-merges PRs, so the default ticketPattern's "#123" picks up "(#123)" at the end of every
    // subject: the "tickets" are PR numbers, and `where` ranks by them without anyone noticing.
    [TestMethod]
    public void Ticket_ids_that_are_pull_request_numbers_get_a_warning()
    {
        var p = StatusProject.Build(tickets: new[]
        {
            Ticket("101", "Fix basket total (#101)"),
            Ticket("102", "Add paging (#102)"),
            Ticket("103", "Merge pull request #103 from someone/branch"),
            Ticket("104", "Rename service (#104)"),
            Ticket("105", "Fix #105: checkout crash"),
        });

        var text = p.Tool(workspace: p.Repo).Render(project: null);

        StringAssert.Contains(text, "4 of 5 ticket ID(s)");
        StringAssert.Contains(text, "pull request numbers");
        StringAssert.Contains(text, "ticketPattern");
    }

    [TestMethod]
    public void Real_ticket_ids_get_no_pull_request_warning()
    {
        var p = StatusProject.Build(tickets: new[]
        {
            Ticket("101", "Fix #101 basket total"),
            Ticket("102", "Add paging, refs #102"),
            Ticket("103", "#103 rename service"),
            Ticket("104", "Fix #104 checkout"),
            Ticket("105", "Fix #105 checkout crash"),
        });

        Assert.IsFalse(p.Tool(workspace: p.Repo).Render(project: null).Contains("pull request numbers"));
    }

    [TestMethod]
    public void Project_parameter_selects_a_project_the_workspace_does_not_map_to()
    {
        var p = StatusProject.Build();

        var lines = Lines(p.Tool(workspace: TestPaths.NewTempDir(), config: p.Config).Render(project: "app"));

        StringAssert.StartsWith(lines[0], "codemap · app · HEAD");
        StringAssert.Contains(Line(lines, "project"), "named by the project parameter");
    }

    private static TicketFileRecord Ticket(string id, string message)
        => new(id, new List<string> { "abc123" }, "2026-01-01", message, new List<string> { "Orders/Svc.cs" });

    private static string[] Lines(string text) => text.TrimEnd('\n').Split('\n');

    private static string Line(string[] lines, string label)
        => lines.FirstOrDefault(l => l.StartsWith(label + " ", StringComparison.Ordinal))
           ?? throw new AssertFailedException($"no '{label}' line in:\n{string.Join("\n", lines)}");

    /// <summary>root/repo (git, one commit), root/out/index (an index as of that commit), root/codemap.projects.json naming it "app".</summary>
    private sealed record StatusProject(string Root, string Repo, string IndexDir, string Config, string Head)
    {
        public static StatusProject Build(bool marker = true, TicketFileRecord[]? tickets = null)
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
                RegisteredProject.Symbol("M:Orders.Svc.Cancel", "Cancel", "Orders.Svc", "Orders/Svc.cs", 1),
                RegisteredProject.Symbol("M:Orders.Ctl.Post", "Post", "Orders.Ctl", "Orders/Ctl.cs", 1));
            RegisteredProject.WriteJsonl(Path.Combine(indexDir, "edges.jsonl"),
                new { from = "M:Orders.Ctl.Post", to = "M:Orders.Svc.Cancel", kind = "call", file = "Orders/Ctl.cs", line = 1 });
            File.WriteAllText(Path.Combine(indexDir, "entrypoints.json"), JsonSerializer.Serialize(new[]
            {
                new { id = "M:Orders.Ctl.Post", type = "http", httpMethod = "POST", route = "api/orders" },
            }));
            const string indexedAt = "2026-10-30T07:59:00Z";
            File.WriteAllText(Path.Combine(indexDir, "meta.json"), JsonSerializer.Serialize(new
            {
                indexedAt, gitCommit = head, solutionPath = "App.sln", projectCount = 1, symbolCount = 2, edgeCount = 1,
            }));
            if (tickets != null) JsonlWriter.Write(Path.Combine(indexDir, "ticket-files.jsonl"), tickets);
            if (marker)
                SyncMarker.Write(indexDir, new SyncMarker
                {
                    SyncId = "s1", CompletedAt = "2026-10-30T08:00:00Z", Project = "app", GitCommit = head, MetaIndexedAt = indexedAt,
                    Steps = new()
                    {
                        new() { Name = "scan", Status = SyncStepResult.Ok },
                        new() { Name = "scan-git", Status = SyncStepResult.Ok },
                        new() { Name = "scan-fe", Status = SyncStepResult.Skipped, Detail = "no frontend configured" },
                        new() { Name = "map", Status = SyncStepResult.Ok },
                    },
                });

            var config = Path.Combine(root, ProjectRegistry.FileName);
            File.WriteAllText(config, """
                { "projects": [ { "name": "app", "description": "Order app", "solution": "repo/App.sln", "output": "out", "commitLanguage": "ja" } ] }
                """);

            return new StatusProject(root, Path.GetFullPath(repo), Path.GetFullPath(indexDir), config, head);
        }

        public StatusTool Tool(string? workspace = null, string? cwd = null, string? config = null, IndexCache? indexes = null)
            => new(new ToolContext(
                new ProjectResolver(config, workspace, cwd ?? Path.Combine(Root, "home"), Path.Combine(Root, "home")),
                indexes ?? new IndexCache()));
    }
}
