using CodeMap.Query.Config;
using CodeMap.Query.Impact;

namespace CodeMap.Mcp;

/// <summary>What produced the index on disk. codemap_status and the freshness line report it.</summary>
internal enum IndexOrigin
{
    /// <summary>A `codemap sync` that finished: sync-complete.json is there and meta.json is still the one it saw.</summary>
    CompleteSync,

    /// <summary>No marker: `scan` and the other steps were run by hand, so nothing says they all ran, or ran together.</summary>
    ScanByHand,

    /// <summary>A marker, but meta.json changed after it was written: `scan` was run by hand on top of a synced index.</summary>
    ChangedAfterSync,

    /// <summary>sync-complete.json exists and cannot be read.</summary>
    MarkerUnreadable,
}

/// <param name="Fingerprint">What decides "same index": the syncId after a complete sync; otherwise meta.json's indexedAt plus every index file's size and timestamp.</param>
/// <param name="Problem">Set when this is a copy loaded earlier, served because the index on disk is gone or could not be loaded. Null when it matches the disk.</param>
internal sealed record CachedIndex(
    string Project, string IndexDir, ImpactIndex Index, IndexOrigin Origin, SyncMarker? Marker, string Fingerprint,
    DateTime LoadedAtUtc, string? Problem);

/// <summary>
/// Each project's <see cref="ImpactIndex"/>, loaded on its first tool call and kept for the next ones (loading
/// eShopOnWeb took 61 ms in the M0 spike; a big codebase takes far longer, every call). Every call reads
/// index/sync-complete.json and reloads when its syncId has changed, which `codemap sync` guarantees once per
/// completed run; an index built by hand falls back to a fingerprint of its files.
///
/// sync builds the new index in a staging directory and swaps it in with two renames, so for a few milliseconds
/// there is no index/ at all, and a load running across the swap can read old and new files together. Both are
/// handled the same way: probe the index before and after loading, and if it was missing or changed in between,
/// wait briefly and load again. A missing symbols.jsonl is not an error for <see cref="ImpactIndex.Load"/> (it reads
/// as zero rows), so without that check a call landing mid-swap would get an empty index and report "no callers".
///
/// The previous copy is replaced only by a complete, successful load. When a load fails, or the index has gone,
/// the previous copy is served with <see cref="CachedIndex.Problem"/> set.
///
/// Synchronous and thread-safe: tools call it inside Task.Run; two calls for one project load it once.
/// </summary>
internal sealed class IndexCache
{
    /// <summary>sync's two renames take milliseconds; together these wait up to ~0.4 s before giving up.</summary>
    public static readonly IReadOnlyList<TimeSpan> DefaultSwapRetryDelays =
        new[] { 25, 50, 100, 200 }.Select(ms => TimeSpan.FromMilliseconds(ms)).ToArray();

    private const string SymbolsFile = "symbols.jsonl";

    private readonly IReadOnlyList<TimeSpan> _retryDelays;
    private readonly Func<string, ImpactIndex> _load;
    private readonly Action<TimeSpan> _sleep;
    private readonly object _gate = new();
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.OrdinalIgnoreCase); // registry names are case-insensitive

    /// <param name="load">Tests substitute this to count loads or to make one overlap a swap.</param>
    /// <param name="sleep">Tests substitute this to act during the wait between attempts.</param>
    public IndexCache(
        IReadOnlyList<TimeSpan>? swapRetryDelays = null, Func<string, ImpactIndex>? load = null, Action<TimeSpan>? sleep = null)
    {
        _retryDelays = swapRetryDelays ?? DefaultSwapRetryDelays;
        _load = load ?? ImpactIndex.Load;
        _sleep = sleep ?? Thread.Sleep;
    }

    /// <summary>
    /// The project's index as it is on disk now, or the copy loaded earlier with <see cref="CachedIndex.Problem"/>
    /// set. Throws <see cref="CodeMapToolException"/> (INDEX_MISSING, INDEX_UNREADABLE) only when there is no
    /// earlier copy to fall back on.
    /// </summary>
    public CachedIndex Get(string project, string indexDir, CancellationToken cancellationToken = default)
    {
        var slot = SlotFor(project);
        lock (slot.Gate)
            return Refresh(slot, project, Path.GetFullPath(indexDir), cancellationToken);
    }

    private Slot SlotFor(string project)
    {
        lock (_gate)
        {
            if (!_slots.TryGetValue(project, out var slot))
                _slots[project] = slot = new Slot();
            return slot;
        }
    }

    private CachedIndex Refresh(Slot slot, string project, string indexDir, CancellationToken cancellationToken)
    {
        // The registry can move a project's output; a copy loaded from the old place says nothing about the new one.
        var current = slot.Loaded != null && string.Equals(slot.Loaded.IndexDir, indexDir, FilePathComparison.Comparison)
            ? slot.Loaded
            : null;
        var rebuild = $"Run `codemap sync --project {project}`";

        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var canRetry = attempt < _retryDelays.Count;

            var before = Probe(indexDir);
            if (before == null)
            {
                if (canRetry)
                {
                    _sleep(_retryDelays[attempt]);
                    continue;
                }

                if (current != null)
                    return current with { Problem = $"the index directory {indexDir} no longer exists; answering from the copy loaded at {Time(current)}. {rebuild}." };
                throw new CodeMapToolException(CodeMapToolException.IndexMissing,
                    $"project '{project}' has no index at {indexDir}. {rebuild} to build it, then ask again.");
            }

            if (current != null && before.Fingerprint == current.Fingerprint)
                return current;

            ImpactIndex index;
            try
            {
                index = _load(indexDir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file vanishing or locked mid-read is the swap again, or `scan` writing in place.
                if (canRetry)
                {
                    _sleep(_retryDelays[attempt]);
                    continue;
                }

                return Failed(ex.Message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The content itself is broken: waiting and loading again would fail the same way.
                return Failed(ex.Message);
            }

            if (Probe(indexDir)?.Fingerprint != before.Fingerprint)
            {
                // The directory was swapped or rewritten while it was being read: the result may mix two indexes.
                if (canRetry)
                {
                    _sleep(_retryDelays[attempt]);
                    continue;
                }

                return Failed("the index kept changing while it was being loaded");
            }

            return slot.Loaded = new CachedIndex(
                project, indexDir, index, before.Origin, before.Marker, before.Fingerprint, DateTime.UtcNow, Problem: null);
        }

        CachedIndex Failed(string reason)
        {
            if (current != null)
                return current with { Problem = $"could not load the index at {indexDir} ({reason}); answering from the copy loaded at {Time(current)}. {rebuild}." };
            throw new CodeMapToolException(CodeMapToolException.IndexUnreadable,
                $"could not load the index of project '{project}' at {indexDir} ({reason}). {rebuild} to rebuild it, then ask again.");
        }
    }

    /// <summary>Null when there is no index to load right now (no symbols.jsonl, or the directory went away while it was being looked at).</summary>
    private static IndexProbe? Probe(string indexDir)
    {
        try
        {
            if (!File.Exists(Path.Combine(indexDir, SymbolsFile))) return null;

            var marker = SyncMarker.TryRead(indexDir, out var markerProblem);
            var indexedAt = ProjectsCommand.ReadMetaIndexedAt(indexDir);
            if (marker != null)
            {
                // Same rule as `codemap projects`, so the CLI and the server agree on what "changed since the sync" means.
                var changedAfterSync = marker.MetaIndexedAt != null && indexedAt != null
                    && !string.Equals(marker.MetaIndexedAt, indexedAt, StringComparison.Ordinal);
                if (!changedAfterSync) return new IndexProbe($"sync {marker.SyncId}", IndexOrigin.CompleteSync, marker);
            }

            var origin = marker != null ? IndexOrigin.ChangedAfterSync
                : markerProblem != null ? IndexOrigin.MarkerUnreadable
                : IndexOrigin.ScanByHand;
            var files = new DirectoryInfo(indexDir).EnumerateFiles()
                .OrderBy(f => f.Name, StringComparer.Ordinal)
                .Select(f => $"{f.Name}:{f.Length}:{f.LastWriteTimeUtc.Ticks}");
            return new IndexProbe($"files {indexedAt} {string.Join(";", files)}", origin, marker);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Time(CachedIndex index) => index.LoadedAtUtc.ToString("yyyy-MM-dd HH:mm:ss") + "Z";

    private sealed record IndexProbe(string Fingerprint, IndexOrigin Origin, SyncMarker? Marker);

    private sealed class Slot
    {
        public object Gate { get; } = new();
        public CachedIndex? Loaded { get; set; }
    }
}
