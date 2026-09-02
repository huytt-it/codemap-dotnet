using CodeMap.Query.ArgParsing;
using CodeMap.Query.Config;
using CodeMap.Query.FrontendScan;
using CodeMap.Query.Json;
using CodeMap.Query.Models;

namespace CodeMap.Query.Link;

/// <summary>`codemap link` (spec section 6): matches frontend-calls.jsonl against entrypoints.json by (httpMethod, normalized route).</summary>
internal static class LinkCommand
{
    public static int Run(string[] rawArgs)
    {
        var args = Args.Parse(rawArgs);
        var indexDir = IndexPathResolver.Resolve(args);

        var entryPointsPath = Path.Combine(indexDir, "entrypoints.json");
        var frontendCallsPath = Path.Combine(indexDir, "frontend-calls.jsonl");
        if (!File.Exists(entryPointsPath))
        {
            Console.Error.WriteLine($"{entryPointsPath} not found. Run 'codemap scan' first.");
            return 1;
        }

        if (!File.Exists(frontendCallsPath))
        {
            Console.Error.WriteLine($"{frontendCallsPath} not found. Run 'codemap scan-fe' first.");
            return 1;
        }

        var entryPoints = JsonUtil.ReadFile<List<EntryPoint>>(entryPointsPath) ?? new();
        var httpEntryPoints = entryPoints.Where(e => e.Type == "http" && e.HttpMethod != null && e.Route != null).ToList();
        var frontendCalls = JsonlReader.Read<FrontendCall>(frontendCallsPath);

        var backendByKey = httpEntryPoints
            .GroupBy(e => (e.HttpMethod!, RouteNormalizer.NormalizeBackendRoute(e.Route!)))
            .ToDictionary(g => g.Key, g => g.ToList());

        var links = new List<ApiLink>();
        var unmatched = new List<UnmatchedFrontendCall>();

        foreach (var call in frontendCalls)
        {
            var key = (call.HttpMethod, call.Route);
            if (!backendByKey.TryGetValue(key, out var matches) || matches.Count == 0)
            {
                unmatched.Add(new UnmatchedFrontendCall(call.Id, call.HttpMethod, call.Route));
                continue;
            }

            var matchKind = matches.Count == 1 ? "exact" : "ambiguous";
            foreach (var backend in matches)
                links.Add(new ApiLink(call.Id, backend.Id, matchKind));
        }

        var linkedBackendIds = links.Select(l => l.BackendId).ToHashSet(StringComparer.Ordinal);
        var unreferenced = httpEntryPoints.Where(e => !linkedBackendIds.Contains(e.Id)).Select(e => e.Id).OrderBy(id => id, StringComparer.Ordinal).ToList();

        JsonlWriter.Write(Path.Combine(indexDir, "api-links.jsonl"), links);

        var diagnosticsPath = Path.Combine(indexDir, "diagnostics.json");
        var diagnostics = File.Exists(diagnosticsPath) ? JsonUtil.ReadFile<DiagnosticsModel>(diagnosticsPath) ?? new DiagnosticsModel() : new DiagnosticsModel();
        diagnostics.UnmatchedFrontendCalls.Clear(); // this command owns these two lists end-to-end; re-linking replaces rather than accumulates
        diagnostics.UnmatchedFrontendCalls.AddRange(unmatched.OrderBy(u => u.FrontendId, StringComparer.Ordinal));
        diagnostics.UnreferencedEndpoints.Clear();
        diagnostics.UnreferencedEndpoints.AddRange(unreferenced);
        JsonUtil.WriteIndented(diagnosticsPath, diagnostics);

        Console.WriteLine($"api-links.jsonl: {links.Count} link(s) ({links.Count(l => l.MatchKind == "ambiguous")} ambiguous) -> {indexDir}");
        Console.WriteLine($"diagnostics.json: {unmatched.Count} unmatched frontend call(s), {unreferenced.Count} unreferenced endpoint(s)");

        return 0;
    }
}
