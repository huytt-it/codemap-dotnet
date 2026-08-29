using CodeMap.Query.Models;

namespace CodeMap.Query.Impact;

/// <summary>
/// Spec section 7: "một engine, nhiều renderer". BFS over the reverse call graph (edges.jsonl, "who calls this")
/// up to <paramref name="depth"/>, tiering reached symbols into entry points / tests / plain intermediate
/// callers. Pure function of (index, symbolId, depth) — no I/O, no rendering decisions (--full is a renderer
/// concern: this always computes IntermediateCallers, the caller decides whether to print them).
/// </summary>
public static class ImpactEngine
{
    /// <summary>Spec section 7: "nếu số entry point đạt tới được vượt 30" → hub mode.</summary>
    public const int HubEntryPointThreshold = 30;

    public static ImpactResult Traverse(ImpactIndex index, string symbolId, int depth)
    {
        index.SymbolsById.TryGetValue(symbolId, out var rootSymbol);

        var visitedDepth = new Dictionary<string, int>(StringComparer.Ordinal) { [symbolId] = 0 };
        var predecessors = new Dictionary<string, string>(StringComparer.Ordinal);
        var edgesUsed = new List<EdgeRecord>();
        // Nodes FIRST discovered through a via:"interface" edge docs/BENCHMARK-INTERFACE-EXPANSION.md's audit
        // could positively confirm is over-inference (di-confirmed.json shows a DIFFERENT sibling implementation
        // is the one actually DI-bound at that call site) — never populated for "we don't know" cases, only
        // provably-wrong ones, so this can't cry wolf on a type with no DI registration info at all.
        var unconfirmedBinding = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue(symbolId);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            var currentDepth = visitedDepth[current];
            if (currentDepth >= depth) continue;
            if (!index.ReverseEdges.TryGetValue(current, out var incoming)) continue;

            foreach (var edge in incoming)
            {
                edgesUsed.Add(edge);
                if (visitedDepth.ContainsKey(edge.From)) continue;
                visitedDepth[edge.From] = currentDepth + 1;
                predecessors[edge.From] = current;
                // Taint propagates forward through the BFS: if `current` itself was only reached via an
                // unconfirmed interface hop, everything reached FROM it (regardless of THIS edge's own kind)
                // is equally speculative — the whole path back to the target depends on that earlier hop
                // being real, which it isn't confirmed to be.
                var thisEdgeUnconfirmed = edge.Via == "interface" && !IsConfirmedInterfaceEdge(index, edge);
                if (thisEdgeUnconfirmed || unconfirmedBinding.Contains(current))
                    unconfirmedBinding.Add(edge.From);
                queue.Enqueue(edge.From);
            }
        }

        var directFanIn = index.ReverseEdges.TryGetValue(symbolId, out var direct) ? direct.Count : 0;

        var entryPoints = new List<ReachedEntryPoint>();
        var testsReached = new List<string>();
        var intermediateCallers = new List<CallerNode>();

        foreach (var (id, d) in visitedDepth)
        {
            if (id == symbolId) continue;

            var sym = index.SymbolsById.GetValueOrDefault(id);
            var displayName = DisplayNameOf(id, sym);
            var project = sym?.Project ?? "?";

            var isConfirmed = !unconfirmedBinding.Contains(id);
            if (index.EntryPointsById.TryGetValue(id, out var ep))
                entryPoints.Add(new ReachedEntryPoint(id, displayName, ep.Type, ep.HttpMethod, ep.Route, project, d, isConfirmed));
            else if (project.Contains("Test", StringComparison.OrdinalIgnoreCase))
                testsReached.Add(displayName);
            else
                intermediateCallers.Add(new CallerNode(id, displayName, project, d, isConfirmed));
        }

        var moduleFanIn = entryPoints
            .GroupBy(e => e.Project, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var viaInterfaceCount = edgesUsed.Count(e => e.Via == "interface");
        var viaInterfaceUnconfirmedCount = edgesUsed.Count(e => e.Via == "interface" && !IsConfirmedInterfaceEdge(index, e));
        var viaMediatrCount = edgesUsed.Count(e => e.Via == "mediatr");

        var rootFile = rootSymbol?.File;
        var relatedTickets = rootFile == null
            ? new List<TicketFileRecord>()
            : index.Tickets.Where(t => t.Files.Contains(rootFile, StringComparer.Ordinal)).ToList();
        var coChanging = rootFile == null
            ? new List<CoChangeRecord>()
            : index.CoChanges.Where(c => c.FileA == rootFile || c.FileB == rootFile).ToList();

        var screens = BuildScreens(index, entryPoints);

        var blindSpots = BuildBlindSpots(index, visitedDepth.Keys, rootSymbol, viaMediatrCount, viaInterfaceCount, viaInterfaceUnconfirmedCount, screens);

        var riskScore = ComputeRiskScore(entryPoints.Count, testsReached.Count, viaInterfaceCount);

        return new ImpactResult
        {
            SymbolId = symbolId,
            DisplayName = DisplayNameOf(symbolId, rootSymbol),
            File = rootSymbol?.File,
            Line = rootSymbol?.Line,
            DirectFanIn = directFanIn,
            DepthScanned = depth,
            IsHub = entryPoints.Count > HubEntryPointThreshold,
            RiskScore = riskScore,
            ViaInterfaceCount = viaInterfaceCount,
            ViaMediatrCount = viaMediatrCount,
            EntryPoints = entryPoints.OrderBy(e => e.Project, StringComparer.Ordinal).ThenBy(e => e.DisplayName, StringComparer.Ordinal).ToList(),
            Screens = screens,
            TestsReached = testsReached.OrderBy(t => t, StringComparer.Ordinal).ToList(),
            RelatedTickets = relatedTickets,
            CoChangingFiles = coChanging,
            BlindSpots = blindSpots,
            IntermediateCallers = intermediateCallers.OrderBy(c => c.Depth).ThenBy(c => c.DisplayName, StringComparer.Ordinal).ToList(),
            ModuleFanIn = moduleFanIn,
            Predecessors = predecessors,
        };
    }

    /// <summary>
    /// Approximation, not a spec formula (none is given): weights entry points highest (that's what actually
    /// breaks when this changes), a smaller weight for interface hops (real risk, but a DI container might
    /// resolve differently than the static expansion), tests nudge it down a little (a guard rail exists).
    /// </summary>
    private static int ComputeRiskScore(int entryPointCount, int testCount, int viaInterfaceCount)
    {
        if (entryPointCount == 0 && testCount == 0) return 0;
        var score = entryPointCount * 0.6 + viaInterfaceCount * 0.15 - (testCount > 0 ? 0.5 : 0);
        return Math.Clamp((int)Math.Ceiling(score), entryPointCount > 0 ? 1 : 0, 10);
    }

    /// <summary>Spec section 7's "màn hình FE" field — post-processes the already-computed EntryPoints, looking up api-links.jsonl for each `http` one. Not part of the BFS itself: FE calls aren't nodes in the call graph, they're joined in via the (frontendId, backendId) pairs `link` produced.</summary>
    private static List<ReachedScreen> BuildScreens(ImpactIndex index, List<ReachedEntryPoint> entryPoints)
    {
        var screens = new List<ReachedScreen>();
        foreach (var ep in entryPoints.Where(e => e.Type == "http"))
        {
            if (!index.ApiLinksByBackendId.TryGetValue(ep.Id, out var links)) continue;
            foreach (var link in links)
            {
                if (!index.FrontendCallsById.TryGetValue(link.FrontendId, out var call)) continue;
                screens.Add(new ReachedScreen(call.Feature, call.File, call.Line, call.HttpMethod, call.Route, call.Confidence, link.MatchKind, ep.Id, call.InjectedBy));
            }
        }

        return screens
            .OrderBy(s => s.Feature, StringComparer.Ordinal)
            .ThenBy(s => s.FrontendFile, StringComparer.Ordinal)
            .ThenBy(s => s.FrontendLine)
            .ToList();
    }

    /// <summary>
    /// Docs/BENCHMARK-INTERFACE-EXPANSION.md: a via:"interface" edge is only flagged as unconfirmed when there
    /// is POSITIVE evidence it's wrong — di-confirmed.json (real DI registrations, never the structural
    /// fallback di.json also carries) names a DIFFERENT sibling implementation at the same call site. If the
    /// interface has no confirmed binding at all (assembly-scanning DI, or never registered), this returns
    /// true — "unknown" must not be treated the same as "known wrong".
    /// </summary>
    private static bool IsConfirmedInterfaceEdge(ImpactIndex index, EdgeRecord edge)
    {
        var key = $"{edge.From}|{edge.File}|{edge.Line}";
        if (!index.InterfaceCallSiteCandidateTypes.TryGetValue(key, out var candidates)) return true;

        var confirmedInGroup = candidates.Where(index.ConfirmedImplementationTypes.Contains).ToList();
        if (confirmedInGroup.Count == 0) return true; // no DI evidence at all for this call site - not a known wrong answer

        var implType = index.SymbolsById.GetValueOrDefault(edge.To)?.ContainingType;
        return implType != null && confirmedInGroup.Contains(implType);
    }

    private static string DisplayNameOf(string id, SymbolRecord? sym)
    {
        if (sym == null) return StripDocIdPrefix(id);
        return sym.ContainingType != null ? $"{sym.ContainingType}.{sym.Name}" : sym.Name;
    }

    private static string StripDocIdPrefix(string id) => id.Length > 2 && id[1] == ':' ? id[2..] : id;

    private static List<string> BuildBlindSpots(
        ImpactIndex index, IEnumerable<string> reachedIds, SymbolRecord? rootSymbol, int mediatrCount, int interfaceCount,
        int unconfirmedInterfaceCount, List<ReachedScreen> screens)
    {
        var spots = new List<string>();

        var lowConfidenceScreens = screens.Count(s => s.Confidence == "low");
        if (lowConfidenceScreens > 0)
            spots.Add($"{lowConfidenceScreens} FE screen(s) detected via low-confidence jQuery URL parsing (spec: dynamic URLs often can't be fully resolved) — may be incomplete or wrong.");
        var ambiguousScreens = screens.Count(s => s.MatchKind == "ambiguous");
        if (ambiguousScreens > 0)
            spots.Add($"{ambiguousScreens} FE screen link(s) are ambiguous — the normalized route matched more than one backend endpoint.");

        if (rootSymbol == null)
            spots.Add("This symbol was not found in symbols.jsonl — the docId may be stale (re-run `codemap find`), or the index is out of date.");

        if (mediatrCount > 0)
            spots.Add($"{mediatrCount} edge(s) go through MediatR (inferred by convention: mediator.Send/.Publish with an inline `new` argument).");
        if (interfaceCount > 0)
            spots.Add($"{interfaceCount} edge(s) go through an interface (expanded to every known implementation — a DI container could resolve a different one at runtime).");
        if (unconfirmedInterfaceCount > 0)
            spots.Add(
                $"{unconfirmedInterfaceCount} of those interface edge(s) point at an implementation di-confirmed.json shows is NOT the one actually DI-bound at that call site (commonly a decorator pattern — see docs/BENCHMARK-INTERFACE-EXPANSION.md) — marked \"other possible implementation\" below, not the confirmed path.");

        if (index.Diagnostics != null)
        {
            var reachedProjects = reachedIds
                .Select(id => index.SymbolsById.GetValueOrDefault(id)?.Project)
                .Where(p => p != null)
                .Select(p => p!)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var degraded in index.Diagnostics.DegradedProjects)
                if (reachedProjects.Contains(degraded.Project))
                    spots.Add($"Project `{degraded.Project}` was only indexed at L1: {degraded.Reason}");
        }

        if (index.Meta == null)
            spots.Add("No meta.json found — this index's freshness relative to the current code can't be determined.");

        return spots;
    }
}
