using System.Collections.Concurrent;
using System.Text;
using CodeMap.Query.Git;
using CodeMap.Query.Impact;
using CodeMap.Query.Models;

namespace CodeMap.Query.Reporting;

/// <summary>
/// Spec section 7.5: every markdown artifact must open with this block. Content translated to English per this
/// project's language convention (source/runtime output in English; only docs/ in the CODEMAP-SPEC.md sense are
/// Vietnamese) — same call already made for MAP.md's section headers.
///
/// Git commands run against the repo the caller names (<c>cwdOverride</c>) — for a registered project, the
/// registry's repo, so the answer does not depend on where the process was started (an MCP server's cwd is its
/// client's choice). With a bare `--index` there is nothing to name one, so the old convention holds: the user
/// runs the command from inside the target repo (like any git-aware CLI) and git auto-discovers the root upward.
///
/// The comparison itself is <see cref="Compute"/>, which returns the facts; <see cref="Render"/> turns them into the
/// markdown block and <see cref="RenderLine"/> into the one line an MCP tool result opens with.
/// </summary>
public static class StalenessBanner
{
    private const string ScopeNote =
        "> **Scope of this file.** This is a static index auto-generated from Roslyn and git log.\n" +
        "> **TRUSTWORTHY FOR:** C# call relationships, API routes, DI mappings, edit history.\n" +
        "> **NOT COVERED:** reflection, assembly scanning, stored procedures, runtime logic,\n" +
        "> environment-specific config, real data in the database.\n" +
        "> If a question falls into the NOT COVERED category, say plainly that this file can't answer it.\n";

    /// <param name="relevantFiles">The files the answer depends on, in the index's own path space: relative to the scanned solution's directory, as symbols.jsonl records them. Git's repo-root-relative paths are rebased onto that space before comparing (GitPathRebaser).</param>
    /// <param name="cwdOverride">The repo to run git in. Null means Environment.CurrentDirectory (a bare `--index`, see class doc). Pass the registered project's repo, so the output is the same wherever the process runs; tests pass one too, instead of mutating global process state.</param>
    public static string Render(MetaModel? meta, IReadOnlyCollection<string>? relevantFiles = null, string? cwdOverride = null)
    {
        var sb = new StringBuilder();
        sb.Append("<!-- codemap v2 · ").Append(HeaderLine(meta)).Append('\n');
        sb.Append("     ").Append(StalenessLine(Compute(meta, relevantFiles, cwdOverride))).Append(" -->\n\n");
        sb.Append(ScopeNote);
        return sb.ToString();
    }

    /// <summary>
    /// How the repo now differs from the scan, as far as git can tell. Parameters as for <see cref="Render"/>;
    /// <paramref name="cache"/> lets a long-lived caller reuse the committed half of the answer (see
    /// <see cref="CommittedChangesCache"/>). The uncommitted half is asked of git on every call.
    /// </summary>
    public static Staleness Compute(
        MetaModel? meta, IReadOnlyCollection<string>? relevantFiles = null, string? repo = null, CommittedChangesCache? cache = null)
    {
        var relevantOnly = relevantFiles is { Count: > 0 };
        if (meta == null) return Unknown(StalenessState.NoMeta);
        if (meta.GitCommit == null) return Unknown(StalenessState.NoScanCommit);

        var cwd = repo ?? Environment.CurrentDirectory;
        var currentHead = GitRepoInfo.TryGetHeadCommit(cwd);
        if (currentHead == null) return Unknown(StalenessState.GitUnavailable);

        // git reports repo-root-relative paths; the index (and so relevantFiles) is relative to the solution's own
        // directory. They differ whenever the solution is not at the repo root, so bring git's side over.
        var prefix = GitPathRebaser.PrefixOf(meta);
        List<string>? InIndexSpace(List<string>? gitPaths) =>
            prefix == null ? gitPaths : gitPaths?.Select(f => GitPathRebaser.Rebase(prefix, f)).ToList();

        // Same path comparison as every other file join in the index: case-insensitive only where the file system is.
        var relevantSet = relevantOnly ? new HashSet<string>(relevantFiles!, FilePathComparison.Comparer) : null;
        int CountIn(IEnumerable<string> files) => relevantSet == null ? files.Count() : files.Count(relevantSet.Contains);

        var uncommitted = InIndexSpace(GitRepoInfo.TryGetUncommittedFiles(cwd));
        int? dirty = uncommitted == null ? null : CountIn(uncommitted);

        // HEAD matching is not enough: an edit that is not committed yet is invisible to it.
        if (string.Equals(currentHead, meta.GitCommit, StringComparison.OrdinalIgnoreCase))
            return new Staleness(StalenessState.AtScannedCommit, meta.GitCommit, currentHead, 0, dirty, dirty, relevantOnly, repo);

        var changes = cache?.Get(cwd, meta.GitCommit, currentHead) ?? CommittedChangesCache.Ask(cwd, meta.GitCommit, currentHead);
        var committed = InIndexSpace(changes.Files);

        // A file can be both committed-over and dirty; Union counts it once.
        int? changed = committed == null
            ? null
            : CountIn(uncommitted == null ? committed : committed.Union(uncommitted, FilePathComparison.Comparer));
        return new Staleness(StalenessState.Behind, meta.GitCommit, currentHead, changes.CommitCount, changed, dirty, relevantOnly, repo);

        Staleness Unknown(StalenessState state) => new(state, meta?.GitCommit, null, null, null, null, relevantOnly, repo);
    }

    /// <summary>
    /// <c>codemap · eShopOnWeb · HEAD 4da8212 · index fresh</c>, or the same with what changed: the first line of
    /// every MCP tool result, where the markdown banner's two comment lines and scope note would cost a model
    /// seven lines on every call. The scope note lives in the codemap_status tool description instead.
    /// </summary>
    public static string RenderLine(string project, Staleness staleness)
    {
        var head = $"HEAD {Short(staleness.CurrentHead)}";
        var scope = staleness.RelevantOnly ? "relevant file(s)" : "file(s)";
        var text = staleness.State switch
        {
            StalenessState.NoMeta => "staleness unknown (no meta.json)",
            StalenessState.NoScanCommit => "staleness unknown (not a git repo at scan time)",
            StalenessState.GitUnavailable => $"staleness unknown (could not run git in {staleness.Repo ?? "the current directory"})",
            StalenessState.AtScannedCommit => staleness.UncommittedFiles switch
            {
                null => $"{head} · at the scanned commit (uncommitted changes not checked)",
                0 => $"{head} · index fresh",
                var n => $"{head} · at the scanned commit, {n} {scope} changed ({n} uncommitted)",
            },
            _ => $"{head} · {CommitsBehindText(staleness, "commit count unknown")}, " + (staleness.ChangedFiles is { } changed
                ? $"{changed} {scope} changed{UncommittedSuffix(staleness)}"
                : "changed-file count unknown"),
        };
        return $"codemap · {project} · {text}";
    }

    private static string HeaderLine(MetaModel? meta)
        => meta == null
            ? "no meta.json found"
            : $"index commit {Short(meta.GitCommit)} · scanned {meta.IndexedAt}";

    private static string StalenessLine(Staleness staleness)
    {
        var head = $"current HEAD {Short(staleness.CurrentHead)}";
        var scope = staleness.RelevantOnly ? "relevant file(s)" : "file(s)";
        switch (staleness.State)
        {
            case StalenessState.NoMeta:
                return "staleness unknown (no meta.json — run `codemap scan` to produce one)";
            case StalenessState.NoScanCommit:
                return "staleness unknown (target solution wasn't a git repo, or git wasn't on PATH at scan time)";
            case StalenessState.GitUnavailable:
                return staleness.Repo == null
                    ? "could not run git in the current directory — run this command from inside the target repo for a staleness comparison"
                    : $"could not run git in {staleness.Repo} — check that the project's repo in codemap.projects.json exists and is a git repository";
            case StalenessState.AtScannedCommit:
                return staleness.UncommittedFiles switch
                {
                    null => $"{head} · matches the scan (could not check for uncommitted changes)",
                    0 => $"{head} · matches the scan, index is fresh",
                    var dirty => $"{head} · matches the scan, but {dirty} {scope} have uncommitted changes",
                };
        }

        var fileText = staleness.ChangedFiles is not { } changed
            ? "changed-file count unknown"
            : (staleness.RelevantOnly
                ? $"{changed} relevant file(s) changed since the scan"
                : $"{changed} file(s) changed in the repo since the scan") + UncommittedSuffix(staleness);
        return $"{head} · {CommitsBehindText(staleness, "commit count behind unknown")}, {fileText}";
    }

    private static string CommitsBehindText(Staleness staleness, string unknown)
        => staleness.CommitsBehind is { } n ? $"{n} commit(s) behind" : unknown;

    private static string UncommittedSuffix(Staleness staleness)
        => staleness.UncommittedFiles is > 0 and var dirty ? $" ({dirty} uncommitted)" : "";

    private static string Short(string? commit) => commit == null ? "unknown" : commit.Length > 7 ? commit[..7] : commit;
}

public enum StalenessState
{
    /// <summary>No meta.json, so no scanned commit to compare with.</summary>
    NoMeta,

    /// <summary>meta.json has no gitCommit: the solution was not in a git repo, or git was not on PATH, at scan time.</summary>
    NoScanCommit,

    /// <summary>git cannot be run in the repo now.</summary>
    GitUnavailable,

    /// <summary>HEAD is the scanned commit; only uncommitted edits can have made the index stale.</summary>
    AtScannedCommit,

    /// <summary>HEAD has moved since the scan.</summary>
    Behind,
}

/// <param name="CommitsBehind">Commits from the scanned one to HEAD; 0 at the scanned commit, null when git could not count them.</param>
/// <param name="ChangedFiles">Files changed since the scan, committed or not, each counted once, and only those in the relevant set when one was given. Null when git could not list the committed ones.</param>
/// <param name="UncommittedFiles">The uncommitted part of <paramref name="ChangedFiles"/>. Null when git could not list them.</param>
/// <param name="RelevantOnly">The counts are limited to the files the answer depends on.</param>
/// <param name="Repo">The repo git ran in; null means the process's working directory (a bare `--index`).</param>
public sealed record Staleness(
    StalenessState State, string? ScannedCommit, string? CurrentHead, int? CommitsBehind, int? ChangedFiles, int? UncommittedFiles,
    bool RelevantOnly, string? Repo)
{
    /// <summary>HEAD is the scanned commit and no file in scope has uncommitted changes.</summary>
    public bool IsFresh => State == StalenessState.AtScannedCommit && UncommittedFiles == 0;
}

/// <summary>
/// The committed half of a staleness check (commit count and changed files from the scanned commit to HEAD) for a
/// caller that asks over and over, like the MCP server on every tool call. Both ends are commit hashes, so the answer
/// for one pair never changes and is kept; the uncommitted half changes with every save and is never kept here. A
/// git call that failed is not remembered. Thread-safe.
/// </summary>
public sealed class CommittedChangesCache
{
    /// <summary>HEAD moves a few times a day; this only bounds a server left running for weeks.</summary>
    private const int MaxEntries = 64;

    private readonly ConcurrentDictionary<string, CommittedChanges> _entries = new(FilePathComparison.Comparer);

    public int Count => _entries.Count;

    internal CommittedChanges Get(string repo, string scannedCommit, string head)
    {
        var key = $"{Path.GetFullPath(repo)}|{scannedCommit}|{head}";
        if (_entries.TryGetValue(key, out var known)) return known;

        var asked = Ask(repo, scannedCommit, head);
        if (asked.CommitCount == null || asked.Files == null) return asked;

        if (_entries.Count >= MaxEntries) _entries.Clear();
        return _entries[key] = asked;
    }

    /// <summary>Names HEAD by the hash already read, so the answer belongs to that exact pair even if HEAD moves meanwhile.</summary>
    internal static CommittedChanges Ask(string repo, string scannedCommit, string head)
        => new(GitRepoInfo.TryGetCommitCountSince(repo, scannedCommit, head), GitRepoInfo.TryGetChangedFilesSince(repo, scannedCommit, head));
}

internal sealed record CommittedChanges(int? CommitCount, List<string>? Files);
