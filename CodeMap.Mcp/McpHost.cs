using System.Diagnostics;
using System.Reflection;
using CodeMap.Query.ArgParsing;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CodeMap.Mcp;

/// <summary>
/// <c>codemap mcp [--config &lt;file&gt;] [--workspace &lt;dir&gt;]</c>: an MCP server over stdio.
///
/// --workspace is there because MCP roots are deprecated as of specification 2026-07-28 (SEP-2577), and a 2026-07-28
/// session has no channel for the server to ask for them at all. The SEP points at server configuration instead,
/// which for VS Code means passing "${workspaceFolder}" in mcp.json. The server never asks for roots.
///
/// stdout is the protocol channel, so nothing in this project may write to Console.Out. Diagnostics go to
/// stderr, which clients show in their MCP output or log view.
/// </summary>
public static class McpHost
{
    public static int Run(string[] rawArgs)
    {
        var args = Args.Parse(rawArgs, options: new[] { "config", "workspace" }, flags: Array.Empty<string>());
        var workspaceArg = args.GetOrDefault("workspace");
        return RunAsync(ToolContext.ForProcess(args.GetOrDefault("config"), workspaceArg), workspaceArg).GetAwaiter().GetResult();
    }

    private static async Task<int> RunAsync(ToolContext context, string? workspaceArg)
    {
        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "codemap", Version = ToolVersion() },
            // Secondary: VS Code does not pass server instructions to the model, so the tool descriptions carry the routing.
            ServerInstructions = ToolText.Instructions,
            ToolCollection = new() { StatusTool.Create(context), ImpactTool.Create(context) },
        };

        var transport = new StdioServerTransport("codemap");
        await using var server = McpServer.Create(transport, options);

        // The client's answers about itself, written to stderr as soon as the handshake ends, so they show in the
        // client's MCP log without anyone having to call a tool.
        server.RegisterNotificationHandler(NotificationMethods.InitializedNotification, (_, _) =>
        {
            _ = Task.Run(() => LogHandshakeAsync(server, workspaceArg));
            return ValueTask.CompletedTask;
        });

        var startedUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime();
        Console.Error.WriteLine(
            $"codemap mcp: ready after {(long)(DateTime.UtcNow - startedUtc).TotalMilliseconds} ms · pid {Environment.ProcessId} · cwd {Environment.CurrentDirectory}");
        var workspace = context.Resolver.Workspace;
        Console.Error.WriteLine($"codemap mcp: workspace {workspace.Path} (from {workspace.SourceLabel})");
        foreach (var warning in workspace.Warnings) Console.Error.WriteLine($"codemap mcp: warning: {warning}");

        await server.RunAsync();
        Console.Error.WriteLine("codemap mcp: client closed the connection, exiting");
        return 0;
    }

    /// <summary>One stderr line per fact, right after the handshake. Never throws: a failure here must not take the server down.</summary>
    private static async Task LogHandshakeAsync(McpServer server, string? workspaceArg)
    {
        try
        {
            await Task.Delay(300); // the SDK fills ClientInfo and the negotiated version in just after this notification is dispatched
            var client = server.ClientInfo;
            Console.Error.WriteLine($"codemap mcp: handshake · client {client?.Name} {client?.Version} · protocol {server.NegotiatedProtocolVersion} · capabilities {Describe(server.ClientCapabilities)}");
            Console.Error.WriteLine($"codemap mcp: handshake · cwd {Environment.CurrentDirectory} · --workspace {workspaceArg ?? "(not given)"}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"codemap mcp: handshake log failed: {ex.Message}");
        }
    }

    private static string Describe(ClientCapabilities? capabilities)
    {
        if (capabilities == null) return "none declared";

        var parts = new List<string>();
        // Reported as declared, though the server uses neither roots nor sampling (both deprecated: MCP9005).
#pragma warning disable MCP9005
        if (capabilities.Roots != null) parts.Add(capabilities.Roots.ListChanged == true ? "roots (listChanged)" : "roots");
        if (capabilities.Sampling != null) parts.Add("sampling");
#pragma warning restore MCP9005
        if (capabilities.Elicitation != null) parts.Add("elicitation");
        return parts.Count == 0 ? "none declared" : string.Join(", ", parts);
    }

    private static string ToolVersion() =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "unknown";
}
