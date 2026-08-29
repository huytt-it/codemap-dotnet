using System.Text;
using CodeMap.Query.Models;
using CodeMap.Query.Reporting;

namespace CodeMap.Query.Map;

internal sealed class MapGenerator
{
    private readonly List<SymbolRecord> _symbols;
    private readonly List<EdgeRecord> _edges;
    private readonly DiagnosticsModel? _diagnostics;
    private readonly MetaModel? _meta;
    private readonly List<EntryPoint> _entryPoints;
    private readonly Dictionary<string, SymbolRecord> _byId;
    private readonly Dictionary<string, List<string>> _featuresByEntryPointId;

    public IReadOnlyList<string> Projects { get; }

    public MapGenerator(
        List<SymbolRecord> symbols, List<EdgeRecord> edges, DiagnosticsModel? diagnostics, MetaModel? meta = null,
        List<EntryPoint>? entryPoints = null, List<FrontendCall>? frontendCalls = null, List<ApiLink>? apiLinks = null)
    {
        _symbols = symbols;
        _edges = edges;
        _diagnostics = diagnostics;
        _meta = meta;
        _entryPoints = entryPoints ?? new();
        _byId = symbols.GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First());
        Projects = symbols.Select(s => s.Project).Distinct().OrderBy(p => p, StringComparer.Ordinal).ToList();
        _featuresByEntryPointId = BuildFeaturesByEntryPointId(apiLinks ?? new(), frontendCalls ?? new());
    }

    /// <summary>Spec section 8: entry point table must show "màn hình FE tương ứng" — joins api-links.jsonl (backendId) to frontend-calls.jsonl (feature), same join ImpactEngine.BuildScreens does for a single traversal, here precomputed for every entry point at once.</summary>
    private static Dictionary<string, List<string>> BuildFeaturesByEntryPointId(List<ApiLink> apiLinks, List<FrontendCall> frontendCalls)
    {
        var callsById = frontendCalls.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var link in apiLinks)
        {
            if (!callsById.TryGetValue(link.FrontendId, out var call)) continue;
            if (!result.TryGetValue(link.BackendId, out var features)) result[link.BackendId] = features = new();
            if (!features.Contains(call.Feature, StringComparer.Ordinal)) features.Add(call.Feature);
        }

        return result;
    }

    /// <summary>Hard constraint from the spec (section 8): MAP.md ≤ 500 lines, in EVERY case — the staleness banner (section 7.5) counts toward this too.</summary>
    private const int MaxMapLines = 500;

    public string BuildMapMarkdown(string? preservedHumanBlock)
    {
        var sb = new StringBuilder();
        sb.Append(StalenessBanner.Render(_meta));
        sb.AppendLine();
        sb.AppendLine("# CODEMAP");
        sb.AppendLine();
        sb.AppendLine($"_Auto-generated — {_symbols.Count} symbols, {_edges.Count} edges, {Projects.Count} project(s)._");
        sb.AppendLine();

        AppendProjectLayers(sb);
        AppendEntryPoints(sb);
        AppendHubs(sb);
        AppendBlindSpots(sb);

        sb.AppendLine("## Notes");
        sb.AppendLine();
        sb.AppendLine(HumanNotes.Block(preservedHumanBlock));

        return EnforceLineBudget(sb.ToString());
    }

    /// <summary>
    /// Final safety net: the sections above already cap themselves (project/hub/blind spots), but if the result
    /// still overflows (e.g. a very long hand-written note) we truncate hard instead of violating the constraint —
    /// truncating beats output that blows past what an AI can usefully read.
    /// </summary>
    private static string EnforceLineBudget(string content)
    {
        var lines = content.Split('\n');
        if (lines.Length <= MaxMapLines) return content;

        var truncated = lines.Take(MaxMapLines - 2)
            .Concat(new[] { "", $"_(Truncated — exceeds {MaxMapLines} lines. See index/ for the full data.)_" });
        return string.Join('\n', truncated);
    }

    public string BuildModuleMarkdown(string project, string? preservedHumanBlock)
    {
        var sb = new StringBuilder();
        sb.Append(StalenessBanner.Render(_meta));
        sb.AppendLine();
        sb.AppendLine($"# Module: {project}");
        sb.AppendLine();

        var symbolsInProject = _symbols.Where(s => s.Project == project).ToList();
        var types = symbolsInProject
            .Where(s => s.Kind is "Class" or "Interface" or "Struct" or "Enum" or "Record")
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .ToList();

        sb.AppendLine($"Total symbols: {symbolsInProject.Count} · Types: {types.Count}");
        sb.AppendLine();
        sb.AppendLine("## Types");
        sb.AppendLine();
        if (types.Count == 0)
        {
            sb.AppendLine("_None._");
        }
        else
        {
            foreach (var t in types)
                sb.AppendLine($"- **{t.Name}** ({t.Kind}) — {t.File}:{t.Line}");
        }

        sb.AppendLine();
        sb.AppendLine("## Notes");
        sb.AppendLine();
        sb.AppendLine(HumanNotes.Block(preservedHumanBlock));

        return sb.ToString();
    }

    /// <summary>Max projects listed in the table — keeps MAP.md from exploding on solutions with hundreds of projects.</summary>
    private const int ProjectTableCap = 100;

    private void AppendProjectLayers(StringBuilder sb)
    {
        var deps = BuildProjectDependencies();
        var layers = ComputeLayers(Projects, deps);

        sb.AppendLine("## Projects");
        sb.AppendLine();
        sb.AppendLine("| Project | Layer | Symbols | Depends on |");
        sb.AppendLine("|---|---|---|---|");
        var ordered = Projects.OrderBy(p => layers[p]).ThenBy(p => p, StringComparer.Ordinal).ToList();
        foreach (var p in ordered.Take(ProjectTableCap))
        {
            var count = _symbols.Count(s => s.Project == p);
            var refs = deps.TryGetValue(p, out var d) && d.Count > 0
                ? string.Join(", ", d.OrderBy(x => x, StringComparer.Ordinal))
                : "-";
            sb.AppendLine($"| {p} | {layers[p]} | {count} | {refs} |");
        }
        if (ordered.Count > ProjectTableCap)
            sb.AppendLine($"| _... and {ordered.Count - ProjectTableCap} more project(s) (see modules/)_ | | | |");

        sb.AppendLine();
        sb.AppendLine(
            "_Layer is inferred from cross-project edge direction in edges.jsonl (0 = no dependency on another " +
            "project in the solution). At L1 scan (inherits/implements edges only) this may be incomplete — it " +
            "gets more accurate after L2._");
        sb.AppendLine();
    }

    private Dictionary<string, HashSet<string>> BuildProjectDependencies()
    {
        var deps = Projects.ToDictionary(p => p, _ => new HashSet<string>(StringComparer.Ordinal));
        foreach (var e in _edges)
        {
            if (!_byId.TryGetValue(e.From, out var from)) continue;
            if (!_byId.TryGetValue(e.To, out var to)) continue;
            if (from.Project == to.Project) continue;
            deps[from.Project].Add(to.Project);
        }

        return deps;
    }

    private static Dictionary<string, int> ComputeLayers(IReadOnlyList<string> projects, Dictionary<string, HashSet<string>> deps)
    {
        var layers = new Dictionary<string, int>();
        var inProgress = new HashSet<string>();

        int Layer(string p)
        {
            if (layers.TryGetValue(p, out var cached)) return cached;
            if (!inProgress.Add(p)) return 0; // cycle guard

            var maxDep = -1;
            if (deps.TryGetValue(p, out var set))
                foreach (var d in set)
                    maxDep = Math.Max(maxDep, Layer(d));

            inProgress.Remove(p);
            var result = maxDep + 1;
            layers[p] = result;
            return result;
        }

        foreach (var p in projects) Layer(p);
        return layers;
    }

    /// <summary>Max entry points listed per project group — same "chống nổ report" spirit as impact's hub threshold; a project with hundreds of actions still gets a readable MAP.md.</summary>
    private const int EntryPointGroupCap = 30;

    /// <summary>Spec section 8: "bảng entry point gom theo project kèm màn hình FE tương ứng".</summary>
    private void AppendEntryPoints(StringBuilder sb)
    {
        sb.AppendLine($"## Entry Points ({_entryPoints.Count})");
        sb.AppendLine();

        if (_entryPoints.Count == 0)
        {
            sb.AppendLine("_None found — run `codemap scan` first (entrypoints.json is missing or empty)._");
            sb.AppendLine();
            return;
        }

        var byProject = _entryPoints
            .Select(ep => (EntryPoint: ep, Project: _byId.TryGetValue(ep.Id, out var sym) ? sym.Project : "?"))
            .GroupBy(x => x.Project, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal);

        foreach (var group in byProject)
        {
            sb.AppendLine($"### {group.Key} ({group.Count()})");
            sb.AppendLine();
            sb.AppendLine("| Type | Route | Symbol | FE screens |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var (ep, _) in group.OrderBy(x => x.EntryPoint.Type, StringComparer.Ordinal).ThenBy(x => x.EntryPoint.Id, StringComparer.Ordinal).Take(EntryPointGroupCap))
            {
                var route = ep.HttpMethod != null ? $"{ep.HttpMethod} {ep.Route}" : "-";
                var name = DisplayFromDocId(ep.Id);
                var features = _featuresByEntryPointId.TryGetValue(ep.Id, out var f) ? string.Join(", ", f) : "-";
                sb.AppendLine($"| {ep.Type} | {route} | {name} | {features} |");
            }
            if (group.Count() > EntryPointGroupCap)
                sb.AppendLine($"| _... and {group.Count() - EntryPointGroupCap} more (see modules/{SanitizeModuleName(group.Key)}.md)_ | | | |");
            sb.AppendLine();
        }
    }

    private static string SanitizeModuleName(string name)
        => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    private void AppendHubs(StringBuilder sb)
    {
        var fanIn = _edges
            .GroupBy(e => e.To)
            .Select(g => (Id: g.Key, Count: g.Count()))
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Id, StringComparer.Ordinal)
            .Take(20)
            .ToList();

        sb.AppendLine("## Hub (top 20 fan-in)");
        sb.AppendLine();
        if (fanIn.Count == 0)
        {
            sb.AppendLine("_Not enough edges yet to compute hubs._");
        }
        else
        {
            sb.AppendLine("| Symbol | Fan-in | Project |");
            sb.AppendLine("|---|---|---|");
            foreach (var (id, count) in fanIn)
            {
                var known = _byId.TryGetValue(id, out var sym);
                // Use the docId itself (kind prefix stripped) as the display name — always correct, never
                // reconstructed via ContainingType/Project (a top-level type has no ContainingType, so that
                // fallback would drop the real namespace).
                var name = DisplayFromDocId(id);
                var proj = known ? sym!.Project : "?";
                sb.AppendLine($"| {name} | {count} | {proj} |");
            }

            sb.AppendLine();
            sb.AppendLine("_Signature-change danger zone — high fan-in means a large blast radius._");
        }

        sb.AppendLine();
    }

    /// <summary>Max detail lines per group in "Blind Spots" — keeps MAP.md from exploding, same spirit as the impact hub threshold in spec section 7.</summary>
    private const int BlindSpotGroupCap = 25;

    private void AppendBlindSpots(StringBuilder sb)
    {
        sb.AppendLine("## Blind Spots");
        sb.AppendLine();

        var lines = new List<string>();
        if (_diagnostics != null)
        {
            const int degradedCap = 30;
            foreach (var d in _diagnostics.DegradedProjects.Take(degradedCap))
                lines.Add($"- Project `{d.Project}` could only be indexed at L1: {d.Reason}");
            if (_diagnostics.DegradedProjects.Count > degradedCap)
                lines.Add($"- ... and {_diagnostics.DegradedProjects.Count - degradedCap} more degraded project(s) (see diagnostics.json).");

            // Grouped by base type name instead of listing every call site — a type outside the solution (e.g.
            // Controller, Migration) can show up hundreds of times; a flat list would blow up MAP.md (observed
            // in practice: 642 lines on one repo).
            var byBaseType = _diagnostics.UnresolvedInheritance
                .GroupBy(u => u.BaseTypeName)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .ToList();

            foreach (var g in byBaseType.Take(BlindSpotGroupCap))
            {
                var example = g.First();
                var suffix = g.Count() > 1 ? $", +{g.Count() - 1} more" : "";
                lines.Add($"- Could not resolve base type `{g.Key}` ({g.Count()} occurrence(s)) — e.g. {example.File}:{example.Line}{suffix}");
            }
            if (byBaseType.Count > BlindSpotGroupCap)
                lines.Add(
                    $"- ... and {byBaseType.Count - BlindSpotGroupCap} more unresolved base type name(s) " +
                    $"(total {_diagnostics.UnresolvedInheritance.Count} occurrences — see diagnostics.json for the full list).");

            if (_diagnostics.DuplicateDocIdsAcrossProjects.Count > 0)
            {
                lines.Add(
                    $"- {_diagnostics.DuplicateDocIdsAcrossProjects.Count} docId(s) collide across different projects " +
                    "(two distinct types sharing one identifier because docId doesn't encode the assembly name) — " +
                    "`find`/`impact` may conflate these types; see diagnostics.json.");
                foreach (var dup in _diagnostics.DuplicateDocIdsAcrossProjects.Take(5))
                    lines.Add($"  - `{dup.Id}` in [{string.Join(", ", dup.Projects)}]");
            }
        }

        if (lines.Count == 0)
        {
            sb.AppendLine("_None._");
        }
        else
        {
            foreach (var l in lines) sb.AppendLine(l);
        }

        sb.AppendLine();
    }

    /// <summary>Display name derived from the docId — strips the kind prefix (T:/M:/F:/...), always correct since it comes straight from Roslyn instead of being reconstructed.</summary>
    private static string DisplayFromDocId(string id)
        => id.Length > 2 && id[1] == ':' ? id[2..] : id;
}
