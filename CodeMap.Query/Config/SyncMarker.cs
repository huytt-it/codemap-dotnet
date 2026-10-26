using CodeMap.Query.Json;

namespace CodeMap.Query.Config;

/// <summary>One step of a `sync` run, as recorded in <see cref="SyncMarker.Steps"/>.</summary>
public sealed class SyncStepResult
{
    public const string Ok = "ok";

    /// <summary>The step wrote part of its output and said so (scan-git exit 2: co-change written, tickets refused).</summary>
    public const string Partial = "partial";

    public const string Failed = "failed";
    public const string Skipped = "skipped";

    public required string Name { get; init; }
    public required string Status { get; init; }

    /// <summary>Why a step was skipped, failed or only partly ran. Absent for a clean "ok".</summary>
    public string? Detail { get; init; }
}

/// <summary>
/// <c>index/sync-complete.json</c> — written by `codemap sync` as the last thing before the new index is swapped
/// in. The file being present means every required step (scan, map) finished and the directory it sits in is the
/// complete result of one run; it is never written into a half-built directory. A long-lived reader (an MCP
/// server holding the index in memory) reloads when <see cref="SyncId"/> changes. `scan` and the other commands
/// run by hand write no marker, so an index without one was not produced by `sync`.
/// </summary>
public sealed class SyncMarker
{
    public const string FileName = "sync-complete.json";

    public int SchemaVersion { get; init; } = 1;

    /// <summary>A fresh GUID (32 hex digits) per completed sync. Compare it, not timestamps, to detect a new index.</summary>
    public required string SyncId { get; init; }

    /// <summary>UTC, <c>yyyy-MM-ddTHH:mm:ssZ</c> — the same format as meta.json's indexedAt.</summary>
    public required string CompletedAt { get; init; }

    public required string Project { get; init; }

    /// <summary>meta.json's gitCommit: the HEAD the scan saw. Absent when the solution is not in a git repo.</summary>
    public string? GitCommit { get; init; }

    /// <summary>meta.json's indexedAt at the time of the sync. If meta.json says something else now, `scan` was run by hand on top of this index afterwards.</summary>
    public string? MetaIndexedAt { get; init; }

    /// <summary>Every step in pipeline order, with the ones that did not run marked skipped and why.</summary>
    public List<SyncStepResult> Steps { get; init; } = new();

    internal static void Write(string indexDir, SyncMarker marker)
        => JsonUtil.WriteIndented(Path.Combine(indexDir, FileName), marker);

    /// <summary>The marker in <paramref name="indexDir"/>, or null — with <paramref name="problem"/> set when a file exists but cannot be read.</summary>
    public static SyncMarker? TryRead(string indexDir, out string? problem)
    {
        problem = null;
        var path = Path.Combine(indexDir, FileName);
        if (!File.Exists(path)) return null;

        try
        {
            var marker = JsonUtil.ReadFile<SyncMarker>(path);
            if (marker == null) problem = $"{FileName} is empty";
            return marker;
        }
        catch (Exception ex)
        {
            problem = $"{FileName} unreadable ({ex.Message})";
            return null;
        }
    }
}
