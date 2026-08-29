using System.Text;
using CodeMap.Query.Models;
using CodeMap.Query.Reporting;

namespace CodeMap.Query.Impact;

/// <summary>
/// Renders `impact`'s output from an ImpactResult (spec section 7 template). Never calls the engine or reads
/// JSONL itself — pure formatting, per "một engine, nhiều renderer".
/// </summary>
public static class CompactRenderer
{
    public static string Render(ImpactResult result, bool full, MetaModel? meta)
    {
        var sb = new StringBuilder();
        sb.Append(StalenessBanner.Render(meta, CollectRelevantFiles(result)));
        sb.AppendLine();

        sb.AppendLine($"# Impact: {result.DisplayName}");
        if (result.File != null) sb.AppendLine($"File: {result.File}:{result.Line}");
        sb.AppendLine($"Direct fan-in: {result.DirectFanIn} · Depth scanned: {result.DepthScanned}");
        sb.AppendLine();

        if (result.IsHub && !full)
        {
            AppendHubWarning(sb, result);
        }
        else
        {
            AppendEntryPoints(sb, result);
        }

        AppendScreens(sb, result);
        AppendTests(sb, result);
        AppendRiskScore(sb, result);
        if (full) AppendIntermediateCallers(sb, result);
        AppendTickets(sb, result);
        AppendCoChange(sb, result);
        AppendBlindSpots(sb, result);

        return sb.ToString();
    }

    private static void AppendHubWarning(StringBuilder sb, ImpactResult result)
    {
        var screenCount = result.Screens.Select(s => s.Feature).Distinct(StringComparer.Ordinal).Count();
        sb.AppendLine("## ⚠ This symbol is a hub");
        sb.AppendLine(
            $"Reaches {result.EntryPoints.Count} entry point(s) across {result.ModuleFanIn.Count} module(s)" +
            (screenCount > 0 ? $" and {screenCount} FE screen(s)." : "."));
        sb.AppendLine("Blast radius is system-wide. Don't change the signature, don't change default behavior.");
        sb.AppendLine("Re-run with --full if you still need the full list.");
        sb.AppendLine();
        sb.AppendLine("Affected modules: " + string.Join(" · ",
            result.ModuleFanIn.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} ({kv.Value})")));
        sb.AppendLine();
    }

    private static void AppendEntryPoints(StringBuilder sb, ImpactResult result)
    {
        // Benchmark finding (docs/BENCHMARK-CODEMAP-VS-BASELINE.md, Q2): a bare "(0)" here reads as "no impact" —
        // but 0 only means "0 RECOGNIZED entry points" (Controller/BackgroundService/MediatR handler). A Razor
        // Page, Minimal API, or any other caller type this tool doesn't classify yet still shows up as a real
        // caller in IntermediateCallers; that's a materially different, much less reassuring situation and needs
        // its own header instead of collapsing into the same "(0)" as a genuine leaf.
        if (result.EntryPoints.Count == 0 && result.IntermediateCallers.Count > 0)
        {
            sb.AppendLine("## Affected entry points: 0 recognized");
            sb.AppendLine();
            sb.AppendLine(
                $"⚠ {result.IntermediateCallers.Count} caller(s) were reached that this tool could not classify " +
                "into a known entry point type (not a Controller, BackgroundService, or MediatR handler — could " +
                "be a Razor Page, Minimal API endpoint, or something else not yet recognized). This is NOT the " +
                "same as \"no impact\" — see \"Intermediate callers\" below (rerun with `--full` if not already).");
            sb.AppendLine();
            return;
        }

        // Task "sửa renderer (ưu tiên binding thật)": docs/BENCHMARK-INTERFACE-EXPANSION.md found interface-expand
        // can produce genuine over-inference (typically decorator pattern) — an entry point reached ONLY through
        // such an edge is real (di-confirmed.json positively shows a DIFFERENT implementation is DI-bound at
        // that call site), just not the confirmed path. It's demoted to its own subsection instead of mixed
        // into the primary, headline-count list.
        var confirmed = result.EntryPoints.Where(e => e.IsConfirmedBinding).ToList();
        var unconfirmed = result.EntryPoints.Where(e => !e.IsConfirmedBinding).ToList();

        sb.AppendLine($"## Affected entry points ({confirmed.Count})");
        sb.AppendLine();
        if (confirmed.Count == 0)
        {
            sb.AppendLine("_None reachable within the scanned depth — increase --depth, or this really is a leaf._");
        }
        else
        {
            AppendEntryPointGroups(sb, confirmed, "###");
        }

        sb.AppendLine();

        if (unconfirmed.Count > 0)
        {
            sb.AppendLine($"### Other possible implementations ({unconfirmed.Count}) — via interface, not the confirmed DI binding");
            sb.AppendLine();
            sb.AppendLine(
                "_di-confirmed.json shows a different sibling implementation is the one actually DI-bound at " +
                "these call sites (commonly a decorator pattern — see docs/BENCHMARK-INTERFACE-EXPANSION.md). " +
                "Listed for completeness, not as the primary path._");
            sb.AppendLine();
            AppendEntryPointGroups(sb, unconfirmed, "####");
            sb.AppendLine();
        }
    }

    private static void AppendEntryPointGroups(StringBuilder sb, List<ReachedEntryPoint> entryPoints, string headingLevel)
    {
        foreach (var group in entryPoints.GroupBy(e => e.Project, StringComparer.Ordinal).OrderByDescending(g => g.Count()))
        {
            sb.AppendLine($"{headingLevel} {group.Key} ({group.Count()})");
            foreach (var ep in group.OrderBy(e => e.Depth).ThenBy(e => e.DisplayName, StringComparer.Ordinal))
            {
                var kindLabel = $"[{ep.Type}]";
                var routeText = ep.HttpMethod != null ? $"{ep.HttpMethod,-6} {ep.Route}" : null;
                var line = routeText != null
                    ? $"- {kindLabel,-9} {routeText,-28} → {ep.DisplayName}  (depth {ep.Depth})"
                    : $"- {kindLabel,-9} {ep.DisplayName}  (depth {ep.Depth})";
                sb.AppendLine(line);
            }
        }
    }

    private static void AppendScreens(StringBuilder sb, ImpactResult result)
    {
        var featureCount = result.Screens.Select(s => s.Feature).Distinct(StringComparer.Ordinal).Count();
        sb.AppendLine($"## Affected FE screens ({featureCount})");
        sb.AppendLine();
        if (result.Screens.Count == 0)
        {
            sb.AppendLine("_None found — either no frontend call reaches this endpoint, or `scan-fe`/`link` haven't been run._");
        }
        else
        {
            foreach (var group in result.Screens.GroupBy(s => s.Feature, StringComparer.Ordinal))
            {
                sb.AppendLine($"### {group.Key}");
                foreach (var s in group)
                {
                    var confidenceNote = s.Confidence == "low" ? " (low confidence)" : "";
                    var matchNote = s.MatchKind == "ambiguous" ? " (ambiguous match)" : "";
                    sb.AppendLine($"- {s.HttpMethod} {s.Route} — {ScreenLocation(s)}{confidenceNote}{matchNote}");
                }
            }
        }

        sb.AppendLine();
    }

    /// <summary>Task "nối FE thiếu 1 hop": a service's file isn't what a user sees — the component(s) that inject it are. Falls back to the raw file:line when InjectedByComponents is empty (either the call is already inside a component — that file IS the screen — or scan-fe genuinely couldn't resolve it, noted separately in diagnostics.json).</summary>
    private static string ScreenLocation(ReachedScreen s) =>
        s.InjectedByComponents.Count > 0
            ? $"{string.Join(", ", s.InjectedByComponents)}  (service: {s.FrontendFile}:{s.FrontendLine})"
            : $"{s.FrontendFile}:{s.FrontendLine}";

    private static void AppendTests(StringBuilder sb, ImpactResult result)
    {
        sb.AppendLine($"## Tests reached ({result.TestsReached.Count})");
        sb.AppendLine();
        if (result.TestsReached.Count == 0)
            sb.AppendLine("_None found within the scanned depth._");
        else
            foreach (var t in result.TestsReached) sb.AppendLine($"- {t}");
        sb.AppendLine();
    }

    private static void AppendRiskScore(StringBuilder sb, ImpactResult result)
    {
        sb.AppendLine($"## Risk score: {result.RiskScore}/10");
        sb.AppendLine(
            $"{result.EntryPoints.Count} entry point(s) · {result.TestsReached.Count} test(s) · " +
            $"{result.ViaInterfaceCount} interface hop(s) · {result.ViaMediatrCount} MediatR hop(s)");
        sb.AppendLine();
    }

    private static void AppendIntermediateCallers(StringBuilder sb, ImpactResult result)
    {
        sb.AppendLine($"## Intermediate callers ({result.IntermediateCallers.Count})");
        sb.AppendLine();
        if (result.IntermediateCallers.Count == 0)
        {
            sb.AppendLine("_None — every reached symbol is either an entry point or a test._");
        }
        else
        {
            foreach (var group in result.IntermediateCallers.GroupBy(c => c.Project, StringComparer.Ordinal))
            {
                sb.AppendLine($"### {group.Key} ({group.Count()})");
                foreach (var c in group.OrderBy(c => c.Depth).ThenBy(c => c.DisplayName, StringComparer.Ordinal))
                {
                    var note = c.IsConfirmedBinding ? "" : " (other possible implementation — see docs/BENCHMARK-INTERFACE-EXPANSION.md)";
                    sb.AppendLine($"- {c.DisplayName}  (depth {c.Depth}){note}");
                }
            }
        }

        sb.AppendLine();
    }

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

    private static List<string> CollectRelevantFiles(ImpactResult result)
    {
        var files = new List<string>();
        if (result.File != null) files.Add(result.File);
        return files;
    }
}
