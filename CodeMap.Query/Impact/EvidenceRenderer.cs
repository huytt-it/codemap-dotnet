using System.Text;
using CodeMap.Query.Models;
using CodeMap.Query.Reporting;

namespace CodeMap.Query.Impact;

/// <summary>
/// Renders `slice`'s output (spec section 7 template). Takes the live code snippet as already-fetched input —
/// this renderer never touches disk or Roslyn itself; the CLI layer (which can reference CodeMap.Roslyn's
/// LiveCodeLocator) fetches it and hands it in. Never calls the engine or reads JSONL itself.
/// </summary>
public static class EvidenceRenderer
{
    /// <param name="repoRoot">Repo the staleness banner runs git in; null falls back to the process's working directory (bare `--index`).</param>
    public static string Render(ImpactResult result, ImpactIndex index, LiveCode currentCode, MetaModel? meta, string? repoRoot = null)
    {
        var sb = new StringBuilder();
        var relevantFiles = currentCode.File != null ? new List<string> { currentCode.File } : new List<string>();
        sb.Append(StalenessBanner.Render(meta, relevantFiles, repoRoot));
        sb.AppendLine();

        sb.AppendLine($"# Slice: {result.DisplayName}");
        sb.AppendLine();

        AppendCurrentCode(sb, currentCode);
        AppendPath(sb, result);
        AppendScreens(sb, result);
        AppendTickets(sb, result);
        AppendCoChange(sb, result);
        AppendBlindSpots(sb, result);

        return sb.ToString();
    }

    private static void AppendCurrentCode(StringBuilder sb, LiveCode code)
    {
        sb.AppendLine("## Current code");
        sb.AppendLine();
        if (!code.Found)
        {
            sb.AppendLine(MissMessage(code));
        }
        else
        {
            sb.AppendLine($"### {code.File}:{code.Line}");
            if (code.IsFuzzyMatch) sb.AppendLine(FuzzyNote(code));
            sb.AppendLine("```csharp");
            sb.AppendLine(code.Snippet);
            sb.AppendLine("```");
        }

        sb.AppendLine();
    }

    /// <summary>
    /// One cause per message. These used to be a single "renamed or deleted" — true for only one of the four, and
    /// the likeliest real cause of the other (a path resolved from the wrong place) pointed the reader at a rename
    /// that never happened.
    /// </summary>
    private static string MissMessage(LiveCode code) => code.Miss switch
    {
        LiveCodeMiss.SymbolNotInIndex =>
            "_This symbol is not in the index, so there is no file to read. Run `codemap find` to look up its current docId, " +
            "or `codemap sync` if the index is out of date._",

        LiveCodeMiss.FileMissing =>
            $"_The source file does not exist: `{code.TriedPath}` (the index records it as `{code.File}`). The path was most likely " +
            "resolved from the wrong place — pass `--project <name>` so it is resolved against the project's repo, and with `--index` " +
            "run the command from the repo root. If the file was moved or deleted since the scan, run `codemap sync` to refresh the index._",

        LiveCodeMiss.OutsideRepo =>
            $"_The index records this symbol in `{code.File}`, which resolves to `{code.TriedPath}` — outside the repository, so it was " +
            "not read. The index may be corrupt or built for another checkout; run `codemap sync` to rebuild it._",

        // SymbolNotLocated, and a LiveCode built without a reason.
        _ =>
            $"_`{code.File ?? "(unknown file)"}` exists, but the symbol could not be found in it. It may have been renamed or removed " +
            "since the last scan — run `codemap find` to look up its current docId, or `codemap sync` to refresh the index._",
    };

    /// <summary>
    /// The live file is parsed on its own, so a parameter type declared in another file cannot be resolved and the
    /// exact docId the scan recorded cannot be reproduced; the declaration is then found by name. Said out loud,
    /// because with overloads that is a guess.
    /// </summary>
    private static string FuzzyNote(LiveCode code) => code.FuzzyCandidates > 1
        ? $"_Matched by name and containing type only: {code.FuzzyCandidates} declarations here share that name (overloads) and the exact " +
          "signature could not be reproduced. Showing the one closest to the line the scan recorded — it may be the wrong overload._"
        : "_Matched by name and containing type only: the exact signature could not be reproduced (this file is parsed on its own, so a " +
          "parameter type declared in another file cannot be resolved). Check this is the member you meant._";

    private static void AppendPath(StringBuilder sb, ImpactResult result)
    {
        sb.AppendLine("## Path from entry point");
        sb.AppendLine();

        var closest = result.EntryPoints.OrderBy(e => e.Depth).FirstOrDefault();
        if (closest == null)
        {
            AppendUnclassifiedPath(sb, result);
            return;
        }

        var path = result.GetPathToRoot(closest.Id); // closest ... root
        var labels = new List<string>();
        var epLabel = closest.HttpMethod == null
            ? $"[{closest.Type}]"
            : $"{closest.HttpMethod} {closest.Route ?? "(route not resolved)"}{(closest.RouteInferred ? " (route inferred from convention)" : "")}";
        labels.Add(epLabel);

        for (var i = 0; i < path.Count; i++)
        {
            var id = path[i];
            var line = i == path.Count - 1 ? result.Line : null; // only the root's own Line is on ImpactResult directly
            labels.Add(line != null ? $"{DisplayNameFor(id, result)}:{line}" : DisplayNameFor(id, result));
        }

        sb.AppendLine(string.Join(" → ", labels));
        sb.AppendLine();
    }

    /// <summary>Same benchmark finding as CompactRenderer.AppendEntryPoints: "no known entry point" must not read as "unreachable" when real (just unclassified) callers exist.</summary>
    private static void AppendUnclassifiedPath(StringBuilder sb, ImpactResult result)
    {
        var nearest = result.IntermediateCallers.OrderBy(c => c.Depth).FirstOrDefault();
        if (nearest == null)
        {
            sb.AppendLine("_No entry point reached within the scanned depth — increase --depth, or this really is unreachable from a known entry point._");
            sb.AppendLine();
            return;
        }

        sb.AppendLine(
            $"⚠ No entry point of a known type reached, but {result.IntermediateCallers.Count} caller(s) exist " +
            "(this may be a Razor Page, Minimal API endpoint, or another entry point kind not yet recognized). " +
            "This is NOT the same as \"unreachable\". Nearest known caller:");
        sb.AppendLine();

        var path = result.GetPathToRoot(nearest.Id); // nearest ... root
        var labels = new List<string> { "[unclassified caller]" };
        for (var i = 0; i < path.Count; i++)
        {
            var id = path[i];
            var line = i == path.Count - 1 ? result.Line : null;
            labels.Add(line != null ? $"{DisplayNameFor(id, result)}:{line}" : DisplayNameFor(id, result));
        }

        sb.AppendLine(string.Join(" → ", labels));
        sb.AppendLine();
    }

    private static string DisplayNameFor(string id, ImpactResult result)
    {
        if (id == result.SymbolId) return result.DisplayName;
        var ep = result.EntryPoints.FirstOrDefault(e => e.Id == id);
        if (ep != null) return ep.DisplayName;
        var caller = result.IntermediateCallers.FirstOrDefault(c => c.Id == id);
        return caller?.DisplayName ?? id;
    }

    private static void AppendScreens(StringBuilder sb, ImpactResult result)
    {
        if (result.Screens.Count == 0) return;

        var featureCount = result.Screens.Select(s => s.Feature).Distinct(StringComparer.Ordinal).Count();
        sb.AppendLine($"## FE screens that call this ({featureCount})");
        sb.AppendLine();
        foreach (var group in result.Screens.GroupBy(s => s.Feature, StringComparer.Ordinal))
        {
            sb.AppendLine($"### {group.Key}");
            foreach (var s in group)
            {
                var confidenceNote = s.Confidence == "low" ? " (low confidence)" : "";
                var matchNote = s.MatchKind switch
                {
                    "ambiguous" => " (ambiguous match)",
                    "suffix" => " (matched by route suffix only)",
                    _ => "",
                };
                sb.AppendLine($"- {s.HttpMethod} {s.Route} — {ScreenLocation(s)}{confidenceNote}{matchNote}");
            }
        }

        sb.AppendLine();
    }

    /// <summary>Task "nối FE thiếu 1 hop": a service's file isn't what a user sees — the component(s) that inject it are. See CompactRenderer's copy of this same helper (kept independent — each renderer only reads ImpactResult, no shared rendering-layer dependency between the two).</summary>
    private static string ScreenLocation(ReachedScreen s) =>
        s.InjectedByComponents.Count > 0
            ? $"{string.Join(", ", s.InjectedByComponents)}  (service: {s.FrontendFile}:{s.FrontendLine})"
            : $"{s.FrontendFile}:{s.FrontendLine}";

    private static void AppendTickets(StringBuilder sb, ImpactResult result)
    {
        if (result.RelatedTickets.Count == 0) return;

        sb.AppendLine($"## Past tickets touching this file ({result.RelatedTickets.Count})");
        sb.AppendLine();
        foreach (var t in result.RelatedTickets.OrderByDescending(t => t.Date))
            sb.AppendLine($"- #{t.Ticket} ({t.Date}) {t.Message}");
        sb.AppendLine();
    }

    private static void AppendCoChange(StringBuilder sb, ImpactResult result)
    {
        if (result.CoChangingFiles.Count == 0) return;

        sb.AppendLine($"## Files that often change together ({result.CoChangingFiles.Count})");
        sb.AppendLine();
        foreach (var c in result.CoChangingFiles.OrderByDescending(c => c.Strength))
        {
            var other = FilePathComparison.Comparer.Equals(c.FileA, result.File) ? c.FileB : c.FileA;
            sb.AppendLine($"- {other} — {c.Together}/{Math.Min(c.TotalA, c.TotalB)} times (strength {c.Strength:0.00}) ⚠ Roslyn can't see this relationship");
        }

        sb.AppendLine();
    }

    private static void AppendBlindSpots(StringBuilder sb, ImpactResult result)
    {
        sb.AppendLine("## Blind spots");
        sb.AppendLine();
        if (result.BlindSpots.Count == 0)
            sb.AppendLine("_None._");
        else
            foreach (var b in result.BlindSpots) sb.AppendLine($"- {b}");
    }
}

/// <summary>Why `slice` has no current code to show. The causes need different fixes, so the output names which one it was.</summary>
public enum LiveCodeMiss
{
    None,
    /// <summary>The docId is not in symbols.jsonl: nothing says which file to open.</summary>
    SymbolNotInIndex,
    /// <summary>The file the index names is not on disk at the resolved path — usually the path is wrong, not the file gone.</summary>
    FileMissing,
    /// <summary>The index names a file that resolves outside the repo root; it is not read.</summary>
    OutsideRepo,
    /// <summary>The file is there but the symbol is not in it any more (renamed or removed).</summary>
    SymbolNotLocated,
}

/// <summary>
/// Pre-fetched live-code lookup result, handed in by the CLI layer (which owns the Roslyn reference EvidenceRenderer can't have — spec section 2).
/// <paramref name="Miss"/> and <paramref name="TriedPath"/> say why and where when <paramref name="Found"/> is false;
/// <c>TriedPath</c> is the absolute path that was (or, for OutsideRepo, would have been) read.
/// <paramref name="IsFuzzyMatch"/>: the declaration was found by name and containing type, not by exact docId;
/// <paramref name="FuzzyCandidates"/> is how many declarations matched that way (more than one: overloads).
/// </summary>
public sealed record LiveCode(
    bool Found, string? File, int? Line, string? Snippet, LiveCodeMiss Miss = LiveCodeMiss.None, string? TriedPath = null,
    bool IsFuzzyMatch = false, int FuzzyCandidates = 0);
