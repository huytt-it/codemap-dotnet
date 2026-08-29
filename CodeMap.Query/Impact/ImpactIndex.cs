using CodeMap.Query.Json;
using CodeMap.Query.Models;

namespace CodeMap.Query.Impact;

/// <summary>
/// Everything ImpactEngine needs, loaded once from &lt;index&gt;/*.jsonl|*.json. entrypoints.json/ticket-files.jsonl/
/// co-change.jsonl/diagnostics.json/meta.json are all optional — a solution that only ran `scan --syntax-only`,
/// or never ran `scan-git`, still gets a usable (just less complete) index.
/// </summary>
public sealed class ImpactIndex
{
    public required Dictionary<string, SymbolRecord> SymbolsById { get; init; }
    public required Dictionary<string, List<EdgeRecord>> ReverseEdges { get; init; }
    public required Dictionary<string, EntryPoint> EntryPointsById { get; init; }
    public required List<TicketFileRecord> Tickets { get; init; }
    public required List<CoChangeRecord> CoChanges { get; init; }
    public required Dictionary<string, FrontendCall> FrontendCallsById { get; init; }
    public required Dictionary<string, List<ApiLink>> ApiLinksByBackendId { get; init; }
    public DiagnosticsModel? Diagnostics { get; init; }
    public MetaModel? Meta { get; init; }

    /// <summary>Every implementation type di-confirmed.json records as REALLY DI-bound (fluent/attribute/manual-override — never the structural fallback di.json also carries), flattened across all interfaces and stripped of its docId prefix so it's directly comparable to SymbolRecord.ContainingType. See docs/BENCHMARK-INTERFACE-EXPANSION.md.</summary>
    public required HashSet<string> ConfirmedImplementationTypes { get; init; }

    /// <summary>Every via:"interface" edge's call site (From|File|Line) -> the set of containing types the interface-expand pass produced candidates for at that site — reconstructs "which implementations were siblings of this edge" the same way scripts/interface-expansion-audit.ps1 does.</summary>
    public required Dictionary<string, List<string>> InterfaceCallSiteCandidateTypes { get; init; }

    public static ImpactIndex Load(string indexDir)
    {
        var symbols = JsonlReader.Read<SymbolRecord>(Path.Combine(indexDir, "symbols.jsonl"));
        var edges = JsonlReader.Read<EdgeRecord>(Path.Combine(indexDir, "edges.jsonl"));

        var entryPoints = ReadJsonIfPresent<List<EntryPoint>>(indexDir, "entrypoints.json") ?? new();
        var diagnostics = ReadJsonIfPresent<DiagnosticsModel>(indexDir, "diagnostics.json");
        var meta = ReadJsonIfPresent<MetaModel>(indexDir, "meta.json");

        var ticketsPath = Path.Combine(indexDir, "ticket-files.jsonl");
        var tickets = File.Exists(ticketsPath) ? JsonlReader.Read<TicketFileRecord>(ticketsPath) : new List<TicketFileRecord>();
        var coChangePath = Path.Combine(indexDir, "co-change.jsonl");
        var coChanges = File.Exists(coChangePath) ? JsonlReader.Read<CoChangeRecord>(coChangePath) : new List<CoChangeRecord>();

        var frontendCallsPath = Path.Combine(indexDir, "frontend-calls.jsonl");
        var frontendCalls = File.Exists(frontendCallsPath) ? JsonlReader.Read<FrontendCall>(frontendCallsPath) : new List<FrontendCall>();
        var apiLinksPath = Path.Combine(indexDir, "api-links.jsonl");
        var apiLinks = File.Exists(apiLinksPath) ? JsonlReader.Read<ApiLink>(apiLinksPath) : new List<ApiLink>();

        var symbolsById = symbols.GroupBy(s => s.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var diConfirmed = ReadJsonIfPresent<Dictionary<string, List<string>>>(indexDir, "di-confirmed.json");
        var confirmedTypes = new HashSet<string>(StringComparer.Ordinal);
        if (diConfirmed != null)
            foreach (var impl in diConfirmed.Values.SelectMany(v => v))
                confirmedTypes.Add(StripDocIdPrefix(impl));

        var interfaceCallSites = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var e in edges.Where(e => e.Kind == "call" && e.Via == "interface"))
        {
            var containingType = symbolsById.GetValueOrDefault(e.To)?.ContainingType;
            if (containingType == null) continue;

            var key = $"{e.From}|{e.File}|{e.Line}";
            if (!interfaceCallSites.TryGetValue(key, out var list)) interfaceCallSites[key] = list = new();
            if (!list.Contains(containingType, StringComparer.Ordinal)) list.Add(containingType);
        }

        return new ImpactIndex
        {
            SymbolsById = symbolsById,
            ReverseEdges = edges.GroupBy(e => e.To, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal),
            EntryPointsById = entryPoints.GroupBy(e => e.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal),
            Tickets = tickets,
            CoChanges = coChanges,
            FrontendCallsById = frontendCalls.GroupBy(c => c.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal),
            ApiLinksByBackendId = apiLinks.GroupBy(l => l.BackendId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal),
            Diagnostics = diagnostics,
            Meta = meta,
            ConfirmedImplementationTypes = confirmedTypes,
            InterfaceCallSiteCandidateTypes = interfaceCallSites,
        };
    }

    private static string StripDocIdPrefix(string id) => id.Length > 2 && id[1] == ':' ? id[2..] : id;

    private static T? ReadJsonIfPresent<T>(string indexDir, string fileName)
    {
        var path = Path.Combine(indexDir, fileName);
        return File.Exists(path) ? JsonUtil.ReadFile<T>(path) : default;
    }
}
