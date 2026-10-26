using CodeMap.Query.Models;
using CodeMap.Query.Reporting;

namespace CodeMap.Tests;

/// <summary>Spec section 7.5: staleness banner, verified against a real (temp) git repo — same GitFixtureHelper as Phase 2.5's scan-git tests.</summary>
[TestClass]
public class StalenessBannerTests
{
    [TestMethod]
    public void No_meta_json_reports_unknown_staleness()
    {
        var banner = StalenessBanner.Render(meta: null);
        StringAssert.Contains(banner, "no meta.json found");
    }

    [TestMethod]
    public void Meta_without_a_git_commit_reports_unknown_staleness()
    {
        var meta = new MetaModel { IndexedAt = "2026-01-01T00:00:00Z", SolutionPath = "x.sln", ProjectCount = 1, SymbolCount = 0, EdgeCount = 0 };
        var banner = StalenessBanner.Render(meta);
        StringAssert.Contains(banner, "wasn't a git repo");
    }

    [TestMethod]
    public void Head_matching_the_scanned_commit_reports_fresh()
    {
        var repo = GitFixtureHelper.NewRepo();
        GitFixtureHelper.Commit(repo, "initial", ("a.cs", "class A {}"));
        var head = RunGit(repo, "rev-parse", "HEAD").Trim();

        var meta = new MetaModel { IndexedAt = "x", GitCommit = head, SolutionPath = "x.sln", ProjectCount = 1, SymbolCount = 0, EdgeCount = 0 };
        var banner = StalenessBanner.Render(meta, cwdOverride: repo);

        StringAssert.Contains(banner, "matches the scan, index is fresh");
    }

    [TestMethod]
    public void Commits_since_the_scan_are_reported_with_a_count()
    {
        var repo = GitFixtureHelper.NewRepo();
        GitFixtureHelper.Commit(repo, "initial", ("a.cs", "class A {}"));
        var scannedAt = RunGit(repo, "rev-parse", "HEAD").Trim();

        GitFixtureHelper.Commit(repo, "second", ("b.cs", "class B {}"));
        GitFixtureHelper.Commit(repo, "third", ("c.cs", "class C {}"));

        var meta = new MetaModel { IndexedAt = "x", GitCommit = scannedAt, SolutionPath = "x.sln", ProjectCount = 1, SymbolCount = 0, EdgeCount = 0 };
        var banner = StalenessBanner.Render(meta, cwdOverride: repo);

        StringAssert.Contains(banner, "2 commit(s) behind");
    }

    [TestMethod]
    public void Relevant_file_filter_only_counts_files_in_the_given_set()
    {
        var repo = GitFixtureHelper.NewRepo();
        GitFixtureHelper.Commit(repo, "initial", ("a.cs", "class A {}"), ("b.cs", "class B {}"));
        var scannedAt = RunGit(repo, "rev-parse", "HEAD").Trim();

        GitFixtureHelper.Commit(repo, "touch both", ("a.cs", "class A2 {}"), ("b.cs", "class B2 {}"));

        var meta = new MetaModel { IndexedAt = "x", GitCommit = scannedAt, SolutionPath = "x.sln", ProjectCount = 1, SymbolCount = 0, EdgeCount = 0 };
        var banner = StalenessBanner.Render(meta, relevantFiles: new[] { "a.cs" }, cwdOverride: repo);

        StringAssert.Contains(banner, "1 relevant file(s) changed since the scan");
    }

    [TestMethod]
    public void Every_banner_includes_the_scope_disclaimer()
    {
        var banner = StalenessBanner.Render(meta: null);
        StringAssert.Contains(banner, "Scope of this file");
        StringAssert.Contains(banner, "NOT COVERED");
    }

    // git reports paths from the repo root; symbols.jsonl records them from the solution's directory (GitPathRebaser).
    // The banner has to compare the two in one space, or a nested solution never sees a relevant file change.
    [TestMethod]
    public void Relevant_file_in_a_nested_solution_is_matched_against_git_paths()
    {
        var repo = GitFixtureHelper.NewRepo();
        GitFixtureHelper.Commit(repo, "initial", ("src/Orders/Svc.cs", "class Svc {}"));
        var scannedAt = Head(repo);
        GitFixtureHelper.Commit(repo, "touch svc", ("src/Orders/Svc.cs", "class Svc2 {}"));

        var banner = StalenessBanner.Render(MetaAt(scannedAt, "src/App.sln"), relevantFiles: new[] { "Orders/Svc.cs" }, cwdOverride: repo);

        StringAssert.Contains(banner, "1 relevant file(s) changed since the scan");
    }

    [TestMethod] // the rebased "../db/..." form of a file outside the solution must not be mistaken for a relevant one
    public void Change_outside_the_nested_solution_is_not_counted_as_relevant()
    {
        var repo = GitFixtureHelper.NewRepo();
        GitFixtureHelper.Commit(repo, "initial", ("src/Orders/Svc.cs", "class Svc {}"), ("db/cancel.sql", "-- v1"));
        var scannedAt = Head(repo);
        GitFixtureHelper.Commit(repo, "touch sql only", ("db/cancel.sql", "-- v2"));

        var banner = StalenessBanner.Render(MetaAt(scannedAt, "src/App.sln"), relevantFiles: new[] { "Orders/Svc.cs" }, cwdOverride: repo);

        StringAssert.Contains(banner, "0 relevant file(s) changed since the scan");
    }

    [TestMethod]
    public void Non_ascii_file_name_is_matched_rather_than_escaped_by_git()
    {
        var repo = GitFixtureHelper.NewRepo();
        GitFixtureHelper.Commit(repo, "initial", ("注文.cs", "class A {}"));
        var scannedAt = Head(repo);
        GitFixtureHelper.Commit(repo, "touch", ("注文.cs", "class B {}"));

        var banner = StalenessBanner.Render(MetaAt(scannedAt), relevantFiles: new[] { "注文.cs" }, cwdOverride: repo);

        StringAssert.Contains(banner, "1 relevant file(s) changed since the scan");
    }

    // The agent edits a file and asks about it before committing: HEAD still equals the scanned commit, but the
    // index no longer describes that file.
    [TestMethod]
    public void Uncommitted_edit_to_a_relevant_file_is_not_reported_as_fresh()
    {
        var repo = GitFixtureHelper.NewRepo();
        GitFixtureHelper.Commit(repo, "initial", ("a.cs", "class A {}"), ("b.cs", "class B {}"));
        File.WriteAllText(Path.Combine(repo, "a.cs"), "class A2 {}");

        var banner = StalenessBanner.Render(MetaAt(Head(repo)), relevantFiles: new[] { "a.cs" }, cwdOverride: repo);

        StringAssert.Contains(banner, "matches the scan, but 1 relevant file(s) have uncommitted changes");
        Assert.IsFalse(banner.Contains("index is fresh"), banner);
    }

    [TestMethod] // only the files the answer depends on decide freshness; unrelated work in progress is not noise
    public void Uncommitted_edit_to_an_unrelated_file_leaves_the_index_fresh()
    {
        var repo = GitFixtureHelper.NewRepo();
        GitFixtureHelper.Commit(repo, "initial", ("a.cs", "class A {}"), ("b.cs", "class B {}"));
        File.WriteAllText(Path.Combine(repo, "b.cs"), "class B2 {}");

        var banner = StalenessBanner.Render(MetaAt(Head(repo)), relevantFiles: new[] { "a.cs" }, cwdOverride: repo);

        StringAssert.Contains(banner, "matches the scan, index is fresh");
    }

    [TestMethod] // map has no relevant set: any dirty tracked file is worth a mention
    public void Uncommitted_edit_is_counted_when_there_is_no_relevant_file_set()
    {
        var repo = GitFixtureHelper.NewRepo();
        GitFixtureHelper.Commit(repo, "initial", ("a.cs", "class A {}"));
        File.WriteAllText(Path.Combine(repo, "a.cs"), "class A2 {}");

        var banner = StalenessBanner.Render(MetaAt(Head(repo)), cwdOverride: repo);

        StringAssert.Contains(banner, "matches the scan, but 1 file(s) have uncommitted changes");
    }

    [TestMethod]
    public void Staged_but_uncommitted_edit_counts_as_uncommitted()
    {
        var repo = GitFixtureHelper.NewRepo();
        GitFixtureHelper.Commit(repo, "initial", ("a.cs", "class A {}"));
        File.WriteAllText(Path.Combine(repo, "a.cs"), "class A2 {}");
        RunGit(repo, "add", "a.cs");

        var banner = StalenessBanner.Render(MetaAt(Head(repo)), relevantFiles: new[] { "a.cs" }, cwdOverride: repo);

        StringAssert.Contains(banner, "1 relevant file(s) have uncommitted changes");
    }

    [TestMethod]
    public void Uncommitted_edit_in_a_nested_solution_is_matched_against_git_paths()
    {
        var repo = GitFixtureHelper.NewRepo();
        GitFixtureHelper.Commit(repo, "initial", ("src/Orders/Svc.cs", "class Svc {}"));
        File.WriteAllText(Path.Combine(repo, "src", "Orders", "Svc.cs"), "class Svc2 {}");

        var banner = StalenessBanner.Render(MetaAt(Head(repo), "src/App.sln"), relevantFiles: new[] { "Orders/Svc.cs" }, cwdOverride: repo);

        StringAssert.Contains(banner, "1 relevant file(s) have uncommitted changes");
    }

    [TestMethod] // committed and uncommitted changes add up, and a file in both is counted once
    public void Uncommitted_edits_are_added_to_the_committed_count_when_head_is_behind()
    {
        var repo = GitFixtureHelper.NewRepo();
        GitFixtureHelper.Commit(repo, "initial", ("a.cs", "class A {}"), ("b.cs", "class B {}"), ("c.cs", "class C {}"));
        var scannedAt = Head(repo);
        GitFixtureHelper.Commit(repo, "touch b and c", ("b.cs", "class B2 {}"), ("c.cs", "class C2 {}"));
        File.WriteAllText(Path.Combine(repo, "a.cs"), "class A2 {}");
        File.WriteAllText(Path.Combine(repo, "c.cs"), "class C3 {}");

        var banner = StalenessBanner.Render(MetaAt(scannedAt), relevantFiles: new[] { "a.cs", "b.cs", "c.cs" }, cwdOverride: repo);

        StringAssert.Contains(banner, "1 commit(s) behind, 3 relevant file(s) changed since the scan (2 uncommitted)");
    }

    private static MetaModel MetaAt(string commit, string solutionPath = "x.sln") => new()
    {
        IndexedAt = "x", GitCommit = commit, SolutionPath = solutionPath, ProjectCount = 1, SymbolCount = 0, EdgeCount = 0,
    };

    private static string Head(string repo) => RunGit(repo, "rev-parse", "HEAD").Trim();

    private static string RunGit(string dir, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = System.Diagnostics.Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }
}
