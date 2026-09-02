using CodeMap.Query.Json;
using CodeMap.Query.Models;

namespace CodeMap.Query.Git;

/// <summary>
/// `git log` always reports paths relative to the git repository root, whatever directory it was invoked from.
/// The Roslyn scan records symbol paths relative to the SOLUTION directory instead (SemanticScanner's
/// ToRelativePath). Those two agree only when the solution sits at the repo root — the common layout, and the
/// reason this went unnoticed. When the solution sits deeper (`src/App.sln`), every path written by scan-git
/// fails the exact-string join in ImpactEngine and WhereEngine: `where` silently loses its highest-weighted
/// source (TicketWeight 3.0, the only one that reads natural language) and `impact` loses tickets and co-change
/// outright, with no error, no empty file, and nothing in diagnostics.json to notice.
///
/// meta.json already records solutionPath relative to the repo root, so its directory part is exactly the
/// prefix to strip. Rebasing here, at write time, keeps every consumer on one path convention.
/// </summary>
internal static class GitPathRebaser
{
    /// <summary>
    /// The solution's directory as a repo-root-relative prefix ("src", "app/backend"), or null when the
    /// solution is at the repo root (nothing to do) or meta.json is missing/unreadable (nothing to do it with).
    /// </summary>
    public static string? ReadSolutionPrefix(string indexDir)
    {
        var metaPath = Path.Combine(indexDir, "meta.json");
        if (!File.Exists(metaPath)) return null;

        MetaModel? meta;
        try
        {
            meta = JsonUtil.ReadFile<MetaModel>(metaPath);
        }
        catch
        {
            // A corrupt meta.json is scan's problem to report, not a reason to abort collecting git history.
            return null;
        }

        var prefix = Path.GetDirectoryName(meta?.SolutionPath ?? string.Empty)
            ?.Replace('\\', '/')
            .Trim('/');

        return string.IsNullOrEmpty(prefix) || prefix == "." ? null : prefix;
    }

    /// <summary>Repo-root-relative (git) to solution-relative (Roslyn).</summary>
    public static string Rebase(string prefix, string repoRelativePath)
    {
        if (repoRelativePath.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
            return repoRelativePath[(prefix.Length + 1)..];

        // Outside the solution directory. Kept rather than dropped: it can never match a symbol, but a
        // co-change pair with one leg outside — a stored procedure in db/, a shared .json config — is exactly
        // the kind of relationship static analysis is blind to, which is why co-change exists at all.
        var up = string.Concat(Enumerable.Repeat("../", prefix.Split('/').Length));
        return up + repoRelativePath;
    }
}
