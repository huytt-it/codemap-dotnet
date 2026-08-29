using CodeMap.Query.Git;
using CodeMap.Query.Json;
using CodeMap.Query.Models;

namespace CodeMap.Tests;

/// <summary>Phase 2.5 (spec section 5, "Thu thập dữ liệu git"): ticket-files.jsonl / co-change.jsonl correctness, noise filtering, and the ticket-pattern guard.</summary>
[TestClass]
public class ScanGitTests
{
    [TestMethod]
    public void Extracts_ticket_from_default_pattern_and_merges_multiple_commits()
    {
        var repo = GitFixtureHelper.NewRepo();
        GitFixtureHelper.Commit(repo, "fix #1234 order cancel bug", ("a.cs", "class A {}"));
        GitFixtureHelper.Commit(repo, "fix #1234 followup", ("b.cs", "class B {}"));

        var outDir = RunScanGit(repo);
        var tickets = ReadTickets(outDir);

        var ticket = tickets.Single(t => t.Ticket == "1234");
        Assert.AreEqual(2, ticket.Commits.Count);
        CollectionAssert.AreEquivalent(new[] { "a.cs", "b.cs" }, ticket.Files);
    }

    [TestMethod]
    public void Commit_without_ticket_id_is_excluded()
    {
        var repo = GitFixtureHelper.NewRepo();
        GitFixtureHelper.Commit(repo, "docs update, no ticket here", ("README.md", "hello"));
        GitFixtureHelper.Commit(repo, "fix #1234 real ticket", ("a.cs", "class A {}"));

        var outDir = RunScanGit(repo);
        var tickets = ReadTickets(outDir);

        Assert.AreEqual(1, tickets.Count);
        Assert.AreEqual("1234", tickets[0].Ticket);
    }

    [TestMethod]
    public void Commit_with_only_disallowed_extensions_contributes_nothing()
    {
        var repo = GitFixtureHelper.NewRepo();
        // .md is not in the allowed extension list (.cs .ts .js .html .sql .json .config) — this commit's only
        // file gets filtered out entirely, so ticket 9999 must not appear at all.
        GitFixtureHelper.Commit(repo, "fix #9999 readme only", ("notes.md", "notes"));
        GitFixtureHelper.Commit(repo, "fix #1234 real change", ("a.cs", "class A {}"));

        var outDir = RunScanGit(repo);
        var tickets = ReadTickets(outDir);

        Assert.AreEqual(1, tickets.Count);
        Assert.AreEqual("1234", tickets[0].Ticket);
    }

    [TestMethod] // spec: "Bỏ commit đụng hơn 50 file (merge, rename hàng loạt, format toàn repo)"
    public void Bulk_commit_over_50_files_is_excluded_from_ticket_files()
    {
        var repo = GitFixtureHelper.NewRepo();
        GitFixtureHelper.CommitManyFiles(repo, "fix #1234 mass reformat", 60);

        var outDir = RunScanGit(repo);
        // The commit message matches the ticket pattern (so the probe check passes, exit code 0), but the noise
        // filter drops it before extraction -> ticket-files.jsonl exists but is empty.
        var path = Path.Combine(outDir, "index", "ticket-files.jsonl");
        Assert.IsTrue(File.Exists(path));
        Assert.AreEqual(0, JsonlReader.Read<TicketFileRecord>(path).Count);
    }

    [TestMethod] // spec: "Bỏ cặp co-change có together < 3"
    public void CoChange_pair_below_minimum_together_is_excluded()
    {
        var repo = GitFixtureHelper.NewRepo();
        for (var i = 0; i < 2; i++)
            GitFixtureHelper.Commit(repo, $"fix #1000 pass {i}", ("a.cs", $"v{i}"), ("b.cs", $"v{i}"));

        var outDir = RunScanGit(repo);
        var coChanges = ReadCoChanges(outDir);

        Assert.AreEqual(0, coChanges.Count);
    }

    [TestMethod]
    public void CoChange_pair_at_minimum_together_has_correct_strength()
    {
        var repo = GitFixtureHelper.NewRepo();
        for (var i = 0; i < 3; i++)
            GitFixtureHelper.Commit(repo, $"fix #1000 pass {i}", ("a.cs", $"v{i}"), ("b.cs", $"v{i}"));
        GitFixtureHelper.Commit(repo, "fix #1000 a only", ("a.cs", "v-final"));

        var outDir = RunScanGit(repo);
        var pair = ReadCoChanges(outDir).Single(c => c.FileA == "a.cs" && c.FileB == "b.cs");

        Assert.AreEqual(3, pair.Together);
        Assert.AreEqual(4, pair.TotalA);
        Assert.AreEqual(3, pair.TotalB);
        Assert.AreEqual(1.0, pair.Strength); // 3 / min(4, 3)
    }

    [TestMethod] // regression: found in Phase 5 — .NET on Windows defaults to the console/OEM codepage for redirected process stdout unless told otherwise, mangling non-ASCII commit messages
    public void Non_ascii_commit_message_round_trips_correctly()
    {
        var repo = GitFixtureHelper.NewRepo();
        GitFixtureHelper.Commit(repo, "fix #4821 hủy đơn hàng khi khách đã thanh toán", ("a.cs", "class A {}"));

        var outDir = RunScanGit(repo);
        var ticket = ReadTickets(outDir).Single(t => t.Ticket == "4821");

        Assert.AreEqual("fix #4821 hủy đơn hàng khi khách đã thanh toán", ticket.Message);
    }

    [TestMethod]
    public void Custom_ticket_pattern_from_config_is_honored()
    {
        var repo = GitFixtureHelper.NewRepo();
        File.WriteAllText(Path.Combine(repo, "codemap.config.json"), """{ "ticketPattern": "WEIRD-(\\d+)" }""");
        GitFixtureHelper.Commit(repo, "WEIRD-42 custom convention commit", ("a.cs", "class A {}"));

        var outDir = RunScanGit(repo);
        var tickets = ReadTickets(outDir);

        Assert.AreEqual(1, tickets.Count);
        Assert.AreEqual("42", tickets[0].Ticket);
    }

    [TestMethod]
    public void No_matching_ticket_in_probe_window_refuses_to_write_output()
    {
        var repo = GitFixtureHelper.NewRepo();
        GitFixtureHelper.Commit(repo, "a commit with no ticket convention at all", ("a.cs", "class A {}"));

        var outRoot = TestPaths.NewTempDir();
        var exitCode = ScanGitCommand.Run(new[] { "--repo", repo, "--out", outRoot });

        Assert.AreEqual(1, exitCode);
        Assert.IsFalse(File.Exists(Path.Combine(outRoot, "index", "ticket-files.jsonl")));
    }

    [TestMethod]
    public void Nonexistent_repo_path_does_not_crash()
    {
        var outRoot = TestPaths.NewTempDir();
        var bogusPath = Path.Combine(TestPaths.NewTempDir(), "does-not-exist");

        var ex = TestAssert.RecordException(() => ScanGitCommand.Run(new[] { "--repo", bogusPath, "--out", outRoot }));

        Assert.IsNull(ex);
    }

    [TestMethod]
    public void Non_git_directory_does_not_crash()
    {
        var plainDir = TestPaths.NewTempDir(); // exists, but `git init` was never run here
        var outRoot = TestPaths.NewTempDir();

        var ex = TestAssert.RecordException(() => ScanGitCommand.Run(new[] { "--repo", plainDir, "--out", outRoot }));

        Assert.IsNull(ex);
    }

    private static string RunScanGit(string repoDir)
    {
        var outDir = TestPaths.NewTempDir();
        var exitCode = ScanGitCommand.Run(new[] { "--repo", repoDir, "--out", outDir });
        Assert.AreEqual(0, exitCode);
        return outDir;
    }

    private static List<TicketFileRecord> ReadTickets(string outDir)
        => JsonlReader.Read<TicketFileRecord>(Path.Combine(outDir, "index", "ticket-files.jsonl"));

    private static List<CoChangeRecord> ReadCoChanges(string outDir)
        => JsonlReader.Read<CoChangeRecord>(Path.Combine(outDir, "index", "co-change.jsonl"));
}
