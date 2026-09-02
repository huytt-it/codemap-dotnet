using System.Globalization;
using CodeMap.Query.ArgParsing;
using CodeMap.Query.Json;
using CodeMap.Query.Models;

namespace CodeMap.Query.Config;

/// <summary>
/// `codemap projects` — what is registered, where each index lives, and whether it has actually been built yet.
/// Deliberately reports missing/unbuilt indexes as such instead of hiding them: "the index is not there" and
/// "the codebase has nothing in it" look identical from a query's output, and that confusion is exactly what
/// makes an agent state something false with confidence.
/// </summary>
internal static class ProjectsCommand
{
    public static int Run(string[] rawArgs)
    {
        var args = Args.Parse(rawArgs, options: new[] { "config" }, flags: Array.Empty<string>());

        var registry = ProjectRegistry.Discover(args.GetOrDefault("config"));
        if (registry == null)
        {
            Console.Error.WriteLine(
                $"No {ProjectRegistry.FileName} found in this directory, any parent, or ~/.codemap/.\n" +
                "It is optional — every command still works with explicit --solution/--out/--index paths.\n" +
                "See README.md for the file format if you want one.");
            return 1;
        }

        Console.WriteLine($"Registry: {registry.SourcePath}");
        if (!string.IsNullOrWhiteSpace(registry.Description))
            Console.WriteLine($"  {registry.Description}");

        if (registry.Projects.Count == 0)
        {
            Console.WriteLine("\nNo projects defined.");
            return 0;
        }

        foreach (var entry in registry.Projects)
        {
            var indexDir = registry.IndexDirOf(entry);

            Console.WriteLine();
            Console.WriteLine($"{entry.Name}");
            if (!string.IsNullOrWhiteSpace(entry.Description)) Console.WriteLine($"    {entry.Description}");
            Console.WriteLine($"    solution : {registry.ResolvePath(entry.Solution)}");
            Console.WriteLine($"    repo     : {registry.RepoOf(entry)}");
            if (entry.Frontend != null) Console.WriteLine($"    frontend : {registry.ResolvePath(entry.Frontend)}");
            if (entry.CommitLanguage != null) Console.WriteLine($"    commit language: {entry.CommitLanguage}");
            Console.WriteLine($"    index    : {indexDir}");
            Console.WriteLine($"    status   : {DescribeIndex(indexDir)}");
        }

        return 0;
    }

    private static string DescribeIndex(string indexDir)
    {
        if (!Directory.Exists(indexDir))
            return "NOT BUILT — run `codemap sync --project <name>`";

        var metaPath = Path.Combine(indexDir, "meta.json");
        if (!File.Exists(metaPath))
            return "directory exists but has no meta.json — incomplete or interrupted scan, re-run sync";

        MetaModel? meta;
        try
        {
            meta = JsonUtil.ReadFile<MetaModel>(metaPath);
        }
        catch (Exception ex)
        {
            return $"meta.json unreadable ({ex.Message}) — re-run sync";
        }

        if (meta == null) return "meta.json is empty — re-run sync";

        var age = DateTimeOffset.TryParse(
            meta.IndexedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var indexedAt)
            ? $", {(int)(DateTimeOffset.UtcNow - indexedAt).TotalDays}d old"
            : "";

        var degraded = meta.DegradedProjects.Count > 0 ? $", {meta.DegradedProjects.Count} degraded project(s)" : "";

        return $"{meta.SymbolCount} symbols, {meta.EdgeCount} edges, scanned {meta.IndexedAt}{age}{degraded}";
    }
}
