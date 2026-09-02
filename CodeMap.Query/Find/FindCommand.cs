using CodeMap.Query.ArgParsing;
using CodeMap.Query.Config;
using CodeMap.Query.Json;
using CodeMap.Query.Models;

namespace CodeMap.Query.Find;

/// <summary>`codemap find --index <dir> --query <text>` — approximate name match against symbols.jsonl; typing a docId by hand is infeasible (spec section 3).</summary>
internal static class FindCommand
{
    private const int MaxResults = 20;

    public static int Run(string[] rawArgs)
    {
        var args = Args.Parse(rawArgs);
        var indexDir = IndexPathResolver.Resolve(args);
        var query = args.Require("query");

        var symbolsPath = Path.Combine(indexDir, "symbols.jsonl");
        if (!File.Exists(symbolsPath))
        {
            Console.Error.WriteLine($"{symbolsPath} not found. Run 'codemap scan' first.");
            return 1;
        }

        var symbols = JsonlReader.Read<SymbolRecord>(symbolsPath);
        var matches = Search(symbols, query).Take(MaxResults).ToList();

        if (matches.Count == 0)
        {
            Console.WriteLine($"No symbol matched '{query}'.");
            return 0;
        }

        foreach (var (symbol, _) in matches)
        {
            var displayName = symbol.ContainingType != null ? $"{symbol.ContainingType}.{symbol.Name}" : symbol.Name;
            Console.WriteLine($"{symbol.Id}");
            Console.WriteLine($"    {displayName}  [{symbol.Kind}]  {symbol.Project}  {symbol.File}:{symbol.Line}");
        }

        return 0;
    }

    /// <summary>Exposed for tests. Exact display-name match ranks highest, then exact simple-name, then substring matches.</summary>
    internal static List<(SymbolRecord Symbol, int Score)> Search(List<SymbolRecord> symbols, string query)
    {
        var q = query.Trim();
        var results = new List<(SymbolRecord, int)>();

        foreach (var s in symbols)
        {
            var displayName = s.ContainingType != null ? $"{s.ContainingType}.{s.Name}" : s.Name;
            var score = ScoreMatch(displayName, s.Name, q);
            if (score > 0) results.Add((s, score));
        }

        return results.OrderByDescending(r => r.Item2).ThenBy(r => r.Item1.Id, StringComparer.Ordinal).ToList();
    }

    private static int ScoreMatch(string displayName, string name, string query)
    {
        if (string.Equals(displayName, query, StringComparison.OrdinalIgnoreCase)) return 100;
        if (string.Equals(name, query, StringComparison.OrdinalIgnoreCase)) return 90;
        if (displayName.Contains(query, StringComparison.OrdinalIgnoreCase)) return 70;
        if (name.Contains(query, StringComparison.OrdinalIgnoreCase)) return 60;
        return 0;
    }
}
