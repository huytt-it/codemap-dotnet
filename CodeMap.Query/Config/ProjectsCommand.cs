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
            if (Directory.Exists(indexDir)) Console.WriteLine($"    sync     : {DescribeSync(indexDir)}");
        }

        return 0;
    }

    /// <summary>
    /// Whether the index came out of a finished `codemap sync`. `scan` run by hand writes no sync-complete.json, and
    /// writes meta.json first and the rest later — so an index without the marker may be fine, may be half-built,
    /// and looks identical either way until something asks for the file that never got written.
    /// </summary>
    private static string DescribeSync(string indexDir)
    {
        var marker = SyncMarker.TryRead(indexDir, out var problem);
        if (problem != null) return $"{problem} — re-run sync";
        if (marker == null)
            return $"no {SyncMarker.FileName} — not produced by `codemap sync`, or the sync never finished; run `codemap sync --project <name>` for a verified index";

        var notable = marker.Steps
            .Where(s => s.Status is SyncStepResult.Partial or SyncStepResult.Failed)
            .Select(s => $"{s.Name} {s.Status}")
            .ToList();
        var steps = notable.Count > 0 ? $" ({string.Join(", ", notable)})" : "";

        var metaIndexedAt = ReadMetaIndexedAt(indexDir);
        if (marker.MetaIndexedAt != null && metaIndexedAt != null
            && !string.Equals(marker.MetaIndexedAt, metaIndexedAt, StringComparison.Ordinal))
            return $"index changed since the last sync (meta.json scanned {metaIndexedAt}, sync saw {marker.MetaIndexedAt}) — " +
                   "`scan` run by hand? re-run sync";

        return $"complete, {marker.CompletedAt}{steps}";
    }

    internal static string? ReadMetaIndexedAt(string indexDir)
    {
        try
        {
            var metaPath = Path.Combine(indexDir, "meta.json");
            return File.Exists(metaPath) ? JsonUtil.ReadFile<MetaModel>(metaPath)?.IndexedAt : null;
        }
        catch
        {
            return null;
        }
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
