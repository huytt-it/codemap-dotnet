using System.ComponentModel;
using CodeMap.Query.Config;
using CodeMap.Query.Impact;
using CodeMap.Query.Models;
using CodeMap.Query.Reporting;
using ModelContextProtocol.Server;

namespace CodeMap.Mcp;

/// <summary>
/// <c>codemap_status</c>: does CodeMap cover this workspace, and how far can its index be trusted right now. About
/// ten lines, one fact per line, ending with the one thing to do next.
///
/// Unlike the other tools it does not fail when the workspace is not indexed or the index is missing: whether
/// CodeMap applies here is the question it answers, so those come back as its answer, with the same coded message
/// the other tools would have failed with.
/// </summary>
internal sealed class StatusTool(ToolContext context)
{
    private const int LabelWidth = 10;

    /// <summary>Below this many ticket records a "they look like PR numbers" verdict is noise.</summary>
    private const int MinTicketsForPrCheck = 5;

    public static McpServerTool Create(ToolContext context) =>
        McpServerTool.Create(new StatusTool(context).RunAsync, new McpServerToolCreateOptions
        {
            Name = "codemap_status",
            Title = "CodeMap status",
            Description = ToolText.Status,
            // Spelled out because the SDK defaults are the opposite (Destructive and OpenWorld default to true).
            ReadOnly = true,
            Destructive = false,
            Idempotent = true,
            OpenWorld = false,
        });

    public Task<string> RunAsync(
        [Description(ToolText.ProjectParam)] string? project = null,
        CancellationToken cancellationToken = default)
        => ToolContext.Run("codemap_status", () => Render(project, cancellationToken), cancellationToken);

    public string Render(string? project, CancellationToken cancellationToken = default)
    {
        var lines = new List<string>();
        var workspace = context.Resolver.Workspace;

        ResolvedProject resolved;
        try
        {
            resolved = context.Resolver.Resolve(project);
        }
        catch (CodeMapToolException ex)
        {
            lines.Add("codemap · not available for this workspace");
            AddWorkspace(lines, workspace);
            Add(lines, "reason", ex.Message);
            return Join(lines);
        }

        CachedIndex? index = null;
        CodeMapToolException? indexError = null;
        try
        {
            index = context.Indexes.Get(resolved.Name, resolved.IndexDir, cancellationToken);
        }
        catch (CodeMapToolException ex)
        {
            indexError = ex;
        }

        var staleness = index == null ? null : context.Staleness(resolved, index);
        lines.Add(index == null ? $"codemap · {resolved.Name} · no index" : ToolContext.FreshnessLine(resolved, index, staleness!));
        AddWorkspace(lines, workspace);
        Add(lines, "registry", resolved.Registry.SourcePath);
        var description = string.IsNullOrWhiteSpace(resolved.Entry.Description) ? "" : $": {resolved.Entry.Description}";
        Add(lines, "project", $"{resolved.Name}{description} ({RuleText(resolved.Rule)}) · repo {resolved.RepoRoot}");

        var sync = $"run `codemap sync --project {resolved.Name}`";
        if (index == null)
        {
            Add(lines, "index", indexError!.Message);
            Add(lines, "next", $"{sync} to build the index, then ask again");
            return Join(lines);
        }

        Add(lines, "index", $"{index.IndexDir} · {OriginText(index)}");
        Add(lines, "contents", ContentsText(index.Index));

        var degraded = DegradedProjects(index.Index);
        if (degraded.Count > 0)
            Add(lines, "warning", $"{degraded.Count} project(s) were scanned without a full compile, so their call edges are incomplete: {string.Join(", ", degraded)}");

        if (!string.IsNullOrWhiteSpace(resolved.Entry.CommitLanguage))
            Add(lines, "tickets", $"commit and ticket text is in '{resolved.Entry.CommitLanguage}': search it in that language");

        if (PullRequestNumberWarning(index.Index.Tickets, resolved.Name) is { } prWarning)
            Add(lines, "warning", prWarning);

        Add(lines, "next", NextStep(resolved, index, staleness!, sync));
        return Join(lines);
    }

    private static void AddWorkspace(List<string> lines, Workspace workspace)
    {
        Add(lines, "workspace", $"{workspace.Path} (from {workspace.SourceLabel})");
        foreach (var warning in workspace.Warnings) Add(lines, "note", warning);
    }

    private static string RuleText(MatchRule rule) => rule switch
    {
        MatchRule.ProjectParameter => "named by the project parameter",
        MatchRule.InsideSolutionDir => "the workspace is inside its solution folder",
        MatchRule.InsideRepoRoot => "the workspace is inside its repository",
        MatchRule.InsideFrontend => "the workspace is inside its frontend",
        _ => "the workspace is a parent folder of its repository",
    };

    private static string OriginText(CachedIndex index)
    {
        var completedAt = index.Marker?.CompletedAt;
        switch (index.Origin)
        {
            case IndexOrigin.ScanByHand:
                return "built by running the steps by hand (no sync-complete.json), so nothing says they all ran against the same code";
            case IndexOrigin.ChangedAfterSync:
                return $"changed after the `codemap sync` of {completedAt} (meta.json no longer matches sync-complete.json): scan was run by hand on top of it";
            case IndexOrigin.MarkerUnreadable:
                return "sync-complete.json exists but cannot be read";
        }

        var notable = index.Marker!.Steps
            .Where(s => s.Status != SyncStepResult.Ok)
            .Select(s => $"{s.Name} {s.Status}{(s.Detail == null ? "" : $" ({s.Detail})")}")
            .ToList();
        return $"complete `codemap sync` at {completedAt}" + (notable.Count == 0 ? "" : "; " + string.Join(", ", notable));
    }

    private static string ContentsText(ImpactIndex index)
    {
        var text = $"{index.SymbolsById.Count} symbol(s) · {index.ReverseEdges.Values.Sum(e => e.Count)} edge(s) · " +
                   $"{index.EntryPointsById.Count} entry point(s) · {index.Tickets.Count} ticket record(s) · " +
                   $"{index.CoChanges.Count} co-change pair(s)";
        if (index.FrontendCallsById.Count > 0)
            text += $" · {index.FrontendCallsById.Count} frontend call(s), {index.ApiLinksByBackendId.Values.Sum(l => l.Count)} linked to the API";
        return text;
    }

    private static List<string> DegradedProjects(ImpactIndex index)
        => index.Diagnostics?.DegradedProjects.Select(d => d.Project).ToList() is { Count: > 0 } fromDiagnostics
            ? fromDiagnostics
            : index.Meta?.DegradedProjects ?? new List<string>();

    /// <summary>
    /// The default ticketPattern accepts "#123", which is also how GitHub writes a pull request: "(#123)" at the end
    /// of a squash-merge subject, "Merge pull request #123" for a merge commit. When most ticket IDs show up exactly
    /// like that, the "tickets" are PR numbers (true for eShopOnWeb), and ticket-based ranking quietly means less.
    /// </summary>
    private static string? PullRequestNumberWarning(IReadOnlyCollection<TicketFileRecord> tickets, string project)
    {
        if (tickets.Count < MinTicketsForPrCheck) return null;

        var prLike = tickets.Count(t =>
            t.Ticket.All(char.IsAsciiDigit)
            && (t.Message.Contains($"(#{t.Ticket})", StringComparison.Ordinal)
                || t.Message.Contains($"pull request #{t.Ticket}", StringComparison.OrdinalIgnoreCase)));
        if (prLike * 2 <= tickets.Count) return null;

        return $"{prLike} of {tickets.Count} ticket ID(s) appear in their commit subject as \"(#N)\" or \"pull request #N\", " +
               "so they are probably pull request numbers rather than tickets. If the team's tickets have their own IDs, set " +
               $"ticketPattern in codemap.config.json at the repo root and run `codemap sync --project {project}`.";
    }

    private static string NextStep(ResolvedProject project, CachedIndex index, Staleness staleness, string sync)
    {
        var reasons = new List<string>();
        if (index.Problem != null) reasons.Add("the index on disk could not be used (see the first line)");
        if (index.Origin != IndexOrigin.CompleteSync) reasons.Add("it was not built by a complete sync");
        if (staleness.State == StalenessState.Behind)
            reasons.Add(staleness.CommitsBehind is { } n ? $"it is {n} commit(s) behind HEAD" : "HEAD has moved since the scan");
        if (staleness.State == StalenessState.NoMeta) reasons.Add("it has no meta.json, so its age is unknown");
        if (reasons.Count > 0) return $"{sync}: {string.Join("; ", reasons)}";

        return staleness.State switch
        {
            StalenessState.NoScanCommit => "nothing CodeMap can check: the code was not in a git repository when it was scanned",
            StalenessState.GitUnavailable => $"staleness cannot be checked: git could not be run in {project.RepoRoot}",
            _ when staleness.UncommittedFiles is > 0 and var dirty =>
                $"nothing to sync; {dirty} file(s) have uncommitted changes, so answers about those files may not match the code",
            _ when staleness.UncommittedFiles == null => "nothing to sync; uncommitted changes could not be checked",
            _ => "nothing to do: the index matches HEAD",
        };
    }

    private static void Add(List<string> lines, string label, string text) => lines.Add(label.PadRight(LabelWidth) + text);

    private static string Join(List<string> lines) => string.Join("\n", lines) + "\n";
}
