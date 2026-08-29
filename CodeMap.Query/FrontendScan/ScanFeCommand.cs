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
        var args = Args.Parse(rawArgs);
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

        var tsOutcome = TypeScriptCallScanner.Scan(root);
        if (tsOutcome.SkippedReason != null)
            Console.Error.WriteLine($"scan-fe: {tsOutcome.SkippedReason}");
        AddNormalizedCalls(tsOutcome.Calls.Select(c => (c.File, c.Line, c.HttpMethod, c.RawUrl)), "high", config, frontendCalls, unparsedUrls);

        var jqResult = JQueryCallScanner.Scan(root);
        AddNormalizedCalls(jqResult.Calls.Select(c => (c.File, c.Line, c.HttpMethod, c.RawUrl)), "low", config, frontendCalls, unparsedUrls);
        foreach (var u in jqResult.Unparsed)
            unparsedUrls.Add(new UnparsedFrontendUrl(u.File, u.Line, u.HttpMethod, "", "jQuery call site found but no resolvable URL argument/field"));

        frontendCalls = frontendCalls.OrderBy(c => c.File, StringComparer.Ordinal).ThenBy(c => c.Line).ToList();
        JsonlWriter.Write(Path.Combine(indexDir, "frontend-calls.jsonl"), frontendCalls);

        var diagnosticsPath = Path.Combine(indexDir, "diagnostics.json");
        var diagnostics = File.Exists(diagnosticsPath) ? JsonUtil.ReadFile<DiagnosticsModel>(diagnosticsPath) ?? new DiagnosticsModel() : new DiagnosticsModel();
        diagnostics.UnparsedFrontendUrls.Clear(); // this command owns this list end-to-end; re-scanning replaces it rather than accumulating stale entries
        diagnostics.UnparsedFrontendUrls.AddRange(unparsedUrls);
        JsonUtil.WriteIndented(diagnosticsPath, diagnostics);

        Console.WriteLine($"frontend-calls.jsonl: {frontendCalls.Count} call(s) -> {indexDir}");
        if (unparsedUrls.Count > 0)
            Console.WriteLine($"diagnostics.json: {unparsedUrls.Count} unparsed URL(s)");

        return 0;
    }

    private static void AddNormalizedCalls(
        IEnumerable<(string File, int Line, string HttpMethod, string RawUrl)> raw, string confidence, CodeMapConfig config,
        List<FrontendCall> calls, List<UnparsedFrontendUrl> unparsed)
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
            calls.Add(new FrontendCall($"fe:{c.File}:{c.Line}", c.File, c.Line, c.HttpMethod, c.RawUrl, route, feature, confidence));
        }
    }
}
