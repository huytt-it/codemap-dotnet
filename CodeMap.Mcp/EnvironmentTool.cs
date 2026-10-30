using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using CodeMap.Query.ArgParsing;
using CodeMap.Query.Config;
using CodeMap.Query.Impact;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

// Roots are deprecated as of MCP 2026-07-28 (SEP-2577), so every roots API below warns (MCP9005). This file probes
// the deprecated feature on purpose: whether today's clients still send roots decides how long the real resolver
// can lean on them.
#pragma warning disable MCP9005

namespace CodeMap.Mcp;

/// <summary>
/// The spike's one tool, <c>codemap_debug_environment</c>. Prints the inputs the real ProjectResolver will have to
/// work from (client, protocol, --workspace, roots, cwd, registry) and what the design's matching rules make of
/// them, then times loading the matched project's index. Its output is meant to be pasted into the client test
/// checklist as is.
/// </summary>
internal sealed class EnvironmentTool(SpikeState state)
{
    /// <summary>A client can declare roots and then never answer roots/list; the spike reports that instead of hanging.</summary>
    private static readonly TimeSpan RootsTimeout = TimeSpan.FromSeconds(5);

    public static McpServerTool Create(SpikeState state) =>
        McpServerTool.Create(new EnvironmentTool(state).RunAsync, new McpServerToolCreateOptions
        {
            Name = "codemap_debug_environment",
            Title = "CodeMap environment (spike)",
            Description = SpikeText.EnvironmentTool,
            // Spelled out because the SDK defaults are the opposite (Destructive and OpenWorld default to true), and
            // whether ReadOnly really skips the approval prompt in each client is one of the things being checked.
            ReadOnly = true,
            Destructive = false,
            Idempotent = true,
            OpenWorld = false,
        });

    public async Task<string> RunAsync(
        McpServer server,
        [Description(SpikeText.ProjectParam)] string? project = null,
        CancellationToken cancellationToken = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# CodeMap MCP spike: what this client gives the server");
        sb.AppendLine();

        var client = server.ClientInfo;
        Line(sb, "client", client == null ? "(clientInfo not sent)" : $"{client.Name} {client.Version}");
        Line(sb, "protocol", server.NegotiatedProtocolVersion ?? "unknown");
        Line(sb, "mrtr", server.IsMrtrSupported ? "supported" : "not supported");
        Line(sb, "capabilities", Describe(server.ClientCapabilities));
        Line(sb, "process", $"pid {Environment.ProcessId} · ready {state.ReadyAfterMs} ms after start · up {(DateTime.UtcNow - state.StartedUtc).TotalSeconds:0} s");
        Line(sb, "cwd", Environment.CurrentDirectory);
        Line(sb, "command", Environment.CommandLine);
        var workspaceArgDir = WorkspaceArgDir(out var workspaceArgNote);
        Line(sb, "--workspace", workspaceArgNote);
        sb.AppendLine();

        var roots = await ProbeRootsAsync(server, cancellationToken);
        Line(sb, "roots", roots.Status);
        foreach (var root in roots.Roots)
        {
            var path = RootPath.FromUri(root.Uri);
            var registryAbove = path == null ? null : FindUpward(path);
            sb.AppendLine($"  - {root.Uri}{(root.Name != null ? $" ({root.Name})" : "")}");
            sb.AppendLine($"    -> {path ?? "(not a local file URI)"}");
            if (path != null) sb.AppendLine($"    registry above it: {registryAbove ?? "none"}");
        }

        Line(sb, "roots events", state.RootsChangedCount == 0
            ? "no roots/list_changed received yet"
            : $"{state.RootsChangedCount} roots/list_changed, last at {state.LastRootsChangeUtc:HH:mm:ss}Z");
        sb.AppendLine();

        // Order under test: explicit server configuration first (what SEP-2577 recommends), then roots while clients
        // still send them, then the process cwd, which the client chooses and may not be the workspace at all.
        IReadOnlyList<string> workspaceDirs;
        string source;
        if (workspaceArgDir != null)
            (workspaceDirs, source) = (new[] { workspaceArgDir }, "--workspace");
        else if (roots.Paths.Count > 0)
            (workspaceDirs, source) = (roots.Paths, "roots");
        else
            (workspaceDirs, source) = (new[] { RootPath.Normalize(Environment.CurrentDirectory) }, "cwd (no --workspace, no usable roots)");
        Line(sb, "workspace", $"from {source}: {string.Join(", ", workspaceDirs)}");

        var (registry, registryNote) = FindRegistry(workspaceDirs);
        Line(sb, "registry", registryNote);

        var matches = registry == null ? new List<ProjectMatch>() : Match(registry, workspaceDirs);
        foreach (var m in matches)
            sb.AppendLine($"  - {m.Entry.Name}: {m.Dir} is {m.Rule}");
        if (registry != null)
            Line(sb, "verdict", matches.Count switch
            {
                0 => $"NOT_INDEXED: the workspace maps to none of the {registry.Projects.Count} project(s) in the registry",
                1 => $"default project: {matches[0].Entry.Name}",
                _ => $"AMBIGUOUS_PROJECT: {matches.Count} projects match; the tools would ask for project=<name>",
            });
        sb.AppendLine();

        await AppendIndexLoadAsync(sb, registry, project ?? (matches.Count == 1 ? matches[0].Entry.Name : null), cancellationToken);
        return sb.ToString();
    }

    /// <summary>One stderr line per fact, right after the handshake. Never throws: a failure here must not take the server down.</summary>
    public static async Task LogHandshakeAsync(McpServer server, SpikeState state)
    {
        try
        {
            await Task.Delay(300); // the SDK fills ClientInfo and the negotiated version in just after this notification is dispatched
            var client = server.ClientInfo;
            Console.Error.WriteLine($"codemap mcp: handshake · client {client?.Name} {client?.Version} · protocol {server.NegotiatedProtocolVersion} · capabilities {Describe(server.ClientCapabilities)}");
            Console.Error.WriteLine($"codemap mcp: handshake · cwd {Environment.CurrentDirectory} · --workspace {state.WorkspaceArg ?? "(not given)"}");
            var roots = await ProbeRootsAsync(server, CancellationToken.None);
            Console.Error.WriteLine($"codemap mcp: handshake · roots {roots.Status}");
            foreach (var root in roots.Roots) Console.Error.WriteLine($"codemap mcp: handshake · root {root.Uri}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"codemap mcp: handshake log failed: {ex.Message}");
        }
    }

    private static async Task<RootsProbe> ProbeRootsAsync(McpServer server, CancellationToken cancellationToken)
    {
        if (server.ClientCapabilities?.Roots == null)
            return RootsProbe.Empty("client did not declare the roots capability");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RootsTimeout);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await server.RequestRootsAsync(new ListRootsRequestParams(), timeout.Token);
            var roots = result.Roots.ToList();
            var paths = roots.Select(r => RootPath.FromUri(r.Uri)).OfType<string>().ToList();
            return new RootsProbe($"{roots.Count} root(s), answered in {stopwatch.ElapsedMilliseconds} ms", roots, paths);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return RootsProbe.Empty($"capability declared, but roots/list got no answer within {RootsTimeout.TotalSeconds:0} s");
        }
        catch (Exception ex)
        {
            return RootsProbe.Empty($"capability declared, but roots/list failed: {ex.Message}");
        }
    }

    private string? WorkspaceArgDir(out string note)
    {
        var raw = state.WorkspaceArg;
        if (raw == null)
        {
            note = "not given";
            return null;
        }

        // A client that does not substitute variables in its MCP config hands the placeholder over verbatim.
        if (raw.Contains("${", StringComparison.Ordinal))
        {
            note = $"'{raw}' arrived unexpanded: this client does not substitute that variable";
            return null;
        }

        var full = RootPath.Normalize(raw);
        if (!Directory.Exists(full))
        {
            note = $"{full} does not exist";
            return null;
        }

        note = full;
        return full;
    }

    /// <summary>Same search order as the design: --config, then walking up from each workspace directory, then ~/.codemap.</summary>
    private (ProjectRegistry? Registry, string Note) FindRegistry(IReadOnlyList<string> workspaceDirs)
    {
        string? path = null;
        var how = "";
        if (state.ConfigPath != null)
        {
            path = Path.GetFullPath(state.ConfigPath);
            if (!File.Exists(path)) return (null, $"--config points at {path}, which does not exist");
            how = "--config";
        }
        else
        {
            foreach (var dir in workspaceDirs)
            {
                path = FindUpward(dir);
                if (path == null) continue;
                how = $"walking up from {dir}";
                break;
            }

            if (path == null)
            {
                var home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codemap", ProjectRegistry.FileName);
                if (File.Exists(home))
                {
                    path = home;
                    how = "~/.codemap";
                }
            }
        }

        if (path == null)
            return (null, "none found (no --config, none above the workspace, none in ~/.codemap)");

        try
        {
            return (ProjectRegistry.LoadFrom(path), $"{path} (via {how})");
        }
        catch (CliUsageException ex)
        {
            return (null, $"{path} could not be read: {ex.Message}");
        }
    }

    /// <summary>The design's three rules, in its order: the workspace sits inside the repo, the workspace is a parent folder that contains the repo, or the workspace sits inside the paired frontend.</summary>
    private static List<ProjectMatch> Match(ProjectRegistry registry, IReadOnlyList<string> workspaceDirs)
    {
        var matches = new List<ProjectMatch>();
        foreach (var entry in registry.Projects)
        {
            var repo = registry.RepoOf(entry);
            var frontend = entry.Frontend != null ? registry.ResolvePath(entry.Frontend) : null;
            foreach (var dir in workspaceDirs)
            {
                var rule = RootPath.IsSameOrInside(dir, repo) ? $"inside its repo ({repo})"
                    : RootPath.IsSameOrInside(repo, dir) ? $"a parent folder of its repo ({repo})"
                    : frontend != null && RootPath.IsSameOrInside(dir, frontend) ? $"inside its frontend ({frontend})"
                    : null;
                if (rule == null) continue;
                matches.Add(new ProjectMatch(entry, dir, rule));
                break;
            }
        }

        return matches;
    }

    private static async Task AppendIndexLoadAsync(StringBuilder sb, ProjectRegistry? registry, string? projectName, CancellationToken cancellationToken)
    {
        if (projectName == null || registry == null)
        {
            Line(sb, "index load", "skipped: no single project matched; pass project=<name> to time one");
            return;
        }

        ProjectEntry entry;
        try
        {
            entry = registry.Require(projectName);
        }
        catch (CliUsageException ex)
        {
            Line(sb, "index load", ex.Message);
            return;
        }

        var indexDir = registry.IndexDirOf(entry);
        if (!File.Exists(Path.Combine(indexDir, "symbols.jsonl")))
        {
            Line(sb, "index load", $"{entry.Name}: no index at {indexDir} (run codemap sync --project {entry.Name})");
            return;
        }

        // The design's latency target only covers calls after the first load; this is the number that decides
        // whether the first call also fits inside a client's tool timeout, or needs a background warm-up.
        try
        {
            var before = GC.GetTotalMemory(forceFullCollection: true);
            var stopwatch = Stopwatch.StartNew();
            var index = await Task.Run(() => ImpactIndex.Load(indexDir), cancellationToken);
            stopwatch.Stop();
            var retainedMb = (GC.GetTotalMemory(forceFullCollection: true) - before) / (1024 * 1024);
            var edgeCount = index.ReverseEdges.Values.Sum(edges => edges.Count);

            Line(sb, "index load",
                $"{entry.Name}: {stopwatch.ElapsedMilliseconds} ms · {index.SymbolsById.Count} symbols · {edgeCount} edges · " +
                $"{index.Tickets.Count} ticket record(s) · ~{retainedMb} MB retained · working set {Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024)} MB");
            GC.KeepAlive(index);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Line(sb, "index load", $"{entry.Name}: failed: {ex.Message}");
        }
    }

    private static string? FindUpward(string startDir)
    {
        for (var dir = new DirectoryInfo(startDir); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, ProjectRegistry.FileName);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    private static string Describe(ClientCapabilities? capabilities)
    {
        if (capabilities == null) return "none declared";

        var parts = new List<string>();
        if (capabilities.Roots != null) parts.Add(capabilities.Roots.ListChanged == true ? "roots (listChanged)" : "roots");
        if (capabilities.Sampling != null) parts.Add("sampling");
        if (capabilities.Elicitation != null) parts.Add("elicitation");
        return parts.Count == 0 ? "none declared" : string.Join(", ", parts);
    }

    private static void Line(StringBuilder sb, string label, string text) => sb.Append(label.PadRight(14)).AppendLine(text);

    private sealed record ProjectMatch(ProjectEntry Entry, string Dir, string Rule);

    private sealed record RootsProbe(string Status, IReadOnlyList<Root> Roots, IReadOnlyList<string> Paths)
    {
        public static RootsProbe Empty(string status) => new(status, Array.Empty<Root>(), Array.Empty<string>());
    }
}
