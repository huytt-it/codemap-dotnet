using CodeMap.Query.Config;
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
        // .md is not in the allowed extension list — this commit's only file gets filtered out entirely, so
        // ticket 9999 must not appear at all.
        GitFixtureHelper.Commit(repo, "fix #9999 readme only", ("notes.md", "notes"));
        GitFixtureHelper.Commit(repo, "fix #1234 real change", ("a.cs", "class A {}"));

        var outDir = RunScanGit(repo);
        var tickets = ReadTickets(outDir);

        Assert.AreEqual(1, tickets.Count);
        Assert.AreEqual("1234", tickets[0].Ticket);
    }

    [TestMethod] // spec: drop repo-wide reformatting. Merges and mass renames, the other two cases the spec named, are now handled structurally in GitLogRunner.
    public void Bulk_commit_over_the_noise_threshold_is_excluded_from_ticket_files()
    {
        var repo = GitFixtureHelper.NewRepo();
        GitFixtureHelper.CommitManyFiles(repo, "fix #1234 mass reformat", 120);

        var outDir = RunScanGit(repo);
        // The commit message matches the ticket pattern (so the probe check passes, exit code 0), but the noise
        // filter drops it before extraction -> ticket-files.jsonl exists but is empty.
        var path = Path.Combine(outDir, "index", "ticket-files.jsonl");
        Assert.IsTrue(File.Exists(path));
        Assert.AreEqual(0, JsonlReader.Read<TicketFileRecord>(path).Count);
    }

    [TestMethod] // 50 was cutting off ordinary large pull requests (eShopOnWeb: p95 = 47 files, p98 = 69)
    public void Large_but_realistic_commit_below_the_threshold_is_kept()
    {
        var repo = GitFixtureHelper.NewRepo();
        GitFixtureHelper.CommitManyFiles(repo, "fix #1234 big but legitimate feature", 60);

        var tickets = ReadTickets(RunScanGit(repo));

        Assert.AreEqual(60, tickets.Single(t => t.Ticket == "1234").Files.Count);
    }

    [TestMethod] // a plain `git log --name-only` emits no file list for a merge commit, so the whole unit of work vanished
    public void Merge_commit_contributes_the_files_merged_by_it()
    {
        var repo = GitFixtureHelper.NewRepo();
        GitFixtureHelper.Commit(repo, "fix #1000 initial", ("a.cs", "class A {}"));
        GitFixtureHelper.CommitOnBranchAndMerge(
            repo, "feature", "wip, no ticket here", "Merge pull request #9 from org/SHO_1234-fix-cancel",
            ("Orders/Cancel.cs", "class Cancel {}"));

        var tickets = ReadTickets(RunScanGitWithPattern(repo, @"([A-Z][A-Z0-9]*_\d+)"));

        // The ticket ID exists only in the merge message (via the branch name); the branch commit says "wip".
        CollectionAssert.AreEquivalent(new[] { "Orders/Cancel.cs" }, tickets.Single(t => t.Ticket == "SHO_1234").Files);
    }

    [TestMethod] // a path that no longer exists can never join to symbols.jsonl, which only holds files that do
    public void History_before_a_rename_is_reported_under_the_current_file_name()
    {
        var repo = GitFixtureHelper.NewRepo();
        GitFixtureHelper.Commit(repo, "fix #1234 work under the old name", ("Old.cs", "class X {}"));
        GitFixtureHelper.Rename(repo, "Old.cs", "New.cs", "fix #1234 rename it");

        var ticket = ReadTickets(RunScanGit(repo)).Single(t => t.Ticket == "1234");

        CollectionAssert.AreEquivalent(new[] { "New.cs" }, ticket.Files);
    }

    [TestMethod] // renames chain: history recorded under the first name must survive every later rename
    public void Two_successive_renames_both_resolve_to_the_final_name()
    {
        var repo = GitFixtureHelper.NewRepo();
        GitFixtureHelper.Commit(repo, "fix #1234 original", ("First.cs", "class X {}"));
        GitFixtureHelper.Rename(repo, "First.cs", "Second.cs", "fix #1234 rename once");
        GitFixtureHelper.Rename(repo, "Second.cs", "Third.cs", "fix #1234 rename twice");

        var ticket = ReadTickets(RunScanGit(repo)).Single(t => t.Ticket == "1234");

        CollectionAssert.AreEquivalent(new[] { "Third.cs" }, ticket.Files);
    }

    [TestMethod] // Razor is the dominant UI style in the codebases this tool targets; its edit history was being dropped entirely
    public void Razor_and_cshtml_files_are_part_of_a_ticket()
    {
        var repo = GitFixtureHelper.NewRepo();
        GitFixtureHelper.Commit(repo, "fix #1234 basket screen",
            ("Pages/Basket.cshtml", "@page"), ("Components/Cart.razor", "@code {}"), ("BasketService.cs", "class B {}"));

        var ticket = ReadTickets(RunScanGit(repo)).Single(t => t.Ticket == "1234");

        CollectionAssert.AreEquivalent(
            new[] { "Pages/Basket.cshtml", "Components/Cart.razor", "BasketService.cs" }, ticket.Files);
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

    [TestMethod] // Review Fix Pass v1, Task 4: broader coverage than a single message — 5 commits, spread across all 6 Vietnamese tone marks and the đ/ơ/ư/ă/â/ê letters, each in its own ticket so a partial-mojibake bug affecting only some byte sequences wouldn't slip through
    public void Five_vietnamese_commit_messages_with_diverse_diacritics_all_round_trip_correctly()
    {
        var repo = GitFixtureHelper.NewRepo();
        var messages = new[]
        {
            "fix #201 hủy đơn hàng khi khách đã thanh toán",
            "fix #202 sửa lỗi hiển thị giỏ hàng trống",
            "fix #203 cập nhật số lượng tồn kho không chính xác",
            "fix #204 thêm chức năng xuất hóa đơn PDF",
            "fix #205 khắc phục lỗi đăng nhập bằng tài khoản Google",
        };
        for (var i = 0; i < messages.Length; i++)
            GitFixtureHelper.Commit(repo, messages[i], ($"File{i}.cs", "class X {}"));

        var outDir = RunScanGit(repo);
        var tickets = ReadTickets(outDir);

        Assert.AreEqual(5, tickets.Count);
        foreach (var msg in messages)
        {
            var ticketId = msg.Split(' ')[1].TrimStart('#');
            var ticket = tickets.Single(t => t.Ticket == ticketId);
            Assert.AreEqual(msg, ticket.Message, $"mojibake or truncation for ticket #{ticketId}");
        }
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

    [TestMethod] // scan looks for the config from the solution's directory, scan-git from the repo root; one file at the root must serve both
    public void Config_at_repo_root_is_found_from_a_nested_solution_directory()
    {
        var repo = GitFixtureHelper.NewRepo();
        File.WriteAllText(Path.Combine(repo, "codemap.config.json"), """{ "ticketPattern": "WEIRD-(\\d+)" }""");
        var nested = Path.Combine(repo, "src", "Backend");
        Directory.CreateDirectory(nested);

        var config = CodeMapConfig.Load(nested);

        Assert.AreEqual(@"WEIRD-(\d+)", config.EffectiveTicketPattern);
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

    [TestMethod] // git reports paths from the repo root; the Roslyn scan records them from the solution's directory
    public void Solution_nested_in_repo_gets_git_paths_rebased_onto_the_solution_directory()
    {
        var repo = GitFixtureHelper.NewRepo();
        GitFixtureHelper.Commit(repo, "fix #1234 order cancel", ("src/Orders/Svc.cs", "class Svc {}"));

        var outDir = RunScanGitWithMeta(repo, solutionPath: "src/App.sln");

        var ticket = ReadTickets(outDir).Single(t => t.Ticket == "1234");
        CollectionAssert.AreEquivalent(new[] { "Orders/Svc.cs" }, ticket.Files);
    }

    [TestMethod] // a stored proc or shared config outside the solution is exactly what co-change exists to catch — keep it, don't drop it
    public void File_outside_the_solution_directory_is_kept_as_a_relative_path()
    {
        var repo = GitFixtureHelper.NewRepo();
        GitFixtureHelper.Commit(repo, "fix #1234 proc and caller",
            ("src/Orders/Svc.cs", "class Svc {}"), ("db/procs/cancel.sql", "-- sql"));

        var outDir = RunScanGitWithMeta(repo, solutionPath: "src/App.sln");

        var ticket = ReadTickets(outDir).Single(t => t.Ticket == "1234");
        CollectionAssert.AreEquivalent(new[] { "Orders/Svc.cs", "../db/procs/cancel.sql" }, ticket.Files);
    }

    [TestMethod] // the common layout: solution at the repo root, nothing to rebase
    public void Solution_at_repo_root_leaves_git_paths_untouched()
    {
        var repo = GitFixtureHelper.NewRepo();
        GitFixtureHelper.Commit(repo, "fix #1234 order cancel", ("Orders/Svc.cs", "class Svc {}"));

        var outDir = RunScanGitWithMeta(repo, solutionPath: "App.sln");

        var ticket = ReadTickets(outDir).Single(t => t.Ticket == "1234");
        CollectionAssert.AreEquivalent(new[] { "Orders/Svc.cs" }, ticket.Files);
    }

    [TestMethod] // scan-git run before scan, or standalone: no meta.json to rebase against, so behave as before
    public void Without_meta_json_paths_stay_repo_root_relative()
    {
        var repo = GitFixtureHelper.NewRepo();
        GitFixtureHelper.Commit(repo, "fix #1234 order cancel", ("src/Orders/Svc.cs", "class Svc {}"));

        var outDir = RunScanGit(repo);

        var ticket = ReadTickets(outDir).Single(t => t.Ticket == "1234");
        CollectionAssert.AreEquivalent(new[] { "src/Orders/Svc.cs" }, ticket.Files);
    }

    [TestMethod] // the join is silent when it fails, so scan-git has to say so itself
    public void Warns_when_no_ticket_file_matches_any_scanned_symbol()
    {
        var repo = GitFixtureHelper.NewRepo();
        GitFixtureHelper.Commit(repo, "fix #1234 order cancel", ("Orders/Svc.cs", "class Svc {}"));

        var outDir = TestPaths.NewTempDir();
        var indexDir = Path.Combine(outDir, "index");
        Directory.CreateDirectory(indexDir);
        JsonlWriter.Write(Path.Combine(indexDir, "symbols.jsonl"), new[]
        {
            new SymbolRecord
            {
                Id = "M:Other.Thing.Do", Kind = "method", Name = "Do", Project = "P",
                File = "somewhere/else/Thing.cs", Line = 1, Accessibility = "public",
            },
        });

        var stderr = new StringWriter();
        var previous = Console.Error;
        try
        {
            Console.SetError(stderr);
            Assert.AreEqual(0, ScanGitCommand.Run(new[] { "--repo", repo, "--out", outDir }));
        }
        finally
        {
            Console.SetError(previous);
        }

        StringAssert.Contains(stderr.ToString(), "none of the 1 ticket(s) touch a file that `codemap scan` indexed");
    }

    /// <summary>scan-git reads the solution's location from an existing meta.json, which `codemap scan` normally wrote first.</summary>
    private static string RunScanGitWithMeta(string repoDir, string solutionPath)
    {
        var outDir = TestPaths.NewTempDir();
        var indexDir = Path.Combine(outDir, "index");
        Directory.CreateDirectory(indexDir);
        JsonUtil.WriteIndented(Path.Combine(indexDir, "meta.json"), new MetaModel
        {
            IndexedAt = "2026-01-01T00:00:00Z",
            SolutionPath = solutionPath,
            ProjectCount = 1,
            SymbolCount = 0,
            EdgeCount = 0,
        });

        Assert.AreEqual(0, ScanGitCommand.Run(new[] { "--repo", repoDir, "--out", outDir }));
        return outDir;
    }

    private static string RunScanGit(string repoDir)
    {
        var outDir = TestPaths.NewTempDir();
        var exitCode = ScanGitCommand.Run(new[] { "--repo", repoDir, "--out", outDir });
        Assert.AreEqual(0, exitCode);
        return outDir;
    }

    private static string RunScanGitWithPattern(string repoDir, string ticketPattern)
    {
        File.WriteAllText(
            Path.Combine(repoDir, "codemap.config.json"),
            System.Text.Json.JsonSerializer.Serialize(new { ticketPattern }));
        return RunScanGit(repoDir);
    }

    private static List<TicketFileRecord> ReadTickets(string outDir)
        => JsonlReader.Read<TicketFileRecord>(Path.Combine(outDir, "index", "ticket-files.jsonl"));

    private static List<CoChangeRecord> ReadCoChanges(string outDir)
        => JsonlReader.Read<CoChangeRecord>(Path.Combine(outDir, "index", "co-change.jsonl"));
}
