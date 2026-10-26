using CodeMap.Mcp;
using CodeMap.Query.Json;
using CodeMap.Query.Models;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

// The roots tests drive a deprecated MCP feature (SEP-2577) on purpose; see EnvironmentTool.
#pragma warning disable MCP9005

namespace CodeMap.Tests;

/// <summary>
/// MCP spike (M0), protocol layer: starts the real `codemap mcp` as a child process and talks to it over stdio
/// with the SDK's own client. The client would fail to parse anything else written to stdout, so every passing
/// test here also shows the protocol channel stays clean.
/// </summary>
[TestClass]
public class McpSpikeTests
{
    private const string ToolName = "codemap_debug_environment";

    [TestMethod]
    public async Task Diagnostic_tool_is_declared_read_only_and_closed_world()
    {
        var layout = Layout.Create();
        await using var client = await StartAsync(layout.Elsewhere, "--config", layout.Registry);

        var tool = (await client.ListToolsAsync()).Single(t => t.Name == ToolName);
        var annotations = tool.ProtocolTool.Annotations!;

        Assert.AreEqual(true, annotations.ReadOnlyHint);
        Assert.AreEqual(false, annotations.DestructiveHint);
        Assert.AreEqual(true, annotations.IdempotentHint);
        Assert.AreEqual(false, annotations.OpenWorldHint);
    }

    [TestMethod] // the marker must live ONLY in the instructions, or a client test could not tell where the model got it
    public async Task Spike_marker_is_in_the_server_instructions_and_nowhere_in_the_tool_output()
    {
        var layout = Layout.Create();
        await using var client = await StartAsync(layout.Elsewhere, "--config", layout.Registry, "--workspace", layout.Repo);

        StringAssert.Contains(client.ServerInstructions, SpikeText.Marker);
        Assert.IsTrue((await client.ListPromptsAsync()).Any(p => p.Name == "spike-check"));
        Assert.IsFalse((await CallAsync(client)).Contains(SpikeText.Marker));
    }

    [TestMethod] // the cwd is the client's choice: here it is deliberately a directory outside every project
    public async Task Roots_from_the_client_pick_the_project_even_when_cwd_is_elsewhere()
    {
        var layout = Layout.Create();
        await using var client = await StartAsync(layout.Elsewhere, roots: layout.Repo, args: new[] { "--config", layout.Registry });

        var output = await CallAsync(client);

        StringAssert.Contains(output, "from roots");
        StringAssert.Contains(output, "default project: app");
        StringAssert.Contains(output, "1 symbols");
    }

    [TestMethod] // server configuration is the alternative SEP-2577 recommends, so it has to win over the deprecated roots
    public async Task Workspace_argument_wins_over_roots()
    {
        var layout = Layout.Create();
        await using var client = await StartAsync(layout.Elsewhere, roots: layout.Elsewhere, args: new[] { "--config", layout.Registry, "--workspace", layout.Repo });

        var output = await CallAsync(client);

        StringAssert.Contains(output, "from --workspace");
        StringAssert.Contains(output, "default project: app");
    }

    [TestMethod] // a client that does not substitute ${workspaceFolder} passes it through literally
    public async Task Unexpanded_workspace_variable_is_reported_and_falls_back_to_cwd()
    {
        var layout = Layout.Create();
        await using var client = await StartAsync(layout.Elsewhere, "--config", layout.Registry, "--workspace", "${workspaceFolder}");

        var output = await CallAsync(client);

        StringAssert.Contains(output, "arrived unexpanded");
        StringAssert.Contains(output, "from cwd");
        StringAssert.Contains(output, "NOT_INDEXED");
    }

    [TestMethod] // VS Code percent-encodes the drive colon in root URIs
    public void Root_uri_with_an_escaped_drive_colon_becomes_a_local_path()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Drive letters only exist on Windows.");

        Assert.AreEqual(@"d:\Repos\My Shop", RootPath.FromUri("file:///d%3A/Repos/My%20Shop"), ignoreCase: true);
        Assert.AreEqual(@"D:\Repos\Shop", RootPath.FromUri("file:///D:/Repos/Shop/"), ignoreCase: true);
        Assert.IsNull(RootPath.FromUri("vscode-remote://wsl+Ubuntu/home/me/shop"));
    }

    [TestMethod]
    public void Inside_check_compares_whole_path_segments()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Case-insensitive paths are a Windows rule.");

        Assert.IsTrue(RootPath.IsSameOrInside(@"C:\Repos\Shop\src", @"c:\repos\shop"));
        Assert.IsTrue(RootPath.IsSameOrInside(@"C:\Repos\Shop\", @"C:\Repos\Shop"));
        Assert.IsFalse(RootPath.IsSameOrInside(@"C:\Repos\ShopWeb", @"C:\Repos\Shop"));
    }

    private static Task<McpClient> StartAsync(string cwd, params string[] args) => StartAsync(cwd, roots: null, args);

    /// <summary>
    /// Pins the client to 2025-11-25, the initialize-handshake protocol today's IDE clients speak: on a 2026-07-28
    /// session the server has no channel to ask for roots at all, which is the deprecation the spike is measuring.
    /// </summary>
    private static async Task<McpClient> StartAsync(string cwd, string? roots, string[] args)
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
        if (roots != null)
        {
            options.Capabilities = new ClientCapabilities { Roots = new RootsCapability { ListChanged = true } };
            options.Handlers = new McpClientHandlers
            {
                RootsHandler = (_, _) => ValueTask.FromResult(new ListRootsResult
                {
                    Roots = new List<Root> { new() { Uri = new Uri(roots).AbsoluteUri, Name = "workspace" } },
                }),
            };
        }

        return await McpClient.CreateAsync(transport, options);
    }

    private static async Task<string> CallAsync(McpClient client)
    {
        var result = await client.CallToolAsync(ToolName, new Dictionary<string, object?>());
        Assert.AreNotEqual(true, result.IsError);
        return string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
    }

    /// <summary>A registry with one project ("app") whose repo holds a one-symbol index, plus an unrelated directory to start the server in.</summary>
    private sealed record Layout(string Registry, string Repo, string Elsewhere)
    {
        public static Layout Create()
        {
            var root = TestPaths.NewTempDir();
            var repo = Path.Combine(root, "repo");
            var elsewhere = Path.Combine(root, "elsewhere");
            var indexDir = Path.Combine(root, "out", "index");
            Directory.CreateDirectory(repo);
            Directory.CreateDirectory(elsewhere);
            Directory.CreateDirectory(indexDir);
            File.WriteAllText(Path.Combine(repo, "App.sln"), "");

            JsonlWriter.Write(Path.Combine(indexDir, "symbols.jsonl"), new[]
            {
                new SymbolRecord
                {
                    Id = "M:Orders.OrderService.Cancel(System.Int32)", Kind = "method", Name = "Cancel", Project = "Orders",
                    File = "Orders/OrderService.cs", Line = 42, Accessibility = "public",
                },
            });
            JsonlWriter.Write(Path.Combine(indexDir, "edges.jsonl"), Array.Empty<EdgeRecord>());

            var registry = Path.Combine(root, "codemap.projects.json");
            File.WriteAllText(registry, """{ "projects": [ { "name": "app", "solution": "repo/App.sln", "output": "out" } ] }""");

            return new Layout(registry, repo, elsewhere);
        }
    }
}
