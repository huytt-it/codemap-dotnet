using System.ComponentModel;
using CodeMap.Query.Find;
using CodeMap.Query.Impact;
using CodeMap.Query.Models;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace CodeMap.Mcp;

/// <summary>
/// <c>codemap_impact</c>: what is affected by changing a symbol. The report is `codemap impact`'s own
/// (<see cref="CompactRenderer"/>), rendered in <see cref="RenderStyle.Mcp"/>, so the two cannot drift apart; this
/// class adds only what a tool call needs on top: the project, the symbol lookup, and a one-line freshness in place
/// of the markdown banner.
/// </summary>
internal sealed class ImpactTool(ToolContext context)
{
    private const int DefaultDepth = 5;
    private const int MinDepth = 1;
    private const int MaxDepth = 10;
    private const int MaxCandidates = 5;
    private const int MaxNearMatches = 3;

    public static McpServerTool Create(ToolContext context) =>
        McpServerTool.Create(new ImpactTool(context).RunAsync, new McpServerToolCreateOptions
        {
            Name = "codemap_impact",
            Title = "CodeMap impact",
            Description = ToolText.Impact,
            // Spelled out because the SDK defaults are the opposite (Destructive and OpenWorld default to true).
            ReadOnly = true,
            Destructive = false,
            Idempotent = true,
            OpenWorld = false,
        });

    public Task<string> RunAsync(
        [Description(ToolText.ImpactSymbolParam)] string symbol,
        [Description(ToolText.ProjectParam)] string? project = null,
        [Description(ToolText.ImpactDepthParam)] int depth = DefaultDepth,
        [Description(ToolText.ImpactDetailParam)] string detail = "compact",
        CancellationToken cancellationToken = default)
        => ToolContext.Run("codemap_impact", () => Render(symbol, project, depth, detail, cancellationToken), cancellationToken);

    public string Render(string symbol, string? project, int depth, string? detail, CancellationToken cancellationToken = default)
    {
        var full = IsFull(detail);
        var resolved = context.Resolver.Resolve(project);
        var index = context.Indexes.Get(resolved.Name, resolved.IndexDir, cancellationToken);

        var docId = Lookup(index.Index, resolved.Name, symbol);
        var result = ImpactEngine.Traverse(index.Index, docId, Math.Clamp(depth, MinDepth, MaxDepth));

        // The same file set the CLI's banner compares, so "N relevant file(s) changed" means the same thing in both.
        var staleness = context.Staleness(resolved, index, CompactRenderer.CollectRelevantFiles(result));
        var freshness = ToolContext.FreshnessLine(resolved, index, staleness);
        var report = CompactRenderer.Render(
            result, full, index.Index.Meta, resolved.RepoRoot, new RenderOptions(RenderStyle.Mcp));
        return $"{freshness}\n\n{report}";
    }

    private static bool IsFull(string? detail) => detail?.Trim().ToLowerInvariant() switch
    {
        null or "" or "compact" => false,
        "full" => true,
        _ => throw new McpException($"detail must be 'compact' or 'full', not '{detail}'."),
    };

    /// <summary>
    /// A docId is used as it is. A name goes through the same search as `codemap find`, and is ambiguous only when
    /// several symbols share the top score (overloads, usually): a lower-scoring substring match is not a rival to
    /// an exact one, and asking about it would cost the model a call for nothing.
    /// </summary>
    private static string Lookup(ImpactIndex index, string project, string symbol)
    {
        var query = symbol?.Trim() ?? "";
        if (query.Length == 0)
            throw new McpException("symbol is required: a docId or a Type.Member name.");

        if (IsDocId(query))
            return index.SymbolsById.ContainsKey(query) ? query : throw NotFound(index, project, query);

        var hits = FindCommand.Search(index.SymbolsById.Values.ToList(), query);
        if (hits.Count == 0) throw NotFound(index, project, query);

        var top = hits.TakeWhile(h => h.Score == hits[0].Score).ToList();
        if (top.Count == 1) return top[0].Symbol.Id;

        var lines = top.Take(MaxCandidates).Select(h => Describe(h.Symbol)).ToList();
        if (top.Count > MaxCandidates) lines.Add($"- (and {top.Count - MaxCandidates} more)");
        throw new CodeMapToolException(CodeMapToolException.AmbiguousSymbol,
            $"'{query}' matches {top.Count} symbols equally well (usually overloads). " +
            $"Call again with symbol set to the docId of the one you mean:\n{string.Join("\n", lines)}");
    }

    private static CodeMapToolException NotFound(ImpactIndex index, string project, string query)
    {
        var near = NearMatches(index, query);
        var suggestion = near.Count == 0
            ? "No similar names either. "
            : $"Similar symbols; pass the docId of the one you mean as symbol:\n{string.Join("\n", near.Select(Describe))}\n";
        return new CodeMapToolException(CodeMapToolException.SymbolNotFound,
            $"no symbol '{query}' in the index of project '{project}'. {suggestion}" +
            $"If the code is newer than the index, run `codemap sync --project {project}`; " +
            "for comments, string literals and config values, use text search.");
    }

    /// <summary>The top few symbols whose name contains the type-and-member part of what was asked for; failing that, whose name contains just the member.</summary>
    private static List<SymbolRecord> NearMatches(ImpactIndex index, string query)
    {
        var symbols = index.SymbolsById.Values.ToList();
        var name = TypeAndMember(query);
        var hits = FindCommand.Search(symbols, name);
        if (hits.Count == 0 && name.Contains('.'))
            hits = FindCommand.Search(symbols, name[(name.LastIndexOf('.') + 1)..]);
        return hits.Take(MaxNearMatches).Select(h => h.Symbol).ToList();
    }

    /// <summary>"M:Ns.Type.Member(System.Int32)" and "Ns.Type.Member" both give "Type.Member", the shape `find` matches on.</summary>
    private static string TypeAndMember(string query)
    {
        var text = IsDocId(query) ? query[2..] : query;
        var parameters = text.IndexOf('(');
        if (parameters >= 0) text = text[..parameters];
        return string.Join('.', text.Split('.').TakeLast(2));
    }

    /// <summary>Same test <c>ImpactEngine</c> uses to strip the one-letter kind prefix ("M:", "T:", "P:", ...).</summary>
    private static bool IsDocId(string query) => query.Length > 2 && query[1] == ':' && char.IsAsciiLetterUpper(query[0]);

    private static string Describe(SymbolRecord symbol) => $"- {symbol.Id} — {symbol.File}:{symbol.Line}";
}
