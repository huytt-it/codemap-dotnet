using CodeMap.Query.ArgParsing;
using CodeMap.Query.Config;
using CodeMap.Query.Impact;
using CodeMap.Roslyn.Slice;

namespace CodeMap.Cli.Cli;

/// <summary>
/// `codemap slice` needs both CodeMap.Query (engine + rendering) and CodeMap.Roslyn (LiveCodeLocator, to read
/// the CURRENT code from disk instead of trusting anything cached in the index — spec section 7). Query itself
/// must never reference Roslyn (spec section 2), so this orchestration lives here in Cli, which is allowed to
/// reference both.
/// </summary>
internal static class SliceCommand
{
    public static int Run(string[] rawArgs)
    {
        var args = Args.Parse(rawArgs, options: IndexPathResolver.OptionNames.Concat(new[] { "symbol", "depth", "out" }).ToArray(), flags: Array.Empty<string>());
        var indexDir = IndexPathResolver.Resolve(args);
        var symbolId = args.Require("symbol");
        var depth = args.GetIntOrDefault("depth", 3);
        var outFile = args.GetOrDefault("out");

        var symbolsPath = Path.Combine(indexDir, "symbols.jsonl");
        if (!File.Exists(symbolsPath))
        {
            Console.Error.WriteLine($"{symbolsPath} not found. Run 'codemap scan' first.");
            return 1;
        }

        var index = ImpactIndex.Load(indexDir);
        var result = ImpactEngine.Traverse(index, symbolId, depth);
        var currentCode = LocateCurrentCode(index, symbolId);
        var markdown = EvidenceRenderer.Render(result, index, currentCode, index.Meta);

        if (outFile != null)
        {
            File.WriteAllText(outFile, markdown);
            Console.WriteLine($"Slice written to {outFile}");
        }
        else
        {
            Console.WriteLine(markdown);
        }

        return 0;
    }

    /// <summary>
    /// symbols.jsonl's File is relative to the scanned solution's own directory; meta.json's SolutionPath is
    /// relative to the git repo root. Combined, resolving against the current working directory matches the same
    /// "run this from inside the target repo" convention the staleness banner already relies on.
    /// </summary>
    private static LiveCode LocateCurrentCode(ImpactIndex index, string symbolId)
    {
        if (!index.SymbolsById.TryGetValue(symbolId, out var symbol))
            return new LiveCode(false, null, null, null);

        var solutionDir = index.Meta?.SolutionPath is { } solutionPath ? Path.GetDirectoryName(solutionPath) ?? "" : "";
        var absolutePath = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, solutionDir, symbol.File));

        var located = LiveCodeLocator.Locate(absolutePath, symbol);
        return located == null
            ? new LiveCode(false, symbol.File, null, null)
            : new LiveCode(true, symbol.File, located.Line, located.Snippet);
    }
}
