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
    public static string Render(ImpactResult result, ImpactIndex index, LiveCode currentCode, MetaModel? meta)
    {
        var sb = new StringBuilder();
        var relevantFiles = currentCode.File != null ? new List<string> { currentCode.File } : new List<string>();
        sb.Append(StalenessBanner.Render(meta, relevantFiles));
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
            sb.AppendLine(
                $"_Could not re-locate this symbol in {code.File ?? "(unknown file)"}. It may have been renamed or " +
                "deleted since the last scan — re-run `codemap scan` and `codemap find` to get a current docId._");
        }
        else
        {
            sb.AppendLine($"### {code.File}:{code.Line}");
            sb.AppendLine("```csharp");
            sb.AppendLine(code.Snippet);
            sb.AppendLine("```");
        }

        sb.AppendLine();
    }

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
        var epLabel = closest.HttpMethod != null ? $"{closest.HttpMethod} {closest.Route}" : $"[{closest.Type}]";
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
                sb.AppendLine($"- {s.HttpMethod} {s.Route} — {ScreenLocation(s)}{confidenceNote}");
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
            var other = c.FileA == result.File ? c.FileB : c.FileA;
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

/// <summary>Pre-fetched live-code lookup result, handed in by the CLI layer (which owns the Roslyn reference EvidenceRenderer can't have — spec section 2).</summary>
public sealed record LiveCode(bool Found, string? File, int? Line, string? Snippet);
