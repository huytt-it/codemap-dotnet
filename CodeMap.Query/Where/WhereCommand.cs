using CodeMap.Query.ArgParsing;
using CodeMap.Query.Config;
using CodeMap.Query.Impact;

namespace CodeMap.Query.Where;

/// <summary>`codemap where --index <dir> --query "<mô tả nghiệp vụ>"` (spec section 3).</summary>
internal static class WhereCommand
{
    public static int Run(string[] rawArgs)
    {
        var args = Args.Parse(rawArgs, options: IndexPathResolver.OptionNames.Concat(new[] { "query" }).ToArray(), flags: Array.Empty<string>());
        var indexDir = IndexPathResolver.Resolve(args);
        var query = args.Require("query");

        var symbolsPath = Path.Combine(indexDir, "symbols.jsonl");
        if (!File.Exists(symbolsPath))
        {
            Console.Error.WriteLine($"{symbolsPath} not found. Run 'codemap scan' first.");
            return 1;
        }

        var index = ImpactIndex.Load(indexDir);
        var candidates = WhereEngine.Search(index, query);

        if (candidates.Count == 0)
        {
            Console.WriteLine($"No candidate matched '{query}'.");
            Console.WriteLine("This means no ticket message, route/FE feature, or symbol name shares a term with the query — not that nothing in the code relates to it. Try `codemap find` with an English term instead, or check ticket-files.jsonl exists (run `codemap scan-git`).");
            return 0;
        }

        foreach (var c in candidates)
        {
            Console.WriteLine($"{c.SymbolId}  (score {c.Score})");
            Console.WriteLine($"    {c.DisplayName}");
            foreach (var reason in c.Reasons)
                Console.WriteLine($"    - {reason}");
        }

        return 0;
    }
}
