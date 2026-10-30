using CodeMap.Query.Reporting;
using ModelContextProtocol;

namespace CodeMap.Mcp;

/// <summary>What every tool shares for the life of the server: one project resolver and one index cache.</summary>
internal sealed record ToolContext(ProjectResolver Resolver, IndexCache Indexes)
{
    /// <summary>The real inputs: the server's own arguments, working directory and user profile.</summary>
    public static ToolContext ForProcess(string? configArg, string? workspaceArg) => new(
        new ProjectResolver(
            configArg, workspaceArg, Environment.CurrentDirectory, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
        new IndexCache());

    public Staleness Staleness(ResolvedProject project, CachedIndex index, IReadOnlyCollection<string>? relevantFiles = null)
        => StalenessBanner.Compute(index.Index.Meta, relevantFiles, project.RepoRoot);

    /// <summary>The line every tool result opens with. When the cache is serving an older copy, why goes on the same line, so it cannot be missed.</summary>
    public static string FreshnessLine(ResolvedProject project, CachedIndex index, Staleness staleness)
    {
        var line = StalenessBanner.RenderLine(project.Name, staleness);
        return index.Problem == null ? line : $"{line} · warning: {index.Problem}";
    }

    /// <summary>
    /// Runs a tool body off the request thread (the engine is synchronous). A <see cref="McpException"/> reaches the
    /// model as a tool error it can act on; anything else reaches it only as the SDK's generic message, so the
    /// details are written to stderr, where the client's MCP log shows them.
    /// </summary>
    public static Task<string> Run(string tool, Func<string> body, CancellationToken cancellationToken)
        => Task.Run(() =>
        {
            try
            {
                return body();
            }
            catch (Exception ex) when (ex is not McpException and not OperationCanceledException)
            {
                Console.Error.WriteLine($"codemap mcp: {tool} failed: {ex}");
                throw;
            }
        }, cancellationToken);
}
