using CodeMap.Query.ArgParsing;

namespace CodeMap.Query.Impact;

/// <summary>`codemap impact --index <dir> --symbol <docId> [--depth 5] [--full] [--out <file.md>]` — no Roslyn needed, works on any machine that can read the index (spec section 2).</summary>
internal static class ImpactCommand
{
    public static int Run(string[] rawArgs)
    {
        var args = Args.Parse(rawArgs);
        var indexDir = Path.GetFullPath(args.Require("index"));
        var symbolId = args.Require("symbol");
        var depth = args.GetIntOrDefault("depth", 5);
        var full = args.HasFlag("full");
        var outFile = args.GetOrDefault("out");

        if (!File.Exists(Path.Combine(indexDir, "symbols.jsonl")))
        {
            Console.Error.WriteLine($"{Path.Combine(indexDir, "symbols.jsonl")} not found. Run 'codemap scan' first.");
            return 1;
        }

        var index = ImpactIndex.Load(indexDir);
        var result = ImpactEngine.Traverse(index, symbolId, depth);
        var markdown = CompactRenderer.Render(result, full, index.Meta);

        if (outFile != null)
        {
            File.WriteAllText(outFile, markdown);
            Console.WriteLine($"Impact report written to {outFile}");
        }
        else
        {
            Console.WriteLine(markdown);
        }

        return 0;
    }
}
