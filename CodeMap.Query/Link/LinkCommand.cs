using System.Text.RegularExpressions;
using CodeMap.Query.ArgParsing;
using CodeMap.Query.Config;
using CodeMap.Query.FrontendScan;
using CodeMap.Query.Json;
using CodeMap.Query.Models;

namespace CodeMap.Query.Link;

/// <summary>`codemap link` (spec section 6): matches frontend-calls.jsonl against entrypoints.json by (httpMethod, normalized route).</summary>
internal static partial class LinkCommand
{
    /// <summary>What scan writes as the HTTP method of an action with no verb attribute: ASP.NET lets every verb reach it.</summary>
    private const string AnyVerb = "ANY";

    public static int Run(string[] rawArgs)
    {
        var args = Args.Parse(rawArgs, options: IndexPathResolver.OptionNames, flags: Array.Empty<string>());
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
            .SelectMany(e => RouteKeys(e).Select(route => (Key: (e.HttpMethod!, route), Endpoint: e)))
            .GroupBy(x => x.Key)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Endpoint).Distinct().ToList());

        var backendBySuffixKey = BuildSuffixIndex(backendByKey);

        var links = new List<ApiLink>();
        var unmatched = new List<UnmatchedFrontendCall>();

        foreach (var call in frontendCalls)
        {
            var matches = ExactMatches(backendByKey, call.HttpMethod, call.Route);
            if (matches.Count > 0)
            {
                var matchKind = matches.Count == 1 ? "exact" : "ambiguous";
                foreach (var backend in matches)
                    links.Add(new ApiLink(call.Id, backend.Id, matchKind));
                continue;
            }

            var suffixMatches = new List<EntryPoint>();
            if (call.BaseUrlDropped)
            {
                suffixMatches = FindSuffixMatches(backendBySuffixKey, call, call.HttpMethod);
                if (suffixMatches.Count == 0) suffixMatches = FindSuffixMatches(backendBySuffixKey, call, AnyVerb);
            }
            if (suffixMatches.Count == 0)
            {
                unmatched.Add(new UnmatchedFrontendCall(call.Id, call.HttpMethod, call.Route));
                continue;
            }

            // "ambiguous", not "suffix", when several backend routes end the same way: that is the kind the impact
            // renderers already flag, and what they need to know is that the link is a guess among several.
            var suffixKind = suffixMatches.Count == 1 ? "suffix" : "ambiguous";
            foreach (var backend in suffixMatches)
                links.Add(new ApiLink(call.Id, backend.Id, suffixKind));
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

        Console.WriteLine($"api-links.jsonl: {links.Count} link(s) ({links.Count(l => l.MatchKind == "suffix")} suffix, {links.Count(l => l.MatchKind == "ambiguous")} ambiguous) -> {indexDir}");
        Console.WriteLine($"diagnostics.json: {unmatched.Count} unmatched frontend call(s), {unreferenced.Count} unreferenced endpoint(s)");

        return 0;
    }

    /// <summary>
    /// Every normalized route a call can reach this endpoint by. A trailing optional parameter (`{id?}`) may be left
    /// out of the URL, so the route without it counts too. A route scan inferred from the default MVC convention,
    /// {controller}/{action=Index}/{id?}, also answers to the controller path alone when its action is Index; that
    /// default belongs to the convention only, so an attribute route that ends in "index" means exactly that.
    /// Not modelled: the {controller=Home} default that sends "/" to Home/Index.
    /// </summary>
    private static IEnumerable<string> RouteKeys(EntryPoint endpoint)
    {
        var segments = endpoint.Route!.Trim('/').Split('/');
        var length = segments.Length;
        yield return Join(length);

        while (length > 1 && OptionalParameter().IsMatch(segments[length - 1]))
            yield return Join(--length);

        if (endpoint.RouteInferred == true && length > 1 && segments[length - 1].Equals("index", StringComparison.OrdinalIgnoreCase))
            yield return Join(length - 1);

        string Join(int count) => RouteNormalizer.NormalizeBackendRoute(string.Join('/', segments, 0, count));
    }

    /// <summary>
    /// Endpoints declared for the call's verb first, and only when there are none, the ones that accept any verb.
    /// That is the order ASP.NET selects in: of MVC's GET Edit(id) and [HttpPost] Edit(model) on one route, a POST
    /// goes to the constrained action.
    /// </summary>
    private static List<EntryPoint> ExactMatches(Dictionary<(string Method, string Route), List<EntryPoint>> backendByKey, string method, string route)
    {
        if (backendByKey.TryGetValue((method, route), out var declared) && declared.Count > 0) return declared;
        return backendByKey.TryGetValue((AnyVerb, route), out var any) ? any : new List<EntryPoint>();
    }

    [GeneratedRegex(@"^\{[^}]*\?\}$")]
    private static partial Regex OptionalParameter();

    /// <summary>Backend routes indexed by (httpMethod, last segment) — a suffix match needs the last segments to agree, so this narrows each lookup to the few routes that could possibly qualify.</summary>
    private static Dictionary<(string Method, string LastSegment), List<(string[] Segments, List<EntryPoint> Endpoints)>> BuildSuffixIndex(
        Dictionary<(string Method, string Route), List<EntryPoint>> backendByKey)
    {
        var index = new Dictionary<(string Method, string LastSegment), List<(string[] Segments, List<EntryPoint> Endpoints)>>();
        foreach (var ((method, route), endpoints) in backendByKey)
        {
            var segments = route.Split('/');
            var key = (method, segments[^1]);
            if (!index.TryGetValue(key, out var bucket))
                index[key] = bucket = new();
            bucket.Add((segments, endpoints));
        }

        return index;
    }

    /// <summary>
    /// A frontend call whose base URL the normalizer discarded (`${this.baseUrl}/orders` -> "orders") can only be
    /// compared on its tail, so a backend route matches when the frontend route is a proper whole-segment suffix of
    /// it ("orders" ends "api/orders"; it does not end "api/suborders"). The whole frontend route is always the
    /// suffix being compared, so every candidate matches with the same length and there is nothing to rank: more
    /// than one candidate stays more than one (the caller marks them ambiguous). A route made only of wildcards
    /// ("{*}") is never tried — it ends nearly every parameterized backend route and would link everything.
    /// </summary>
    private static List<EntryPoint> FindSuffixMatches(
        Dictionary<(string Method, string LastSegment), List<(string[] Segments, List<EntryPoint> Endpoints)>> index, FrontendCall call, string method)
    {
        var wanted = call.Route.Split('/');
        if (wanted.All(s => s == RouteNormalizer.Wildcard)) return new();
        if (!index.TryGetValue((method, wanted[^1]), out var candidates)) return new();

        // Distinct: one endpoint can sit in the index under several routes (with and without an optional parameter).
        return candidates
            .Where(c => c.Segments.Length > wanted.Length && c.Segments.AsSpan(c.Segments.Length - wanted.Length).SequenceEqual(wanted))
            .SelectMany(c => c.Endpoints)
            .Distinct()
            .ToList();
    }
}
