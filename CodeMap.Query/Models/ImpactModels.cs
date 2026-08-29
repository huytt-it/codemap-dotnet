namespace CodeMap.Query.Models;

/// <summary>
/// An entry point reached by the BFS traversal in ImpactEngine, at a given depth from the root symbol.
/// <paramref name="IsConfirmedBinding"/> is false only when this node was FIRST discovered through a
/// via:"interface" edge that docs/BENCHMARK-INTERFACE-EXPANSION.md's audit found could be genuine
/// over-inference (di-confirmed.json shows a DIFFERENT implementation is the one actually DI-bound at that
/// call site) — true for every other case, including "we don't know" (no DI info at all), since only a
/// positively-confirmed wrong answer is worth flagging, not silence.
/// </summary>
public sealed record ReachedEntryPoint(string Id, string DisplayName, string Type, string? HttpMethod, string? Route, string Project, int Depth, bool IsConfirmedBinding = true);

/// <summary>A non-entry-point, non-test caller reached by the traversal — only rendered when --full is passed (spec section 7, "chống nổ report"). See ReachedEntryPoint for IsConfirmedBinding.</summary>
public sealed record CallerNode(string Id, string DisplayName, string Project, int Depth, bool IsConfirmedBinding = true);

/// <summary>An FE screen (spec section 6's `feature`) reached via an api-links.jsonl match onto one of the traversal's reached http entry points.</summary>
public sealed record ReachedScreen(string Feature, string FrontendFile, int FrontendLine, string HttpMethod, string Route, string Confidence, string MatchKind, string BackendEntryPointId);

/// <summary>
/// Pure data produced by ImpactEngine.Traverse — spec section 7: "ImpactEngine.Traverse(symbolId, depth) trả về
/// một ImpactResult thuần dữ liệu". Renderers (CompactRenderer for `impact`, EvidenceRenderer for `slice`) only
/// read this; they never re-run the traversal or read JSONL themselves.
/// </summary>
public sealed class ImpactResult
{
    public required string SymbolId { get; init; }
    public required string DisplayName { get; init; }
    public string? File { get; init; }
    public int? Line { get; init; }
    public required int DirectFanIn { get; init; }
    public required int DepthScanned { get; init; }
    public required bool IsHub { get; init; }
    public required int RiskScore { get; init; }
    public required int ViaInterfaceCount { get; init; }
    public required int ViaMediatrCount { get; init; }
    public required List<ReachedEntryPoint> EntryPoints { get; init; }
    public required List<ReachedScreen> Screens { get; init; }
    public required List<string> TestsReached { get; init; }
    public required List<TicketFileRecord> RelatedTickets { get; init; }
    public required List<CoChangeRecord> CoChangingFiles { get; init; }
    public required List<string> BlindSpots { get; init; }
    public required List<CallerNode> IntermediateCallers { get; init; }
    public required Dictionary<string, int> ModuleFanIn { get; init; }

    /// <summary>callerId -> the node it was first discovered from during the BFS (points toward the root) — lets a renderer (slice's "Đường đi từ entry point") reconstruct a shortest path back to SymbolId.</summary>
    public required Dictionary<string, string> Predecessors { get; init; }

    /// <summary>Walks Predecessors from <paramref name="fromId"/> back to the root, inclusive of both ends.</summary>
    public List<string> GetPathToRoot(string fromId)
    {
        var path = new List<string> { fromId };
        var current = fromId;
        while (current != SymbolId && Predecessors.TryGetValue(current, out var next))
        {
            path.Add(next);
            current = next;
        }

        return path;
    }
}
