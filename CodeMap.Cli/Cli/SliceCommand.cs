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
        var location = IndexPathResolver.ResolveLocation(args);
        var indexDir = location.IndexDir;
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
        var currentCode = LocateCurrentCode(index, symbolId, location.RepoRoot ?? Environment.CurrentDirectory);
        var markdown = EvidenceRenderer.Render(result, index, currentCode, index.Meta, location.RepoRoot);

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
    /// relative to the git repo root. Joined onto <paramref name="repoRoot"/> they give the file on disk. For a
    /// registered project the root comes from the registry; for a bare `--index` it is the working directory,
    /// the same "run this from inside the target repo" convention as before.
    ///
    /// A path that resolves outside <paramref name="repoRoot"/> is never read: the index is a file anyone can
    /// have edited or swapped, and the MCP server this feeds hands the result to a model.
    /// </summary>
    internal static LiveCode LocateCurrentCode(ImpactIndex index, string symbolId, string repoRoot)
    {
        if (!index.SymbolsById.TryGetValue(symbolId, out var symbol))
            return new LiveCode(false, null, null, null);

        var solutionDir = index.Meta?.SolutionPath is { } solutionPath ? Path.GetDirectoryName(solutionPath) ?? "" : "";
        var absolutePath = Path.GetFullPath(Path.Combine(repoRoot, solutionDir, symbol.File));
        if (!FilePathComparison.IsInside(repoRoot, absolutePath))
            return new LiveCode(false, symbol.File, null, null);

        var located = LiveCodeLocator.Locate(absolutePath, symbol);
        return located == null
            ? new LiveCode(false, symbol.File, null, null)
            : new LiveCode(true, symbol.File, located.Line, located.Snippet);
    }
}
