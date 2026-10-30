using System.Diagnostics;
using System.Reflection;
using CodeMap.Query.ArgParsing;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CodeMap.Mcp;

/// <summary>
/// <c>codemap mcp [--config &lt;file&gt;] [--workspace &lt;dir&gt;]</c>: an MCP server over stdio.
///
/// SPIKE, milestone M0 of the MCP design. It serves one diagnostic tool and one prompt, and exists to find out
/// what each client really gives a server (workspace roots, working directory, protocol version, capabilities),
/// whether read-only annotations skip the approval prompt, whether server instructions reach the model, and how
/// long a real index takes to load, before ProjectResolver and IndexCache are designed around any of it.
///
/// --workspace is there because MCP roots, which the design relied on, are deprecated as of specification
/// 2026-07-28 (SEP-2577), and a 2026-07-28 session has no channel for the server to ask for them at all. The SEP
/// points at server configuration instead, which for VS Code means passing "${workspaceFolder}" in mcp.json.
///
/// stdout is the protocol channel, so nothing in this project may write to Console.Out. Diagnostics go to
/// stderr, which clients show in their MCP output or log view.
/// </summary>
public static class McpHost
{
    public static int Run(string[] rawArgs)
    {
        var args = Args.Parse(rawArgs, options: new[] { "config", "workspace" }, flags: Array.Empty<string>());
        var state = new SpikeState(args.GetOrDefault("config"), args.GetOrDefault("workspace"));
        return RunAsync(state, ToolContext.ForProcess(state.ConfigPath, state.WorkspaceArg)).GetAwaiter().GetResult();
    }

    private static async Task<int> RunAsync(SpikeState state, ToolContext context)
    {
        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "codemap", Version = ToolVersion() },
            ServerInstructions = SpikeText.Instructions,
            ToolCollection = new() { EnvironmentTool.Create(state), StatusTool.Create(context), ImpactTool.Create(context) },
            PromptCollection = new()
            {
                McpServerPrompt.Create(() => SpikeText.CheckPrompt, new McpServerPromptCreateOptions
                {
                    Name = "spike-check",
                    Title = "CodeMap spike check",
                    Description = "Runs the CodeMap environment diagnostic and asks for the spike marker.",
                }),
            },
        };

        var transport = new StdioServerTransport("codemap");
        await using var server = McpServer.Create(transport, options);

        // A client that supports roots should send this when the workspace's folders change. Whether it does is
        // one of the things the spike is here to observe, so count it rather than act on it. Deprecated API on
        // purpose (MCP9005): observing the deprecated feature is the point.
#pragma warning disable MCP9005
        server.RegisterNotificationHandler(NotificationMethods.RootsListChangedNotification, (_, _) =>
#pragma warning restore MCP9005
        {
            state.OnRootsChanged();
            return ValueTask.CompletedTask;
        });

        // The client's answers to the spike's questions, written to stderr as soon as the handshake ends, so they
        // show in the client's MCP log without anyone having to call a tool. Same facts as codemap_debug_environment.
        server.RegisterNotificationHandler(NotificationMethods.InitializedNotification, (_, _) =>
        {
            _ = Task.Run(() => EnvironmentTool.LogHandshakeAsync(server, state));
            return ValueTask.CompletedTask;
        });

        state.MarkReady();
        Console.Error.WriteLine(
            $"codemap mcp: ready after {state.ReadyAfterMs} ms · pid {Environment.ProcessId} · cwd {Environment.CurrentDirectory}");
        var workspace = context.Resolver.Workspace;
        Console.Error.WriteLine($"codemap mcp: workspace {workspace.Path} (from {workspace.SourceLabel})");
        foreach (var warning in workspace.Warnings) Console.Error.WriteLine($"codemap mcp: warning: {warning}");

        await server.RunAsync();
        Console.Error.WriteLine("codemap mcp: client closed the connection, exiting");
        return 0;
    }

    private static string ToolVersion() =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "unknown";
}

/// <summary>What the spike remembers between calls: how it was started, and the roots notifications it saw.</summary>
internal sealed class SpikeState(string? configPath, string? workspaceArg)
{
    private int _rootsChangedCount;
    private DateTime? _lastRootsChangeUtc;

    public string? ConfigPath { get; } = configPath;

    /// <summary>--workspace as given. Kept raw so a client that passes "${workspaceFolder}" unexpanded shows up as exactly that.</summary>
    public string? WorkspaceArg { get; } = workspaceArg;

    public DateTime StartedUtc { get; } = Process.GetCurrentProcess().StartTime.ToUniversalTime();

    /// <summary>Process start to "server created, about to read stdin". A client gives up on a server that is slow to answer initialize.</summary>
    public long ReadyAfterMs { get; private set; }

    public int RootsChangedCount => Volatile.Read(ref _rootsChangedCount);

    public DateTime? LastRootsChangeUtc => _lastRootsChangeUtc;

    public void MarkReady() => ReadyAfterMs = (long)(DateTime.UtcNow - StartedUtc).TotalMilliseconds;

    public void OnRootsChanged()
    {
        Interlocked.Increment(ref _rootsChangedCount);
        _lastRootsChangeUtc = DateTime.UtcNow;
        Console.Error.WriteLine("codemap mcp: notifications/roots/list_changed received");
    }
}
