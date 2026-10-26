using CodeMap.Query.Config;
using CodeMap.Query.Json;
using CodeMap.Query.Models;

namespace CodeMap.Roslyn.Scan;

/// <summary>
/// Everything SyntaxSymbolWalker finds across the trees of one scan, plus the solution-wide passes that need all
/// of it at once (expand-via-interface, MediatR Send -> Handle, di.json) and the index files they produce.
/// Shared by L2, L2's L1 fallback and plain L1 (--syntax-only): before, the two L1 paths kept only symbols and
/// inherits/implements edges, so a degraded project — or a whole --syntax-only index — had no call edge, entry
/// point or DI binding, and impact reported "no callers" for code that plainly had them.
/// </summary>
internal sealed class ScanResults
{
    public List<SymbolRecord> Symbols { get; } = new();
    public List<EdgeRecord> Edges { get; } = new();
    public List<EntryPoint> EntryPoints { get; } = new();

    private readonly List<(string InterfaceId, string ImplId)> _interfaceImpls = new();
    private readonly List<(string InterfaceMemberId, string ImplMemberId)> _interfaceMemberMaps = new();
    private readonly List<(string OverriddenId, string OverrideId, List<string> OverrideTypeAndBaseIds)> _overrideMaps = new();
    private readonly List<(EdgeRecord Edge, string ReceiverTypeId)> _virtualCalls = new();
    private readonly List<(string ServiceId, string ImplId)> _diRegistrations = new();
    private readonly List<(string ServiceId, string ImplId)> _attributeDiBindings = new();
    private readonly List<(string RequestTypeId, string HandleMethodId)> _mediatrHandlerMaps = new();
    private readonly List<(string FromId, string ConstructedTypeId, string File, int Line)> _mediatrSendSites = new();

    /// <summary>codemap.config.json from the solution directory upward; a malformed one is reported and replaced by defaults rather than failing the scan.</summary>
    public static CodeMapConfig LoadConfig(string solutionDir)
    {
        try
        {
            return CodeMapConfig.Load(solutionDir);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: {ex.Message}. Continuing with default configuration (DI-attribute detection disabled).");
            return new CodeMapConfig();
        }
    }

    public void Add(SyntaxSymbolWalker walker, string project, DiagnosticsModel diagnostics, string unresolvedBaseTypeReason)
    {
        Symbols.AddRange(walker.Symbols);
        Edges.AddRange(walker.DirectEdges);
        EntryPoints.AddRange(walker.EntryPoints);
        _interfaceImpls.AddRange(walker.InterfaceImplementations);
        _interfaceMemberMaps.AddRange(walker.InterfaceMemberMappings);
        _overrideMaps.AddRange(walker.OverrideMappings);
        _virtualCalls.AddRange(walker.VirtualCalls);
        _diRegistrations.AddRange(walker.DiRegistrations);
        _attributeDiBindings.AddRange(walker.AttributeDiBindings);
        _mediatrHandlerMaps.AddRange(walker.MediatrHandlerMappings);
        _mediatrSendSites.AddRange(walker.MediatrSendSites);

        foreach (var u in walker.UnresolvedBaseRefs)
            diagnostics.UnresolvedInheritance.Add(new UnresolvedInheritance(
                u.Project, u.File, u.Line, u.FromDocId, u.BaseSimpleName, unresolvedBaseTypeReason));
        foreach (var d in walker.UnresolvedDiRegistrations)
            diagnostics.UnresolvedDiRegistrations.Add(new UnresolvedDiRegistration(project, d.File, d.Line, d.Reason));
        foreach (var (typeId, candidates) in walker.AmbiguousDiTypes)
            diagnostics.AmbiguousDiTypes.Add(new AmbiguousDiType(typeId, candidates));
    }

    /// <summary>
    /// Runs the solution-wide passes, then writes symbols.jsonl, edges.jsonl, di.json, di-confirmed.json,
    /// entrypoints.json and diagnostics.json (last, since the passes add to it). meta.json stays with the caller.
    /// Returns the number of DI-mapped services, for the scan summary line.
    /// </summary>
    public int CompleteAndWrite(string indexDir, CodeMapConfig config, DiagnosticsModel diagnostics)
    {
        ExpandCallsThroughInterfaces(Edges, _interfaceMemberMaps);
        ExpandCallsThroughOverrides(Edges, _virtualCalls, _overrideMaps);

        DetectDiRegistrationConflicts(_attributeDiBindings, _diRegistrations, diagnostics);
        var (diMap, diConfirmedMap) = BuildDiJson(_interfaceImpls, _diRegistrations, _attributeDiBindings, config.DiManualOverrides, diagnostics);
        AddCrossProjectDuplicateDiagnostics(Symbols, diagnostics);
        ExpandMediatrSendEdges(Edges, _mediatrSendSites, _mediatrHandlerMaps);

        JsonlWriter.Write(Path.Combine(indexDir, "symbols.jsonl"), Symbols);
        JsonlWriter.Write(Path.Combine(indexDir, "edges.jsonl"), Edges);
        JsonUtil.WriteIndented(Path.Combine(indexDir, "di.json"), diMap);
        JsonUtil.WriteIndented(Path.Combine(indexDir, "di-confirmed.json"), diConfirmedMap);
        JsonUtil.WriteIndented(Path.Combine(indexDir, "diagnostics.json"), diagnostics);
        JsonUtil.WriteIndented(Path.Combine(indexDir, "entrypoints.json"), EntryPoints);
        return diMap.Count;
    }

    /// <summary>Resolves each `mediator.Send(new FooCommand())` / `.Publish(new FooEvent())` call site to every matching handler's Handle method — a notification usually has several — emitting a `call` edge marked via:"mediatr" to each (spec section 5, "Entry point").</summary>
    private static void ExpandMediatrSendEdges(
        List<EdgeRecord> edges,
        List<(string FromId, string ConstructedTypeId, string File, int Line)> sendSites,
        List<(string RequestTypeId, string HandleMethodId)> handlerMappings)
    {
        var handleMethodsByRequestType = handlerMappings
            .GroupBy(m => m.RequestTypeId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(m => m.HandleMethodId).Distinct(StringComparer.Ordinal).ToList(), StringComparer.Ordinal);

        foreach (var (fromId, constructedTypeId, file, line) in sendSites)
        {
            if (!handleMethodsByRequestType.TryGetValue(constructedTypeId, out var handleIds)) continue;
            foreach (var handleId in handleIds)
                edges.Add(new EdgeRecord { From = fromId, To = handleId, Kind = "call", File = file, Line = line, Via = "mediatr" });
        }
    }

    /// <summary>Duplicates each `call` edge that targets an interface member into a new edge at each concrete implementation, marked via:"interface". Keeps the original edge too.</summary>
    private static void ExpandCallsThroughInterfaces(List<EdgeRecord> edges, List<(string InterfaceMemberId, string ImplMemberId)> mappings)
    {
        var byInterfaceMember = mappings
            .GroupBy(m => m.InterfaceMemberId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(m => m.ImplMemberId).Distinct(StringComparer.Ordinal).ToList(), StringComparer.Ordinal);

        var expanded = new List<EdgeRecord>();
        foreach (var edge in edges)
        {
            if (edge.Kind != "call") continue;
            if (!byInterfaceMember.TryGetValue(edge.To, out var implIds)) continue;

            foreach (var implId in implIds)
            {
                if (implId == edge.To) continue; // the edge already targets this exact member (rare/defensive)
                expanded.Add(new EdgeRecord { From = edge.From, To = implId, Kind = "call", File = edge.File, Line = edge.Line, Via = "interface" });
            }
        }

        edges.AddRange(expanded);
    }

    /// <summary>
    /// The class-hierarchy counterpart of ExpandCallsThroughInterfaces: a virtual call — `BaseRepository r;
    /// r.Save()`, or `Save()` inside the base itself — binds to the abstract/virtual member, but what runs is an
    /// override. Adds a `call` edge marked via:"override" to every override below the called member, transitively,
    /// whose type is the receiver's static type or derives from it; keeps the original edge too. Only walker-direct
    /// edges are expanded (the walker records which ones dispatch virtually; `base.M()` doesn't), so an edge the
    /// interface pass produced is never expanded a second time — that pass already maps each implementing type to
    /// its own override.
    /// </summary>
    private static void ExpandCallsThroughOverrides(
        List<EdgeRecord> edges,
        List<(EdgeRecord Edge, string ReceiverTypeId)> virtualCalls,
        List<(string OverriddenId, string OverrideId, List<string> OverrideTypeAndBaseIds)> mappings)
    {
        var overridesOf = mappings
            .GroupBy(m => m.OverriddenId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var expanded = new List<EdgeRecord>();
        foreach (var (edge, receiverTypeId) in virtualCalls)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal) { edge.To };
            var pending = new Queue<string>();
            pending.Enqueue(edge.To);
            while (pending.TryDequeue(out var member))
            {
                if (!overridesOf.TryGetValue(member, out var overrides)) continue;
                foreach (var (_, overrideId, overrideTypeAndBaseIds) in overrides)
                {
                    if (!seen.Add(overrideId)) continue;
                    pending.Enqueue(overrideId); // a type outside the receiver's subtree may still have one below it
                    if (overrideTypeAndBaseIds.Contains(receiverTypeId, StringComparer.Ordinal))
                        expanded.Add(new EdgeRecord { From = edge.From, To = overrideId, Kind = "call", File = edge.File, Line = edge.Line, Via = "override" });
                }
            }
        }

        edges.AddRange(expanded);
    }

    /// <summary>
    /// di.json (spec section 4) merges structural "implements" data with real DI evidence, by design — it's
    /// meant as a best-guess reference even for types with no explicit registration anywhere. But that merge
    /// makes di.json unusable as ground truth for "is this actually the DI-bound implementation" (found via
    /// docs/BENCHMARK-INTERFACE-EXPANSION.md's audit: every interface-expand candidate is, by construction,
    /// also a "structural" implementer, so di.json alone can never disagree with an expanded edge). di-confirmed.json
    /// is the same shape but built from ONLY real evidence (fluent AddScoped/AddSingleton/AddTransient, the
    /// [Injectable]-attribute convention, and manual overrides) — no structural fallback — so ImpactEngine can
    /// tell "confirmed" apart from "merely implements the interface" when judging via:"interface" edges.
    /// </summary>
    private static (SortedDictionary<string, List<string>> Full, SortedDictionary<string, List<string>> ConfirmedOnly) BuildDiJson(
        List<(string InterfaceId, string ImplId)> structural,
        List<(string ServiceId, string ImplId)> diRegistrations,
        List<(string ServiceId, string ImplId)> attributeBindings,
        Dictionary<string, string>? manualOverrides,
        DiagnosticsModel diagnostics)
    {
        var full = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        var confirmedOnly = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);

        void Add(SortedDictionary<string, List<string>> map, string key, string value)
        {
            if (!map.TryGetValue(key, out var list))
            {
                list = new List<string>();
                map[key] = list;
            }

            if (!list.Contains(value, StringComparer.Ordinal))
                list.Add(value);
        }

        foreach (var (interfaceId, implId) in structural) Add(full, interfaceId, implId);
        foreach (var (serviceId, implId) in diRegistrations) { Add(full, serviceId, implId); Add(confirmedOnly, serviceId, implId); }
        foreach (var (serviceId, implId) in attributeBindings) { Add(full, serviceId, implId); Add(confirmedOnly, serviceId, implId); }

        // Manual overrides (spec section 5, P10) always win, and apply beyond just ambiguousDiTypes — resolve
        // them here, then drop the resolved type from the ambiguous-diagnostics list.
        if (manualOverrides is { Count: > 0 })
        {
            foreach (var (typeId, interfaceId) in manualOverrides)
            {
                Add(full, interfaceId, typeId);
                Add(confirmedOnly, interfaceId, typeId);
            }

            var overriddenTypes = new HashSet<string>(manualOverrides.Keys, StringComparer.Ordinal);
            diagnostics.AmbiguousDiTypes.RemoveAll(a => overriddenTypes.Contains(a.TypeDocId));
        }

        foreach (var list in full.Values) list.Sort(StringComparer.Ordinal);
        foreach (var list in confirmedOnly.Values) list.Sort(StringComparer.Ordinal);
        return (full, confirmedOnly);
    }

    /// <summary>The attribute-convention source and the fluent AddScoped/AddSingleton/AddTransient source disagreeing about a type's bound interface — spec section 5, P10.</summary>
    private static void DetectDiRegistrationConflicts(
        List<(string ServiceId, string ImplId)> attributeBindings,
        List<(string ServiceId, string ImplId)> diRegistrations,
        DiagnosticsModel diagnostics)
    {
        var attributeByType = attributeBindings
            .Where(b => b.ServiceId != b.ImplId) // exclude self-registration — nothing to conflict against
            .GroupBy(b => b.ImplId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().ServiceId, StringComparer.Ordinal);

        var fluentByType = diRegistrations
            .GroupBy(r => r.ImplId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(r => r.ServiceId).Distinct(StringComparer.Ordinal).ToList(), StringComparer.Ordinal);

        foreach (var (typeId, attrInterface) in attributeByType)
        {
            if (fluentByType.TryGetValue(typeId, out var fluentInterfaces) &&
                !fluentInterfaces.Contains(attrInterface, StringComparer.Ordinal))
            {
                diagnostics.DiRegistrationConflicts.Add(new DiRegistrationConflict(typeId, attrInterface, fluentInterfaces));
            }
        }
    }

    /// <summary>
    /// docId doesn't encode the assembly name, so two fully-qualified-name-identical types in two different
    /// projects (legal in .NET) collide on docId — at L1 inside the merged compilation, at L2 across project
    /// compilations alike. An inherent limitation of docId itself, not a merging bug: log it transparently
    /// instead of letting symbols.jsonl carry two records sharing an "id" unnoticed.
    /// </summary>
    private static void AddCrossProjectDuplicateDiagnostics(List<SymbolRecord> allSymbols, DiagnosticsModel diagnostics)
    {
        var crossProjectDuplicates = allSymbols
            .GroupBy(s => s.Id, StringComparer.Ordinal)
            .Where(g => g.Select(s => s.Project).Distinct().Count() > 1)
            .Select(g => new DuplicateDocId(
                g.Key,
                g.Select(s => s.Project).Distinct().ToList(),
                g.Select(s => s.File).Distinct().ToList()))
            .ToList();
        diagnostics.DuplicateDocIdsAcrossProjects.AddRange(crossProjectDuplicates);
    }
}
