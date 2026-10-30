using System.Text.Json;
using CodeMap.Mcp;
using CodeMap.Query.Config;
using CodeMap.Query.Impact;
using CodeMap.Query.Json;
using CodeMap.Query.Models;

namespace CodeMap.Tests;

/// <summary>
/// MCP M1: the server keeps each project's index in memory and must notice when `codemap sync` replaces it. sync
/// builds the new index next to the old one, swaps the two directories, and the new one carries a fresh syncId in
/// index/sync-complete.json; that id, not a timestamp, is what says "reload". An index built by running `scan` by
/// hand has no marker and falls back to a fingerprint of its files. A reload that fails, or that overlaps the
/// swap, must never replace a good index with a broken or half-old one.
/// </summary>
[TestClass]
public class IndexCacheTests
{
    private static readonly TimeSpan[] NoDelays = { TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero };

    [TestMethod]
    public void Index_is_loaded_once_and_reused_while_the_sync_id_is_unchanged()
    {
        var output = TestPaths.NewTempDir();
        Publish(output, "2026-10-01T00:00:00Z", syncId: "a1", "Cancel");
        var loader = new CountingLoader();
        var cache = new IndexCache(load: loader.Load, sleep: _ => { });

        var first = cache.Get("shop", IndexDir(output));
        var second = cache.Get("shop", IndexDir(output));

        Assert.AreEqual(1, loader.Calls);
        Assert.AreSame(first.Index, second.Index);
        Assert.AreEqual(IndexOrigin.CompleteSync, second.Origin);
        Assert.AreEqual("a1", second.Marker?.SyncId);
    }

    [TestMethod]
    public void New_sync_id_reloads_the_index()
    {
        var output = TestPaths.NewTempDir();
        Publish(output, "2026-10-01T00:00:00Z", syncId: "a1", "Cancel");
        var cache = new IndexCache(sleep: _ => { });
        cache.Get("shop", IndexDir(output));

        Publish(output, "2026-10-02T00:00:00Z", syncId: "b2", "Cancel", "Refund");
        var after = cache.Get("shop", IndexDir(output));

        Assert.AreEqual("b2", after.Marker?.SyncId);
        Assert.IsTrue(after.Index.SymbolsById.ContainsKey(SymbolId("Refund")));
    }

    [TestMethod] // the marker is the contract; file timestamps move for reasons that say nothing about the content
    public void Touched_files_under_an_unchanged_sync_id_do_not_reload()
    {
        var output = TestPaths.NewTempDir();
        Publish(output, "2026-10-01T00:00:00Z", syncId: "a1", "Cancel");
        var loader = new CountingLoader();
        var cache = new IndexCache(load: loader.Load, sleep: _ => { });
        cache.Get("shop", IndexDir(output));

        var symbols = Path.Combine(IndexDir(output), "symbols.jsonl");
        File.SetLastWriteTimeUtc(symbols, File.GetLastWriteTimeUtc(symbols).AddMinutes(1));
        cache.Get("shop", IndexDir(output));

        Assert.AreEqual(1, loader.Calls);
    }

    // `scan --out` writes straight into the synced output: the files change, the marker stays. Trusting the syncId
    // alone would serve the pre-scan index for as long as the server lives.
    [TestMethod]
    public void Scan_run_by_hand_on_top_of_a_synced_index_is_noticed()
    {
        var output = TestPaths.NewTempDir();
        Publish(output, "2026-10-01T00:00:00Z", syncId: "a1", "Cancel");
        var cache = new IndexCache(sleep: _ => { });
        cache.Get("shop", IndexDir(output));

        WriteIndexFiles(IndexDir(output), "2026-10-05T00:00:00Z", "Cancel", "Refund");
        var after = cache.Get("shop", IndexDir(output));

        Assert.AreEqual(IndexOrigin.ChangedAfterSync, after.Origin);
        Assert.IsTrue(after.Index.SymbolsById.ContainsKey(SymbolId("Refund")));
    }

    [TestMethod]
    public void Index_without_a_marker_reloads_when_its_files_change()
    {
        var output = TestPaths.NewTempDir();
        WriteIndexFiles(IndexDir(output), "2026-10-01T00:00:00Z", "Cancel");
        var loader = new CountingLoader();
        var cache = new IndexCache(load: loader.Load, sleep: _ => { });

        var before = cache.Get("shop", IndexDir(output));
        cache.Get("shop", IndexDir(output));
        WriteIndexFiles(IndexDir(output), "2026-10-01T00:00:00Z", "Cancel", "Refund");
        var after = cache.Get("shop", IndexDir(output));

        Assert.AreEqual(IndexOrigin.ScanByHand, before.Origin);
        Assert.IsNull(before.Marker);
        Assert.AreEqual(2, loader.Calls);
        Assert.IsTrue(after.Index.SymbolsById.ContainsKey(SymbolId("Refund")));
    }

    // Between sync's two renames there is no index/ at all, for a few milliseconds. A call landing there must wait
    // for the new directory, not fail and not serve an empty index (a missing symbols.jsonl reads as zero rows).
    [TestMethod]
    public void Call_during_the_swap_waits_for_the_new_index()
    {
        var output = TestPaths.NewTempDir();
        Publish(output, "2026-10-01T00:00:00Z", syncId: "a1", "Cancel");
        Action onSleep = () => { };
        var cache = new IndexCache(swapRetryDelays: NoDelays, sleep: _ => onSleep());
        cache.Get("shop", IndexDir(output));

        // sync is between its two renames: the old index is parked, the new one not yet moved in.
        var staging = Stage(output, "2026-10-02T00:00:00Z", syncId: "b2", "Cancel", "Refund");
        Directory.Move(IndexDir(output), Path.Combine(output, "index.old-1"));
        var sleeps = 0;
        onSleep = () =>
        {
            if (sleeps++ == 0) Directory.Move(staging, IndexDir(output));
        };

        var during = cache.Get("shop", IndexDir(output));

        Assert.AreEqual(1, sleeps);
        Assert.AreEqual("b2", during.Marker?.SyncId);
        Assert.IsTrue(during.Index.SymbolsById.ContainsKey(SymbolId("Refund")));
    }

    // If sync swaps while a load is reading, the load may have read old symbols and new edges. The marker read
    // before the load no longer matches the one after it; that load is thrown away and done again.
    [TestMethod]
    public void Load_that_overlaps_a_swap_is_redone_instead_of_served()
    {
        var output = TestPaths.NewTempDir();
        Publish(output, "2026-10-01T00:00:00Z", syncId: "a1", "Cancel");
        var loader = new CountingLoader();
        loader.AfterLoad = _ =>
        {
            if (loader.Calls == 1) Publish(output, "2026-10-02T00:00:00Z", syncId: "b2", "Cancel", "Refund");
        };
        var cache = new IndexCache(swapRetryDelays: NoDelays, load: loader.Load, sleep: _ => { });

        var result = cache.Get("shop", IndexDir(output));

        Assert.AreEqual(2, loader.Calls);
        Assert.AreEqual("b2", result.Marker?.SyncId);
        Assert.IsTrue(result.Index.SymbolsById.ContainsKey(SymbolId("Refund")));
    }

    [TestMethod]
    public void Missing_index_with_nothing_loaded_is_INDEX_MISSING_after_retrying()
    {
        var output = TestPaths.NewTempDir();
        var sleeps = 0;
        var cache = new IndexCache(swapRetryDelays: NoDelays, sleep: _ => sleeps++);

        var ex = Assert.ThrowsException<CodeMapToolException>(() => cache.Get("shop", IndexDir(output)));

        Assert.AreEqual(CodeMapToolException.IndexMissing, ex.Code);
        Assert.AreEqual(NoDelays.Length, sleeps);
        StringAssert.Contains(ex.Message, IndexDir(output));
        StringAssert.Contains(ex.Message, "codemap sync --project shop");
    }

    [TestMethod] // e.g. the output folder was deleted while the editor was open: answer from memory, but say so
    public void Index_gone_after_a_load_serves_the_loaded_copy_with_a_problem()
    {
        var output = TestPaths.NewTempDir();
        Publish(output, "2026-10-01T00:00:00Z", syncId: "a1", "Cancel");
        var cache = new IndexCache(swapRetryDelays: NoDelays, sleep: _ => { });
        var loaded = cache.Get("shop", IndexDir(output));

        Directory.Delete(IndexDir(output), recursive: true);
        var after = cache.Get("shop", IndexDir(output));

        Assert.AreSame(loaded.Index, after.Index);
        StringAssert.Contains(after.Problem, "no longer exists");
    }

    [TestMethod]
    public void Failed_reload_keeps_the_previous_index_and_is_not_retried_until_the_index_changes()
    {
        var output = TestPaths.NewTempDir();
        Publish(output, "2026-10-01T00:00:00Z", syncId: "a1", "Cancel");
        var loader = new CountingLoader();
        var cache = new IndexCache(swapRetryDelays: NoDelays, load: loader.Load, sleep: _ => { });
        var good = cache.Get("shop", IndexDir(output));

        // entrypoints.json is parsed whole, so a broken one fails the real ImpactIndex.Load.
        var broken = Stage(output, "2026-10-02T00:00:00Z", syncId: "b2", "Cancel");
        File.WriteAllText(Path.Combine(broken, "entrypoints.json"), "[ { \"id\": ");
        Swap(output, broken);
        var afterFailure = cache.Get("shop", IndexDir(output));
        var again = cache.Get("shop", IndexDir(output));

        Assert.AreSame(good.Index, afterFailure.Index);
        StringAssert.Contains(afterFailure.Problem, "could not load");
        Assert.AreEqual(2, loader.Calls, "the broken index must not be re-parsed on every call");
        Assert.AreSame(good.Index, again.Index);

        Publish(output, "2026-10-03T00:00:00Z", syncId: "c3", "Cancel", "Refund");
        var fixedUp = cache.Get("shop", IndexDir(output));

        Assert.AreEqual("c3", fixedUp.Marker?.SyncId);
        Assert.IsNull(fixedUp.Problem);
    }

    [TestMethod]
    public void Broken_index_with_nothing_loaded_is_INDEX_UNREADABLE()
    {
        var output = TestPaths.NewTempDir();
        Publish(output, "2026-10-01T00:00:00Z", syncId: "a1", "Cancel");
        File.WriteAllText(Path.Combine(IndexDir(output), "entrypoints.json"), "[ { \"id\": ");
        var cache = new IndexCache(swapRetryDelays: NoDelays, sleep: _ => { });

        var ex = Assert.ThrowsException<CodeMapToolException>(() => cache.Get("shop", IndexDir(output)));

        Assert.AreEqual(CodeMapToolException.IndexUnreadable, ex.Code);
        StringAssert.Contains(ex.Message, "codemap sync --project shop");
    }

    [TestMethod]
    public void Least_recently_used_project_is_evicted_beyond_capacity()
    {
        var root = TestPaths.NewTempDir();
        foreach (var name in new[] { "a", "b", "c" })
            Publish(Path.Combine(root, name), "2026-10-01T00:00:00Z", syncId: name, "Cancel");
        var loader = new CountingLoader();
        var cache = new IndexCache(capacity: 2, load: loader.Load, sleep: _ => { });

        cache.Get("a", IndexDir(Path.Combine(root, "a")));
        cache.Get("b", IndexDir(Path.Combine(root, "b")));
        cache.Get("a", IndexDir(Path.Combine(root, "a")));
        cache.Get("c", IndexDir(Path.Combine(root, "c"))); // evicts b, the one used longest ago
        Assert.AreEqual(3, loader.Calls);

        cache.Get("a", IndexDir(Path.Combine(root, "a")));
        Assert.AreEqual(3, loader.Calls);
        cache.Get("b", IndexDir(Path.Combine(root, "b")));
        Assert.AreEqual(4, loader.Calls);
    }

    [TestMethod] // the registry was edited to point the project's output somewhere else
    public void Project_whose_index_directory_moved_is_loaded_from_the_new_place()
    {
        var root = TestPaths.NewTempDir();
        Publish(Path.Combine(root, "old"), "2026-10-01T00:00:00Z", syncId: "same", "Cancel");
        Publish(Path.Combine(root, "new"), "2026-10-01T00:00:00Z", syncId: "same", "Cancel", "Refund");
        var cache = new IndexCache(sleep: _ => { });
        cache.Get("shop", IndexDir(Path.Combine(root, "old")));

        var moved = cache.Get("SHOP", IndexDir(Path.Combine(root, "new")));

        Assert.IsTrue(moved.Index.SymbolsById.ContainsKey(SymbolId("Refund")));
    }

    private static string IndexDir(string output) => Path.Combine(output, "index");

    private static string SymbolId(string method) => $"M:App.Svc.{method}";

    /// <summary>What a completed `codemap sync` leaves: the new index built in staging, swapped in, marker inside.</summary>
    private static void Publish(string output, string indexedAt, string syncId, params string[] methods)
        => Swap(output, Stage(output, indexedAt, syncId, methods));

    private static string Stage(string output, string indexedAt, string syncId, params string[] methods)
    {
        var staging = Path.Combine(output, $"staging-{Guid.NewGuid():N}", "index");
        WriteIndexFiles(staging, indexedAt, methods);
        SyncMarker.Write(staging, new SyncMarker
        {
            SyncId = syncId, CompletedAt = indexedAt, Project = "shop", MetaIndexedAt = indexedAt,
        });
        return staging;
    }

    /// <summary>SyncWorkspace.PublishIndex's two renames.</summary>
    private static void Swap(string output, string stagingIndex)
    {
        var index = IndexDir(output);
        var parked = Path.Combine(output, $"index.old-{Guid.NewGuid():N}");
        if (Directory.Exists(index)) Directory.Move(index, parked);
        Directory.Move(stagingIndex, index);
        if (Directory.Exists(parked)) Directory.Delete(parked, recursive: true);
    }

    /// <summary>What `scan` writes, in place, with no marker. Bumps the timestamps so a rewrite within one clock tick still looks changed.</summary>
    private static void WriteIndexFiles(string indexDir, string indexedAt, params string[] methods)
    {
        Directory.CreateDirectory(indexDir);
        var symbols = Path.Combine(indexDir, "symbols.jsonl");
        var previous = File.Exists(symbols) ? File.GetLastWriteTimeUtc(symbols) : DateTime.MinValue;

        JsonlWriter.Write(symbols, methods.Select(m => new SymbolRecord
        {
            Id = SymbolId(m), Kind = "method", Name = m, ContainingType = "App.Svc", Project = "App",
            File = "Svc.cs", Line = 1, Accessibility = "public",
        }));
        JsonlWriter.Write(Path.Combine(indexDir, "edges.jsonl"), Array.Empty<EdgeRecord>());
        File.WriteAllText(Path.Combine(indexDir, "meta.json"), JsonSerializer.Serialize(new
        {
            indexedAt, solutionPath = "App.sln", projectCount = 1, symbolCount = methods.Length, edgeCount = 0,
        }));

        if (previous != DateTime.MinValue && File.GetLastWriteTimeUtc(symbols) <= previous)
            File.SetLastWriteTimeUtc(symbols, previous.AddSeconds(1));
    }

    private sealed class CountingLoader
    {
        public int Calls;

        /// <summary>Runs after the real load has read the files, before the cache sees the result.</summary>
        public Action<string>? AfterLoad;

        public ImpactIndex Load(string indexDir)
        {
            Calls++;
            var index = ImpactIndex.Load(indexDir);
            AfterLoad?.Invoke(indexDir);
            return index;
        }
    }
}
