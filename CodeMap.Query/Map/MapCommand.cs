using CodeMap.Query.ArgParsing;
using CodeMap.Query.Json;
using CodeMap.Query.Models;

namespace CodeMap.Query.Map;

internal static class MapCommand
{
    public static int Run(string[] rawArgs)
    {
        var args = Args.Parse(rawArgs);
        var indexDir = Path.GetFullPath(args.Require("index"));
        var outDir = Path.GetFullPath(args.Require("out"));

        var symbolsPath = Path.Combine(indexDir, "symbols.jsonl");
        var edgesPath = Path.Combine(indexDir, "edges.jsonl");
        var diagnosticsPath = Path.Combine(indexDir, "diagnostics.json");
        var metaPath = Path.Combine(indexDir, "meta.json");
        var entryPointsPath = Path.Combine(indexDir, "entrypoints.json");
        var frontendCallsPath = Path.Combine(indexDir, "frontend-calls.jsonl");
        var apiLinksPath = Path.Combine(indexDir, "api-links.jsonl");

        if (!File.Exists(symbolsPath))
        {
            Console.Error.WriteLine($"{symbolsPath} not found. Run 'codemap scan' first.");
            return 1;
        }

        var symbols = JsonlReader.Read<SymbolRecord>(symbolsPath);
        var edges = JsonlReader.Read<EdgeRecord>(edgesPath);
        var diagnostics = File.Exists(diagnosticsPath) ? JsonUtil.ReadFile<DiagnosticsModel>(diagnosticsPath) : null;
        var meta = File.Exists(metaPath) ? JsonUtil.ReadFile<MetaModel>(metaPath) : null;
        var entryPoints = File.Exists(entryPointsPath) ? JsonUtil.ReadFile<List<EntryPoint>>(entryPointsPath) : null;
        var frontendCalls = File.Exists(frontendCallsPath) ? JsonlReader.Read<FrontendCall>(frontendCallsPath) : null;
        var apiLinks = File.Exists(apiLinksPath) ? JsonlReader.Read<ApiLink>(apiLinksPath) : null;

        var generator = new MapGenerator(symbols, edges, diagnostics, meta, entryPoints, frontendCalls, apiLinks);

        Directory.CreateDirectory(outDir);
        var modulesDir = Path.Combine(outDir, "modules");
        Directory.CreateDirectory(modulesDir);

        var mapPath = Path.Combine(outDir, "MAP.md");
        var existingMap = File.Exists(mapPath) ? File.ReadAllText(mapPath) : null;
        var mapContent = generator.BuildMapMarkdown(HumanNotes.Extract(existingMap));
        File.WriteAllText(mapPath, mapContent);

        foreach (var project in generator.Projects)
        {
            var modulePath = Path.Combine(modulesDir, $"{SanitizeFileName(project)}.md");
            var existing = File.Exists(modulePath) ? File.ReadAllText(modulePath) : null;
            var content = generator.BuildModuleMarkdown(project, HumanNotes.Extract(existing));
            File.WriteAllText(modulePath, content);
        }

        var lineCount = mapContent.Split('\n').Length;
        Console.WriteLine($"MAP.md: {lineCount} lines -> {mapPath}");
        Console.WriteLine($"modules/: {generator.Projects.Count} file(s) -> {modulesDir}");
        if (lineCount > 500)
            Console.Error.WriteLine("WARNING: MAP.md exceeds 500 lines (hard requirement).");

        return 0;
    }

    private static string SanitizeFileName(string name)
        => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
