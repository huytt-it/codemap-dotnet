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
