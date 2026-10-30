using CodeMap.Query.Models;
using CodeMap.Query.Reporting;

namespace CodeMap.Tests;

/// <summary>
/// MCP M1: the staleness banner's facts as data (<see cref="StalenessBanner.Compute"/>), rendered as the one line
/// that opens every MCP tool result. The markdown banner keeps its exact wording (StalenessBannerTests). The
/// committed part of the comparison only changes when HEAD does, so a long-lived server may reuse it; the
/// uncommitted part changes with every save and must be asked of git each time.
/// </summary>
[TestClass]
public class FreshnessLineTests
{
    [TestMethod]
    public void Fresh_index_is_one_short_line()
    {
        var repo = GitFixtureHelper.NewRepo();
        GitFixtureHelper.Commit(repo, "initial", ("a.cs", "class A {}"));
        var head = Head(repo);

        var line = StalenessBanner.RenderLine("shop", StalenessBanner.Compute(MetaAt(head), repo: repo));

        Assert.AreEqual($"codemap · shop · HEAD {head[..7]} · index fresh", line);
    }

    [TestMethod]
    public void Behind_index_gives_commits_relevant_files_and_uncommitted_count()
    {
        var repo = GitFixtureHelper.NewRepo();
        GitFixtureHelper.Commit(repo, "initial", ("a.cs", "class A {}"), ("b.cs", "class B {}"), ("c.cs", "class C {}"));
        var scannedAt = Head(repo);
        GitFixtureHelper.Commit(repo, "touch b and c", ("b.cs", "class B2 {}"), ("c.cs", "class C2 {}"));
        File.WriteAllText(Path.Combine(repo, "a.cs"), "class A2 {}");
        File.WriteAllText(Path.Combine(repo, "c.cs"), "class C3 {}");

        var staleness = StalenessBanner.Compute(MetaAt(scannedAt), new[] { "a.cs", "b.cs", "c.cs" }, repo);

        Assert.AreEqual(StalenessState.Behind, staleness.State);
        Assert.AreEqual(1, staleness.CommitsBehind);
        Assert.AreEqual(3, staleness.ChangedFiles);
        Assert.AreEqual(2, staleness.UncommittedFiles);
        Assert.IsFalse(staleness.IsFresh);
        Assert.AreEqual(
            $"codemap · shop · HEAD {Head(repo)[..7]} · 1 commit(s) behind, 3 relevant file(s) changed (2 uncommitted)",
            StalenessBanner.RenderLine("shop", staleness));
    }

    [TestMethod]
    public void Without_a_relevant_set_the_line_counts_every_changed_file()
    {
        var repo = GitFixtureHelper.NewRepo();
        GitFixtureHelper.Commit(repo, "initial", ("a.cs", "class A {}"));
        var scannedAt = Head(repo);
        GitFixtureHelper.Commit(repo, "second", ("b.cs", "class B {}"));

        var line = StalenessBanner.RenderLine("shop", StalenessBanner.Compute(MetaAt(scannedAt), repo: repo));

        StringAssert.EndsWith(line, "· 1 commit(s) behind, 1 file(s) changed");
    }

    [TestMethod] // HEAD equal to the scanned commit is not "fresh" when a file the answer depends on was edited
    public void Uncommitted_edit_at_the_scanned_commit_is_not_fresh()
    {
        var repo = GitFixtureHelper.NewRepo();
        GitFixtureHelper.Commit(repo, "initial", ("a.cs", "class A {}"));
        File.WriteAllText(Path.Combine(repo, "a.cs"), "class A2 {}");

        var staleness = StalenessBanner.Compute(MetaAt(Head(repo)), new[] { "a.cs" }, repo);

        Assert.AreEqual(StalenessState.AtScannedCommit, staleness.State);
        Assert.IsFalse(staleness.IsFresh);
        StringAssert.EndsWith(StalenessBanner.RenderLine("shop", staleness), "· at the scanned commit, 1 relevant file(s) changed (1 uncommitted)");
    }

    [TestMethod]
    public void Unknown_staleness_says_why_in_the_line()
    {
        Assert.AreEqual("codemap · shop · staleness unknown (no meta.json)", StalenessBanner.RenderLine("shop", StalenessBanner.Compute(null)));

        var noCommit = new MetaModel { IndexedAt = "x", SolutionPath = "x.sln", ProjectCount = 1, SymbolCount = 0, EdgeCount = 0 };
        StringAssert.Contains(StalenessBanner.RenderLine("shop", StalenessBanner.Compute(noCommit)), "staleness unknown (not a git repo at scan time)");

        var notARepo = TestPaths.NewTempDir();
        StringAssert.Contains(
            StalenessBanner.RenderLine("shop", StalenessBanner.Compute(MetaAt("0123456789abcdef"), repo: notARepo)),
            $"staleness unknown (could not run git in {notARepo})");
    }

    [TestMethod]
    public void Committed_changes_are_cached_per_scanned_commit_and_head()
    {
        var repo = GitFixtureHelper.NewRepo();
        GitFixtureHelper.Commit(repo, "initial", ("a.cs", "class A {}"));
        var scannedAt = Head(repo);
        GitFixtureHelper.Commit(repo, "second", ("b.cs", "class B {}"));
        var cache = new CommittedChangesCache();

        StalenessBanner.Compute(MetaAt(scannedAt), repo: repo, cache: cache);
        StalenessBanner.Compute(MetaAt(scannedAt), repo: repo, cache: cache);
        Assert.AreEqual(1, cache.Count);

        GitFixtureHelper.Commit(repo, "third", ("c.cs", "class C {}"));
        var after = StalenessBanner.Compute(MetaAt(scannedAt), repo: repo, cache: cache);

        Assert.AreEqual(2, cache.Count);
        Assert.AreEqual(2, after.CommitsBehind);
        Assert.AreEqual(2, after.ChangedFiles);
    }

    [TestMethod] // caching the whole answer would keep saying "fresh" after the agent's own edit
    public void Uncommitted_changes_are_never_served_from_the_cache()
    {
        var repo = GitFixtureHelper.NewRepo();
        GitFixtureHelper.Commit(repo, "initial", ("a.cs", "class A {}"), ("b.cs", "class B {}"));
        var scannedAt = Head(repo);
        GitFixtureHelper.Commit(repo, "second", ("b.cs", "class B2 {}"));
        var cache = new CommittedChangesCache();

        var before = StalenessBanner.Compute(MetaAt(scannedAt), new[] { "a.cs", "b.cs" }, repo, cache);
        File.WriteAllText(Path.Combine(repo, "a.cs"), "class A2 {}");
        var after = StalenessBanner.Compute(MetaAt(scannedAt), new[] { "a.cs", "b.cs" }, repo, cache);

        Assert.AreEqual(0, before.UncommittedFiles);
        Assert.AreEqual(1, after.UncommittedFiles);
        Assert.AreEqual(2, after.ChangedFiles);
        Assert.AreEqual(1, cache.Count);
    }

    private static MetaModel MetaAt(string commit) => new()
    {
        IndexedAt = "x", GitCommit = commit, SolutionPath = "x.sln", ProjectCount = 1, SymbolCount = 0, EdgeCount = 0,
    };

    private static string Head(string repo) => CodeMap.Query.Git.GitRepoInfo.TryGetHeadCommit(repo)!;
}
