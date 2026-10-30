using System.Diagnostics;
using CodeMap.Query.Git;
using CodeMap.Query.Json;
using CodeMap.Query.Models;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace CodeMap.Tests;

/// <summary>
/// Protocol layer: starts the real `codemap mcp` as a child process and talks to it over stdio with the SDK's own
/// client. The client would fail to parse anything else written to stdout, so every passing test here also shows
/// the protocol channel stays clean. Every tool gets at least one call here, because some failures only exist in
/// the server process (git used to hang on the protocol pipe, and no unit test could see it).
/// </summary>
[TestClass]
public class McpProtocolTests
{
    [TestMethod]
    public async Task Status_tool_is_declared_read_only_and_carries_the_scope_note()
    {
        var layout = Layout.Create();
        await using var client = await StartAsync(layout.Elsewhere, "--config", layout.Registry);

        var tool = (await client.ListToolsAsync()).Single(t => t.Name == "codemap_status");
        var annotations = tool.ProtocolTool.Annotations!;

        Assert.AreEqual(true, annotations.ReadOnlyHint);
        Assert.AreEqual(false, annotations.DestructiveHint);
        Assert.AreEqual(true, annotations.IdempotentHint);
        Assert.AreEqual(false, annotations.OpenWorldHint);
        StringAssert.Contains(tool.Description, "does not cover reflection"); // the banner's scope note moved here
    }

    [TestMethod]
    public async Task Impact_tool_is_declared_read_only_and_tells_the_model_when_to_prefer_text_search()
    {
        var layout = Layout.Create();
        await using var client = await StartAsync(layout.Elsewhere, "--config", layout.Registry);

        var tool = (await client.ListToolsAsync()).Single(t => t.Name == "codemap_impact");
        var annotations = tool.ProtocolTool.Annotations!;

        Assert.AreEqual(true, annotations.ReadOnlyHint);
        Assert.AreEqual(false, annotations.DestructiveHint);
        Assert.AreEqual(true, annotations.IdempotentHint);
        Assert.AreEqual(false, annotations.OpenWorldHint);
        StringAssert.Contains(tool.Description, "Use this instead of text search for callers and impact");
        StringAssert.Contains(tool.Description, "Use text search for comments, string literals and config values");
        StringAssert.Contains(tool.ProtocolTool.InputSchema.GetRawText(), "\"required\":[\"symbol\"]");
    }

    [TestMethod] // the real server process, its real arguments, and an unrelated cwd
    public async Task Status_over_stdio_resolves_the_project_from_the_workspace_argument()
    {
        var layout = Layout.Create();
        await using var client = await StartAsync(layout.Elsewhere, "--config", layout.Registry, "--workspace", layout.Repo);

        var result = await client.CallToolAsync("codemap_status", new Dictionary<string, object?>());
        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));

        Assert.AreNotEqual(true, result.IsError);
        StringAssert.StartsWith(text, "codemap · app · ");
        StringAssert.Contains(text, "(from --workspace)");
        StringAssert.Contains(text, "1 symbol(s)");
    }

    // git used to inherit the server's stdin, which is the JSON-RPC pipe the transport keeps a read pending on. On
    // Windows every other operation on that handle queues behind the read, so git hung until the next client message
    // or GitRepoInfo's 15 s timeout, and the freshness line said git could not be run.
    [TestMethod]
    public async Task Git_runs_inside_the_server_without_waiting_on_the_protocol_pipe()
    {
        var layout = Layout.Create(git: true);
        await using var client = await StartAsync(layout.Elsewhere, "--config", layout.Registry, "--workspace", layout.Repo);

        var stopwatch = Stopwatch.StartNew();
        var result = await client.CallToolAsync("codemap_status", new Dictionary<string, object?>());
        stopwatch.Stop();
        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));

        StringAssert.StartsWith(text, $"codemap · app · HEAD {layout.Head![..7]} · index fresh");
        Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"codemap_status took {stopwatch.Elapsed.TotalSeconds:0.0} s");
    }

    // The protocol test for codemap_impact: the real server on a real git repo (the path that hung before git got its
    // own stdin), and the real CLI as the reference. Anything the tool adds or drops in the report would show here.
    [TestMethod]
    public async Task Impact_over_stdio_is_the_cli_report_with_its_banner_replaced_by_the_freshness_line()
    {
        var layout = Layout.Create(git: true, callGraph: true);
        await using var client = await StartAsync(layout.Elsewhere, "--config", layout.Registry, "--workspace", layout.Repo);

        foreach (var full in new[] { false, true })
        {
            var result = await client.CallToolAsync("codemap_impact", new Dictionary<string, object?>
            {
                ["symbol"] = Layout.CancelId,
                ["detail"] = full ? "full" : "compact",
            });
            var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text)).Replace("\r\n", "\n");
            var cli = RunCliImpact(layout, Layout.CancelId, full).Replace("\r\n", "\n");

            Assert.AreNotEqual(true, result.IsError, text);
            StringAssert.StartsWith(text, $"codemap · app · HEAD {layout.Head![..7]} · index fresh\n\n# Impact:");
            StringAssert.StartsWith(cli, "<!-- codemap v2"); // it is the report with the banner, so there is something to replace
            Assert.AreEqual(cli[cli.IndexOf("# Impact:", StringComparison.Ordinal)..], text[text.IndexOf("# Impact:", StringComparison.Ordinal)..]);
            StringAssert.Contains(text, "POST   api/orders"); // not an empty report that would match trivially
            Assert.AreEqual(full, text.Contains("## Intermediate callers (1)"));
        }
    }

    [TestMethod] // a client that does not substitute ${workspaceFolder} passes it through literally
    public async Task Unexpanded_workspace_variable_is_reported_and_falls_back_to_cwd()
    {
        var layout = Layout.Create();
        await using var client = await StartAsync(layout.Elsewhere, "--config", layout.Registry, "--workspace", "${workspaceFolder}");

        var result = await client.CallToolAsync("codemap_status", new Dictionary<string, object?>());
        var output = string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));

        StringAssert.Contains(output, "arrived unexpanded");
        StringAssert.Contains(output, "(from cwd)");
        StringAssert.Contains(output, "NOT_INDEXED");
    }

    /// <summary>Pins the client to 2025-11-25, the initialize-handshake protocol today's IDE clients speak.</summary>
    private static async Task<McpClient> StartAsync(string cwd, params string[] args)
    {
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "codemap",
            Command = "dotnet",
            Arguments = new[] { Path.Combine(AppContext.BaseDirectory, "CodeMap.Cli.dll"), "mcp" }.Concat(args).ToList(),
            WorkingDirectory = cwd,
            // On dispose, the SDK 2.2.0 client waits this long and then kills the server; the server never sees
            // end-of-input in that window (it never logs its exit line), whereas piping a real EOF into it ends it
            // within ~100 ms. The default 5 s only made every test 5 s slower.
            ShutdownTimeout = TimeSpan.FromMilliseconds(500),
        });

        var options = new McpClientOptions
        {
            ClientInfo = new Implementation { Name = "codemap-tests", Version = "1.0" },
            ProtocolVersion = "2025-11-25",
        };

        return await McpClient.CreateAsync(transport, options);
    }

    /// <summary>`codemap impact` as the command line runs it, written through --out so the console's code page cannot change the text. Its stdin is closed at once, like every child process in this project.</summary>
    private static string RunCliImpact(Layout layout, string symbol, bool full)
    {
        var outFile = Path.Combine(TestPaths.NewTempDir(), "impact.md");
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = layout.Elsewhere,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, "CodeMap.Cli.dll"), "impact", "--config", layout.Registry,
                     "--project", "app", "--symbol", symbol, "--out", outFile,
                 })
            psi.ArgumentList.Add(arg);
        if (full) psi.ArgumentList.Add("--full");

        using var process = Process.Start(psi)!;
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.IsTrue(process.WaitForExit(60_000), "codemap impact did not finish");
        Assert.AreEqual(0, process.ExitCode, $"{stdout.GetAwaiter().GetResult()}\n{stderr.GetAwaiter().GetResult()}");
        return File.ReadAllText(outFile);
    }

    /// <summary>
    /// A registry with one project ("app") whose repo holds a one-symbol index, plus an unrelated directory to start the
    /// server in. With <paramref name="git"/>, the repo is a git repo and the index records its HEAD. With
    /// <paramref name="callGraph"/>, <see cref="CancelId"/> has a caller chain: an HTTP endpoint calls a facade that calls it.
    /// </summary>
    private sealed record Layout(string Registry, string Repo, string Elsewhere, string? Head)
    {
        public const string CancelId = "M:Orders.OrderService.Cancel(System.Int32)";

        public static Layout Create(bool git = false, bool callGraph = false)
        {
            var root = TestPaths.NewTempDir();
            var repo = Path.Combine(root, "repo");
            var elsewhere = Path.Combine(root, "elsewhere");
            var indexDir = Path.Combine(root, "out", "index");
            Directory.CreateDirectory(repo);
            Directory.CreateDirectory(elsewhere);
            Directory.CreateDirectory(indexDir);
            File.WriteAllText(Path.Combine(repo, "App.sln"), "");

            var symbols = new List<SymbolRecord>
            {
                new()
                {
                    Id = CancelId, Kind = "method", Name = "Cancel", Project = "Orders",
                    File = "Orders/OrderService.cs", Line = 42, Accessibility = "public",
                },
            };
            var edges = new List<EdgeRecord>();
            if (callGraph)
            {
                symbols.Add(new()
                {
                    Id = "M:Orders.OrdersController.Post", Kind = "method", Name = "Post", Project = "Orders",
                    File = "Orders/OrdersController.cs", Line = 12, Accessibility = "public",
                });
                symbols.Add(new()
                {
                    Id = "M:Orders.OrderFacade.Run", Kind = "method", Name = "Run", Project = "Orders",
                    File = "Orders/OrderFacade.cs", Line = 7, Accessibility = "public",
                });
                edges.Add(new() { From = "M:Orders.OrderFacade.Run", To = CancelId, Kind = "call", File = "Orders/OrderFacade.cs", Line = 9 });
                edges.Add(new() { From = "M:Orders.OrdersController.Post", To = "M:Orders.OrderFacade.Run", Kind = "call", File = "Orders/OrdersController.cs", Line = 14 });
                File.WriteAllText(Path.Combine(indexDir, "entrypoints.json"),
                    """[ { "id": "M:Orders.OrdersController.Post", "type": "http", "httpMethod": "POST", "route": "api/orders" } ]""");
            }

            JsonlWriter.Write(Path.Combine(indexDir, "symbols.jsonl"), symbols);
            JsonlWriter.Write(Path.Combine(indexDir, "edges.jsonl"), edges);

            string? head = null;
            if (git)
            {
                GitFixtureHelper.InitInPlace(repo);
                GitFixtureHelper.Commit(repo, "initial");
                head = GitRepoInfo.TryGetHeadCommit(repo);
                File.WriteAllText(Path.Combine(indexDir, "meta.json"),
                    $$"""{ "indexedAt": "2026-10-30T00:00:00Z", "gitCommit": "{{head}}", "solutionPath": "App.sln", "projectCount": 1, "symbolCount": {{symbols.Count}}, "edgeCount": {{edges.Count}} }""");
            }

            var registry = Path.Combine(root, "codemap.projects.json");
            File.WriteAllText(registry, """{ "projects": [ { "name": "app", "solution": "repo/App.sln", "output": "out" } ] }""");

            return new Layout(registry, repo, elsewhere, head);
        }
    }
}
