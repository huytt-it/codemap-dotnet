using System.Text;
using CodeMap.Query.Git;
using CodeMap.Query.Models;

namespace CodeMap.Query.Reporting;

/// <summary>
/// Spec section 7.5: every markdown artifact must open with this block. Content translated to English per this
/// project's language convention (source/runtime output in English; only docs/ in the CODEMAP-SPEC.md sense are
/// Vietnamese) — same call already made for MAP.md's section headers.
///
/// Git commands run against the CURRENT WORKING DIRECTORY, not a `--repo` flag: none of the query commands
/// (map/impact/slice) take one, so the natural reading is that the user runs them from inside the target repo
/// (like any git-aware CLI), and git auto-discovers the repo root upward from there.
/// </summary>
public static class StalenessBanner
{
    private const string ScopeNote =
        "> **Scope of this file.** This is a static index auto-generated from Roslyn and git log.\n" +
        "> **TRUSTWORTHY FOR:** C# call relationships, API routes, DI mappings, edit history.\n" +
        "> **NOT COVERED:** reflection, assembly scanning, stored procedures, runtime logic,\n" +
        "> environment-specific config, real data in the database.\n" +
        "> If a question falls into the NOT COVERED category, say plainly that this file can't answer it.\n";

    /// <param name="cwdOverride">Defaults to Environment.CurrentDirectory (production use — see class doc). Tests pass an explicit repo path instead of mutating global process state.</param>
    public static string Render(MetaModel? meta, IReadOnlyCollection<string>? relevantFiles = null, string? cwdOverride = null)
    {
        var sb = new StringBuilder();
        sb.Append("<!-- codemap v2 · ").Append(HeaderLine(meta)).Append('\n');
        sb.Append("     ").Append(StalenessLine(meta, relevantFiles, cwdOverride ?? Environment.CurrentDirectory)).Append(" -->\n\n");
        sb.Append(ScopeNote);
        return sb.ToString();
    }

    private static string HeaderLine(MetaModel? meta)
        => meta == null
            ? "no meta.json found"
            : $"index commit {Short(meta.GitCommit)} · scanned {meta.IndexedAt}";

    private static string StalenessLine(MetaModel? meta, IReadOnlyCollection<string>? relevantFiles, string cwd)
    {
        if (meta == null)
            return "staleness unknown (no meta.json — run `codemap scan` to produce one)";
        if (meta.GitCommit == null)
            return "staleness unknown (target solution wasn't a git repo, or git wasn't on PATH at scan time)";

        var currentHead = GitRepoInfo.TryGetHeadCommit(cwd);
        if (currentHead == null)
            return "could not run git in the current directory — run this command from inside the target repo for a staleness comparison";

        if (string.Equals(currentHead, meta.GitCommit, StringComparison.OrdinalIgnoreCase))
            return $"current HEAD {Short(currentHead)} · matches the scan, index is fresh";

        var commitCount = GitRepoInfo.TryGetCommitCountSince(cwd, meta.GitCommit);
        var changedFiles = GitRepoInfo.TryGetChangedFilesSince(cwd, meta.GitCommit);

        var commitText = commitCount is { } n ? $"{n} commit(s) behind" : "commit count behind unknown";

        string fileText;
        if (changedFiles == null)
        {
            fileText = "changed-file count unknown";
        }
        else if (relevantFiles is { Count: > 0 })
        {
            var relevantSet = new HashSet<string>(relevantFiles, StringComparer.OrdinalIgnoreCase);
            var relevantChanged = changedFiles.Count(f => relevantSet.Contains(f));
            fileText = $"{relevantChanged} relevant file(s) changed since the scan";
        }
        else
        {
            fileText = $"{changedFiles.Count} file(s) changed in the repo since the scan";
        }

        return $"current HEAD {Short(currentHead)} · {commitText}, {fileText}";
    }

    private static string Short(string? commit) => commit == null ? "unknown" : commit.Length > 7 ? commit[..7] : commit;
}
