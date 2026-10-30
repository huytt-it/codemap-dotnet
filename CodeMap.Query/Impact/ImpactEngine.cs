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
                queue.Enqueue(edge.From);
            }
        }

        var unconfirmedBinding = ComputeUnconfirmedBinding(index, symbolId, visitedDepth.Keys, edgesUsed);

        var (directFanIn, directCallersInferred) = CountDirectCallers(index, symbolId);

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
                entryPoints.Add(new ReachedEntryPoint(id, displayName, ep.Type, ep.HttpMethod, ep.Route, project, d, isConfirmed, ep.RouteInferred == true));
            else if (TestProjectName.IsTestProject(project))
                testsReached.Add(displayName);
            else
                intermediateCallers.Add(new CallerNode(id, displayName, project, d, isConfirmed));
        }

        // Risk, hub and the per-module count are judged on the confirmed part: an entry point reachable only
        // through an implementation DI does not bind at that call site is real but speculative (decorator
        // patterns especially), and one wide interface would otherwise tip a symbol into "hub" or a 10/10 score
        // by itself. The unconfirmed ones are still in EntryPoints and the renderers still show them.
        var confirmedEntryPoints = entryPoints.Where(e => e.IsConfirmedBinding).ToList();

        var moduleFanIn = confirmedEntryPoints
            .GroupBy(e => e.Project, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var viaInterfaceCount = edgesUsed.Count(e => e.Via == "interface");
        var viaInterfaceUnconfirmedCount = edgesUsed.Count(e => e.Via == "interface" && !IsConfirmedInterfaceEdge(index, e));
        var viaMediatrCount = edgesUsed.Count(e => e.Via == "mediatr");
        var viaOverrideCount = edgesUsed.Count(e => e.Via == "override");
        var viaDelegateCount = edgesUsed.Count(e => e.Via == "delegate");

        var rootFile = rootSymbol?.File;
        var relatedTickets = rootFile == null
            ? new List<TicketFileRecord>()
            : index.Tickets.Where(t => t.Files.Contains(rootFile, FilePathComparison.Comparer)).ToList();
        var coChanging = rootFile == null
            ? new List<CoChangeRecord>()
            : index.CoChanges.Where(c => FilePathComparison.Comparer.Equals(c.FileA, rootFile) || FilePathComparison.Comparer.Equals(c.FileB, rootFile)).ToList();

        var screens = BuildScreens(index, entryPoints);

        var blindSpots = BuildBlindSpots(
            index, visitedDepth.Keys, rootSymbol, viaMediatrCount, viaInterfaceCount, viaInterfaceUnconfirmedCount, screens,
            inferredRouteCount: entryPoints.Count(e => e.RouteInferred), overrideCount: viaOverrideCount, delegateCount: viaDelegateCount);

        // Override hops have no DI evidence to confirm or rule them out, so all of them count, as unchecked interface hops do.
        // Callers reached but none an entry point (of any binding) or a test: nothing to weigh, so no score rather
        // than a 0 that reads as "safe". A symbol with no callers at all is a leaf, and keeps its 0.
        int? riskScore = entryPoints.Count == 0 && testsReached.Count == 0 && intermediateCallers.Count > 0
            ? null
            : ComputeRiskScore(
                confirmedEntryPoints.Count, testsReached.Count, viaInterfaceCount - viaInterfaceUnconfirmedCount + viaOverrideCount);

        return new ImpactResult
        {
            SymbolId = symbolId,
            DisplayName = DisplayNameOf(symbolId, rootSymbol),
            File = rootSymbol?.File,
            Line = rootSymbol?.Line,
            DirectFanIn = directFanIn,
            DirectCallersInferred = directCallersInferred,
            DepthScanned = depth,
            IsHub = confirmedEntryPoints.Count > HubEntryPointThreshold,
            RiskScore = riskScore,
            ViaInterfaceCount = viaInterfaceCount,
            ViaInterfaceUnconfirmedCount = viaInterfaceUnconfirmedCount,
            ViaMediatrCount = viaMediatrCount,
            ViaOverrideCount = viaOverrideCount,
            ViaDelegateCount = viaDelegateCount,
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
    /// Callers, not edges: the reverse-edge list also holds every extra call site of the same caller, field
    /// reads/writes, inherits/implements edges, and the interface-expansion copies, so its length says little
    /// about how many methods depend on the symbol. Counts distinct <c>From</c> over call and construction edges
    /// (a constructor's only incoming edges are <c>new</c>), leaving out the symbol calling itself. Of those,
    /// reports how many callers have nothing but expanded edges into the symbol (see <see cref="IsExpansion"/>).
    /// </summary>
    private static (int Callers, int InferredOnly) CountDirectCallers(ImpactIndex index, string symbolId)
    {
        if (!index.ReverseEdges.TryGetValue(symbolId, out var incoming)) return (0, 0);

        var byCaller = incoming
            .Where(e => e.Kind is "call" or "new" && e.From != symbolId)
            .GroupBy(e => e.From, StringComparer.Ordinal)
            .ToList();
        return (byCaller.Count, byCaller.Count(g => g.All(e => IsExpansion(e.Via))));
    }

    /// <summary>
    /// An edge the scan inferred rather than read from the caller's code: one call copied to every implementation
    /// (interface) or override (override), or a Send/Publish joined to its handlers by convention (mediatr). A
    /// via:"delegate" edge is not one of them: passing a method as a delegate is written in the caller's code.
    /// </summary>
    private static bool IsExpansion(string? via) => via is "interface" or "mediatr" or "override";

    /// <summary>
    /// Approximation, not a spec formula (none is given): weights entry points highest (that's what actually
    /// breaks when this changes), a smaller weight for expanded hops through an interface or an override (real
    /// risk, but the runtime might resolve a different implementation than the static expansion), tests nudge
    /// it down a little (a guard rail exists).
    /// </summary>
    private static int ComputeRiskScore(int entryPointCount, int testCount, int expandedHopCount)
    {
        if (entryPointCount == 0 && testCount == 0) return 0;
        var score = entryPointCount * 0.6 + expandedHopCount * 0.15 - (testCount > 0 ? 0.5 : 0);
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
    /// <summary>
    /// A node is confirmed if AT LEAST ONE path from the root to it, anywhere in the reached subgraph, uses only
    /// confirmed edges — not merely the specific path the outer BFS in <see cref="Traverse"/> happened to
    /// discover it through first. The original approach tainted a node the instant ANY unconfirmed path reached
    /// it, and, because a node is only ever discovered (and enqueued) once, a later confirmed path to that same
    /// node was silently ignored — so the result depended on which of several equally real paths the BFS visited
    /// first, and a node with a perfectly good confirmed calling path could be mislabeled "other possible
    /// implementation" (docs/BENCHMARK-INTERFACE-EXPANSION.md) just because an unconfirmed one was enumerated
    /// earlier.
    ///
    /// This is a second, independent propagation over the same edge set the BFS already collected
    /// (<paramref name="edgesUsed"/> — every edge between two reached nodes, regardless of which one first
    /// discovered either endpoint): starting confirmed at the root, follow only confirmed edges outward, and
    /// keep enqueuing newly-confirmed nodes until nothing changes. Order-independent by construction — once a
    /// node is marked confirmed it stays that way, so it no longer matters which edge got processed first.
    /// </summary>
    private static HashSet<string> ComputeUnconfirmedBinding(ImpactIndex index, string symbolId, IEnumerable<string> reachedIds, List<EdgeRecord> edgesUsed)
    {
        var edgesByTarget = edgesUsed.GroupBy(e => e.To, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var confirmed = new HashSet<string>(StringComparer.Ordinal) { symbolId };
        var queue = new Queue<string>();
        queue.Enqueue(symbolId);

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (!edgesByTarget.TryGetValue(node, out var outgoing)) continue;

            foreach (var edge in outgoing)
            {
                if (confirmed.Contains(edge.From)) continue;
                var edgeConfirmed = edge.Via != "interface" || IsConfirmedInterfaceEdge(index, edge);
                if (!edgeConfirmed) continue;
                confirmed.Add(edge.From);
                queue.Enqueue(edge.From);
            }
        }

        return reachedIds.Where(id => !confirmed.Contains(id)).ToHashSet(StringComparer.Ordinal);
    }

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
        int unconfirmedInterfaceCount, List<ReachedScreen> screens, int inferredRouteCount, int overrideCount, int delegateCount)
    {
        var spots = new List<string>();

        var lowConfidenceScreens = screens.Count(s => s.Confidence == "low");
        if (lowConfidenceScreens > 0)
            spots.Add($"{lowConfidenceScreens} FE screen(s) detected via low-confidence jQuery URL parsing (spec: dynamic URLs often can't be fully resolved) — may be incomplete or wrong.");
        var ambiguousScreens = screens.Count(s => s.MatchKind == "ambiguous");
        if (ambiguousScreens > 0)
            spots.Add($"{ambiguousScreens} FE screen link(s) are ambiguous — the normalized route matched more than one backend endpoint.");
        var suffixScreens = screens.Count(s => s.MatchKind == "suffix");
        if (suffixScreens > 0)
            spots.Add($"{suffixScreens} FE screen link(s) matched by route suffix only — the frontend URL starts with a base-URL variable the scan could not resolve, so the missing prefix (such as api/) was assumed.");
        if (inferredRouteCount > 0)
            spots.Add($"{inferredRouteCount} HTTP entry point(s) have a route inferred from the default convention ({{controller}}/{{action}}/{{id?}}), not declared by an attribute — the app's real route table may differ, and FE links to them rest on the same guess.");

        if (rootSymbol == null)
            spots.Add("This symbol was not found in symbols.jsonl — the docId may be stale (re-run `codemap find`), or the index is out of date.");

        if (mediatrCount > 0)
            spots.Add($"{mediatrCount} edge(s) go through MediatR (inferred by convention: mediator.Send/.Publish with an inline `new` argument).");
        if (interfaceCount > 0)
            spots.Add($"{interfaceCount} edge(s) go through an interface (expanded to every known implementation — a DI container could resolve a different one at runtime).");
        if (overrideCount > 0)
            spots.Add($"{overrideCount} edge(s) go through a virtual or abstract call (expanded to every override at or below the receiver's declared type — at runtime the object's actual type decides which one runs).");
        if (delegateCount > 0)
            spots.Add($"{delegateCount} edge(s) pass a method as a delegate or event handler — it runs only when that delegate is invoked, which may be later or never.");
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
