using CodeMap.Query.ArgParsing;
using CodeMap.Query.Config;
using CodeMap.Query.Json;
using CodeMap.Query.Models;

namespace CodeMap.Query.FrontendScan;

/// <summary>`codemap scan-fe` (spec section 6). Doesn't use Roslyn — Angular/TypeScript scanning shells out to `node` (TypeScriptCallScanner), jQuery scanning is pure regex (JQueryCallScanner) — so, like the rest of Query, this never needs Build Tools installed.</summary>
internal static class ScanFeCommand
{
    public static int Run(string[] rawArgs)
    {
        var args = Args.Parse(rawArgs, options: new[] { "root", "out" }, flags: Array.Empty<string>());
        var root = Path.GetFullPath(args.Require("root"));
        var outDir = Path.GetFullPath(args.Require("out"));

        if (!Directory.Exists(root))
        {
            Console.Error.WriteLine($"{root} not found.");
            return 1;
        }

        var config = CodeMapConfig.Load(root);
        var indexDir = Path.Combine(outDir, "index");
        Directory.CreateDirectory(indexDir);

        var frontendCalls = new List<FrontendCall>();
        var unparsedUrls = new List<UnparsedFrontendUrl>();
        var unresolvedInjections = new List<UnresolvedFrontendInjection>();

        var tsOutcome = TypeScriptCallScanner.Scan(root);
        if (tsOutcome.SkippedReason != null)
            Console.Error.WriteLine($"scan-fe: {tsOutcome.SkippedReason}");
        AddNormalizedCalls(
            tsOutcome.Calls.Select(c => (c.File, c.Line, c.HttpMethod, c.RawUrl, (List<string>?)c.InjectedBy, c.IsComponentItself)),
            "high", config, calls: frontendCalls, unparsed: unparsedUrls, unresolvedInjections: unresolvedInjections);

        var jqResult = JQueryCallScanner.Scan(root);
        AddNormalizedCalls(
            jqResult.Calls.Select(c => (c.File, c.Line, c.HttpMethod, c.RawUrl, InjectedBy: (List<string>?)null, IsComponentItself: false)),
            "low", config, calls: frontendCalls, unparsed: unparsedUrls, unresolvedInjections: null); // jQuery has no DI concept — never worth an "unresolved injection" note
        foreach (var u in jqResult.Unparsed)
            unparsedUrls.Add(new UnparsedFrontendUrl(u.File, u.Line, u.HttpMethod, "", "jQuery call site found but no resolvable URL argument/field"));

        frontendCalls = frontendCalls.OrderBy(c => c.File, StringComparer.Ordinal).ThenBy(c => c.Line).ToList();
        JsonlWriter.Write(Path.Combine(indexDir, "frontend-calls.jsonl"), frontendCalls);

        var diagnosticsPath = Path.Combine(indexDir, "diagnostics.json");
        var diagnostics = File.Exists(diagnosticsPath) ? JsonUtil.ReadFile<DiagnosticsModel>(diagnosticsPath) ?? new DiagnosticsModel() : new DiagnosticsModel();
        // this command owns these lists end-to-end; re-scanning replaces them rather than accumulating stale entries
        diagnostics.UnparsedFrontendUrls.Clear();
        diagnostics.UnparsedFrontendUrls.AddRange(unparsedUrls);
        diagnostics.UnresolvedFrontendInjections.Clear();
        diagnostics.UnresolvedFrontendInjections.AddRange(unresolvedInjections);
        JsonUtil.WriteIndented(diagnosticsPath, diagnostics);

        Console.WriteLine($"frontend-calls.jsonl: {frontendCalls.Count} call(s) -> {indexDir}");
        if (unparsedUrls.Count > 0)
            Console.WriteLine($"diagnostics.json: {unparsedUrls.Count} unparsed URL(s)");
        if (unresolvedInjections.Count > 0)
            Console.WriteLine($"diagnostics.json: {unresolvedInjections.Count} service call(s) with no directly-injecting component found");

        return 0;
    }

    private static void AddNormalizedCalls(
        IEnumerable<(string File, int Line, string HttpMethod, string RawUrl, List<string>? InjectedBy, bool IsComponentItself)> raw, string confidence, CodeMapConfig config,
        List<FrontendCall> calls, List<UnparsedFrontendUrl> unparsed, List<UnresolvedFrontendInjection>? unresolvedInjections)
    {
        foreach (var c in raw)
        {
            var route = FrontendUrlNormalizer.Normalize(c.RawUrl);
            if (route == null)
            {
                unparsed.Add(new UnparsedFrontendUrl(c.File, c.Line, c.HttpMethod, c.RawUrl, "URL expression has no recognizable path structure (likely a dynamic/computed value)"));
                continue;
            }

            var feature = FeatureExtractor.Extract(c.File, config.EffectiveFrontendAppDir);
            var id = $"fe:{c.File}:{c.Line}";
            var injectedBy = c.InjectedBy ?? new List<string>();
            calls.Add(new FrontendCall(id, c.File, c.Line, c.HttpMethod, c.RawUrl, route, feature, confidence, injectedBy));

            // Spec (Review Fix Pass v1, "nối FE thiếu 1 hop"): only 1 level of DI resolution is attempted (see
            // ts-call-scan.js) — a call with nobody found injecting its containing service directly is reported,
            // not silently guessed at (could be injected into another service, a module-level provider, etc.).
            // A call already inside an @Component class needs no resolution at all — that component IS the
            // screen — so it's never worth flagging even though its InjectedBy is also empty.
            if (unresolvedInjections != null && injectedBy.Count == 0 && !c.IsComponentItself)
                unresolvedInjections.Add(new UnresolvedFrontendInjection(id, c.File, c.Line,
                    "No component's constructor directly injects the service this call lives in — only one level of DI resolution is attempted (could be injected into another service, a module-level provider, or the call isn't inside a class at all)."));
        }
    }
}
