using CodeMap.Cli.Cli;
using CodeMap.Query.Config;
using CodeMap.Query.Json;
using CodeMap.Query.Models;

namespace CodeMap.Tests;

/// <summary>
/// `sync` builds the whole pipeline in a staging directory next to Output and swaps the finished index in, so a
/// reader (an MCP server holding the index in RAM) never sees a half-written one and a step that is no longer
/// configured cannot leave its old files behind. Most tests stand in fake steps for the slow ones: what is under
/// test is the orchestration — where the steps write, what is published, what survives a failure.
/// </summary>
[TestClass]
public class SyncCommandTests
{
    private static readonly TimeSpan[] NoRetryDelays = { TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10) };

    [TestMethod]
    public void Steps_write_into_a_staging_directory_next_to_output_while_the_old_index_stays_put()
    {
        var project = NewProject();
        project.SeedOldIndex();
        var outDirs = new List<string>();
        string? oldSymbolsDuringMap = null;

        var steps = Fake() with
        {
            Scan = a => { outDirs.Add(OutOf(a)); return FakeScan(a); },
            Map = a =>
            {
                outDirs.Add(OutOf(a));
                oldSymbolsDuringMap = File.ReadAllText(Path.Combine(project.IndexDir, "symbols.jsonl"));
                return FakeMap(a);
            },
        };

        Assert.IsTrue(Sync(project, steps).Ok);

        Assert.AreEqual(2, outDirs.Count);
        foreach (var dir in outDirs)
        {
            Assert.AreNotEqual(project.OutDir, dir);
            Assert.AreEqual(Path.GetDirectoryName(project.OutDir), Path.GetDirectoryName(dir), "staging must sit next to Output");
            StringAssert.StartsWith(Path.GetFileName(dir), "app.sync-staging-");
        }

        Assert.AreEqual("old", oldSymbolsDuringMap, "the published index must not change until the pipeline has finished");
        Assert.AreEqual("new", File.ReadAllText(Path.Combine(project.IndexDir, "symbols.jsonl")));
        Assert.AreEqual(0, StagingLeftovers(project).Count);
    }

    [TestMethod] // frontend dropped from the registry: its files used to stay in index/ and keep feeding map and impact
    public void Published_index_holds_only_what_this_run_produced()
    {
        var project = NewProject();
        project.SeedOldIndex("frontend-calls.jsonl", "api-links.jsonl", "ticket-files.jsonl");

        Assert.IsTrue(Sync(project, Fake()).Ok); // no frontend configured, not a git repo

        Assert.IsFalse(File.Exists(Path.Combine(project.IndexDir, "frontend-calls.jsonl")));
        Assert.IsFalse(File.Exists(Path.Combine(project.IndexDir, "api-links.jsonl")));
        Assert.IsFalse(File.Exists(Path.Combine(project.IndexDir, "ticket-files.jsonl")));
        Assert.IsTrue(File.Exists(Path.Combine(project.IndexDir, "symbols.jsonl")));
    }

    [TestMethod]
    public void Failed_scan_leaves_the_previous_index_untouched()
    {
        var project = NewProject();
        project.SeedOldIndex();
        var steps = Fake() with { Scan = a => { FakeScan(a); return 1; } };

        var result = Sync(project, steps);

        Assert.IsFalse(result.Ok);
        Assert.AreEqual("old", File.ReadAllText(Path.Combine(project.IndexDir, "symbols.jsonl")));
        Assert.IsFalse(File.Exists(Path.Combine(project.IndexDir, SyncMarker.FileName)));
        Assert.AreEqual(0, StagingLeftovers(project).Count);
    }

    [TestMethod] // map is a required step: with the old code a failing map left the new scan published and MAP.md stale
    public void Failed_map_publishes_nothing()
    {
        var project = NewProject();
        project.SeedOldIndex();
        var steps = Fake() with { Map = _ => 1 };

        var result = Sync(project, steps);

        Assert.IsFalse(result.Ok);
        Assert.AreEqual("old", File.ReadAllText(Path.Combine(project.IndexDir, "symbols.jsonl")));
        Assert.AreEqual(0, StagingLeftovers(project).Count);
    }

    [TestMethod]
    public void Marker_is_absent_while_steps_run_and_present_in_the_published_index()
    {
        var project = NewProject();
        var markerSeenByMap = true;
        var steps = Fake() with
        {
            Map = a =>
            {
                markerSeenByMap = File.Exists(Path.Combine(OutOf(a), "index", SyncMarker.FileName));
                return FakeMap(a);
            },
        };

        Assert.IsTrue(Sync(project, steps).Ok);

        Assert.IsFalse(markerSeenByMap);
        var marker = SyncMarker.TryRead(project.IndexDir, out var problem);
        Assert.IsNull(problem);
        Assert.IsNotNull(marker);
        Assert.AreEqual(1, marker.SchemaVersion);
        Assert.AreEqual("app", marker.Project);
        Assert.AreEqual(32, marker.SyncId.Length);
        Assert.AreEqual("abc123", marker.GitCommit);
        Assert.AreEqual(JsonUtil.ReadFile<MetaModel>(Path.Combine(project.IndexDir, "meta.json"))!.IndexedAt, marker.MetaIndexedAt);
        Assert.IsTrue(DateTimeOffset.TryParse(marker.CompletedAt, out _), marker.CompletedAt);
        CollectionAssert.AreEqual(
            new[] { "scan", "scan-git", "scan-fe", "link", "map" }, marker.Steps.Select(s => s.Name).ToArray());
        Assert.AreEqual(SyncStepResult.Ok, marker.Steps.Single(s => s.Name == "scan").Status);
        Assert.AreEqual(SyncStepResult.Ok, marker.Steps.Single(s => s.Name == "map").Status);
    }

    [TestMethod]
    public void Every_sync_gets_its_own_sync_id()
    {
        var project = NewProject();

        Assert.IsTrue(Sync(project, Fake()).Ok);
        var first = SyncMarker.TryRead(project.IndexDir, out _)!.SyncId;
        Assert.IsTrue(Sync(project, Fake()).Ok);
        var second = SyncMarker.TryRead(project.IndexDir, out _)!.SyncId;

        Assert.AreNotEqual(first, second);
    }

    [TestMethod]
    public void Steps_that_did_not_run_are_recorded_as_skipped_with_the_reason()
    {
        var project = NewProject(); // no frontend, not a git repo

        Assert.IsTrue(Sync(project, Fake()).Ok);

        var marker = SyncMarker.TryRead(project.IndexDir, out _)!;
        foreach (var name in new[] { "scan-git", "scan-fe", "link" })
        {
            var step = marker.Steps.Single(s => s.Name == name);
            Assert.AreEqual(SyncStepResult.Skipped, step.Status, name);
            Assert.IsFalse(string.IsNullOrWhiteSpace(step.Detail), name);
        }
    }

    [TestMethod]
    public void Optional_steps_that_fail_still_publish_and_leave_no_half_written_files()
    {
        var project = NewProject(withFrontend: true, gitRepo: true);
        var steps = Fake() with
        {
            ScanGit = a =>
            {
                File.WriteAllText(Path.Combine(OutOf(a), "index", "ticket-files.jsonl"), "half");
                return 1;
            },
            ScanFe = _ => throw new InvalidOperationException("node exploded"),
        };

        var result = Sync(project, steps);

        Assert.IsTrue(result.Ok, result.Stderr);
        Assert.IsFalse(File.Exists(Path.Combine(project.IndexDir, "ticket-files.jsonl")), "a failed step's output must not be published");
        var marker = SyncMarker.TryRead(project.IndexDir, out _)!;
        Assert.AreEqual(SyncStepResult.Failed, marker.Steps.Single(s => s.Name == "scan-git").Status);
        Assert.AreEqual(SyncStepResult.Failed, marker.Steps.Single(s => s.Name == "scan-fe").Status);
        StringAssert.Contains(marker.Steps.Single(s => s.Name == "scan-fe").Detail, "node exploded");
        Assert.AreEqual(SyncStepResult.Skipped, marker.Steps.Single(s => s.Name == "link").Status);
    }

    [TestMethod] // exit 2 from scan-git: co-change written, tickets refused — the co-change file is worth publishing
    public void Scan_git_exit_code_2_keeps_co_change_and_is_recorded_as_partial()
    {
        var project = NewProject(gitRepo: true);
        var steps = Fake() with
        {
            ScanGit = a =>
            {
                File.WriteAllText(Path.Combine(OutOf(a), "index", "co-change.jsonl"), "pairs");
                return 2;
            },
        };

        Assert.IsTrue(Sync(project, steps).Ok);

        Assert.AreEqual("pairs", File.ReadAllText(Path.Combine(project.IndexDir, "co-change.jsonl")));
        var step = SyncMarker.TryRead(project.IndexDir, out _)!.Steps.Single(s => s.Name == "scan-git");
        Assert.AreEqual(SyncStepResult.Partial, step.Status);
        StringAssert.Contains(step.Detail, "ticket");
    }

    [TestMethod]
    public void Frontend_steps_are_recorded_ok_when_they_run()
    {
        var project = NewProject(withFrontend: true);

        Assert.IsTrue(Sync(project, Fake()).Ok);

        var marker = SyncMarker.TryRead(project.IndexDir, out _)!;
        Assert.AreEqual(SyncStepResult.Ok, marker.Steps.Single(s => s.Name == "scan-fe").Status);
        Assert.AreEqual(SyncStepResult.Ok, marker.Steps.Single(s => s.Name == "link").Status);
        Assert.IsTrue(File.Exists(Path.Combine(project.IndexDir, "frontend-calls.jsonl")));
        Assert.IsTrue(File.Exists(Path.Combine(project.IndexDir, "api-links.jsonl")));
    }

    [TestMethod] // Windows refuses to rename a directory while a file in it is open; the old index must come out of that unharmed
    public void Open_file_in_the_old_index_fails_the_publish_cleanly_and_keeps_the_old_index()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Directory renames only fail on open files on Windows.");

        var project = NewProject();
        project.SeedOldIndex();
        using var holder = new FileStream(
            Path.Combine(project.IndexDir, "symbols.jsonl"), FileMode.Open, FileAccess.Read, FileShare.None);

        var result = Sync(project, Fake() with { PublishRetryDelays = NoRetryDelays });

        Assert.IsFalse(result.Ok);
        StringAssert.Contains(result.Stderr, "previous index");
        holder.Dispose();
        Assert.AreEqual("old", File.ReadAllText(Path.Combine(project.IndexDir, "symbols.jsonl")));
        Assert.IsFalse(File.Exists(Path.Combine(project.IndexDir, SyncMarker.FileName)));
        Assert.AreEqual(0, StagingLeftovers(project).Count);
        Assert.AreEqual(0, Directory.GetDirectories(project.OutDir, "index.old-*").Length);
    }

    [TestMethod]
    public void Open_file_that_is_released_during_the_retries_does_not_fail_the_publish()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Directory renames only fail on open files on Windows.");

        var project = NewProject();
        project.SeedOldIndex();
        var holder = new FileStream(
            Path.Combine(project.IndexDir, "symbols.jsonl"), FileMode.Open, FileAccess.Read, FileShare.None);
        var releaser = Task.Run(() => { Thread.Sleep(250); holder.Dispose(); });
        var delays = Enumerable.Repeat(TimeSpan.FromMilliseconds(100), 20).ToArray();

        var result = Sync(project, Fake() with { PublishRetryDelays = delays });
        releaser.Wait();

        Assert.IsTrue(result.Ok, result.Stderr);
        Assert.AreEqual("new", File.ReadAllText(Path.Combine(project.IndexDir, "symbols.jsonl")));
    }

    [TestMethod] // the nightly task and a person running sync by hand must not interleave their writes
    public void Second_sync_of_the_same_output_is_refused_while_one_is_running()
    {
        var project = NewProject();
        bool? inner = null;
        string? innerStderr = null;
        var steps = Fake();
        steps = steps with
        {
            Scan = a =>
            {
                var nested = Sync(project, Fake());
                inner = nested.Ok;
                innerStderr = nested.Stderr;
                return FakeScan(a);
            },
        };

        var outer = Sync(project, steps);

        Assert.IsTrue(outer.Ok, outer.Stderr);
        Assert.AreEqual(false, inner);
        StringAssert.Contains(innerStderr, "already");
        Assert.AreEqual(0, StagingLeftovers(project).Count);
    }

    [TestMethod] // a process killed between the two renames leaves no index/ at all; the next sync must put the old one back even if it fails
    public void Interrupted_publish_is_recovered_by_the_next_sync()
    {
        var project = NewProject();
        var parked = Path.Combine(project.OutDir, "index.old-deadbeef");
        Directory.CreateDirectory(parked);
        File.WriteAllText(Path.Combine(parked, "symbols.jsonl"), "old");

        var result = Sync(project, Fake() with { Scan = _ => 1 });

        Assert.IsFalse(result.Ok);
        Assert.AreEqual("old", File.ReadAllText(Path.Combine(project.IndexDir, "symbols.jsonl")));
        Assert.IsFalse(Directory.Exists(parked));
    }

    [TestMethod] // a crash mid-pipeline leaves a staging directory behind; the next run clears it instead of piling them up
    public void Staging_directory_left_by_a_crashed_run_is_removed_by_the_next_sync()
    {
        var project = NewProject();
        var leftover = Path.Combine(Path.GetDirectoryName(project.OutDir)!, "app.sync-staging-crashed");
        Directory.CreateDirectory(Path.Combine(leftover, "index"));
        File.WriteAllText(Path.Combine(leftover, "index", "symbols.jsonl"), "stale");

        Assert.IsTrue(Sync(project, Fake()).Ok);

        Assert.IsFalse(Directory.Exists(leftover));
    }

    [TestMethod] // files in Output that sync does not own (the nightly script's logs/) must survive the swap
    public void Files_in_output_that_sync_does_not_produce_are_left_alone()
    {
        var project = NewProject();
        project.SeedOldIndex();
        Directory.CreateDirectory(Path.Combine(project.OutDir, "logs"));
        File.WriteAllText(Path.Combine(project.OutDir, "logs", "scan.log"), "keep me");

        Assert.IsTrue(Sync(project, Fake()).Ok);

        Assert.AreEqual("keep me", File.ReadAllText(Path.Combine(project.OutDir, "logs", "scan.log")));
    }

    [TestMethod]
    public void Map_and_module_docs_are_published_next_to_the_index()
    {
        var project = NewProject();

        Assert.IsTrue(Sync(project, Fake()).Ok);

        Assert.AreEqual("new map", File.ReadAllText(Path.Combine(project.OutDir, "MAP.md")));
        Assert.IsTrue(File.Exists(Path.Combine(project.OutDir, "modules", "App.md")));
    }

    [TestMethod] // the real MapCommand reads the previous MAP.md for the text between the human markers; staging must not hide it
    public void Hand_written_notes_in_map_md_survive_a_sync()
    {
        var project = NewProject();
        Directory.CreateDirectory(project.OutDir);
        File.WriteAllText(
            Path.Combine(project.OutDir, "MAP.md"),
            "# old\n<!-- human:start -->\nremember: orders are archived nightly\n<!-- human:end -->\n");
        Directory.CreateDirectory(Path.Combine(project.OutDir, "modules"));
        File.WriteAllText(
            Path.Combine(project.OutDir, "modules", "Orders.Core.md"),
            "<!-- human:start -->\nmodule note survives\n<!-- human:end -->\n");
        var steps = Fake() with
        {
            Scan = a => { FakeScan(a); WriteOneSymbol(OutOf(a), "Orders.Core"); return 0; },
            Map = CodeMap.Query.Map.MapCommand.Run,
        };

        var result = Sync(project, steps);

        Assert.IsTrue(result.Ok, result.Stderr);
        StringAssert.Contains(File.ReadAllText(Path.Combine(project.OutDir, "MAP.md")), "remember: orders are archived nightly");
        StringAssert.Contains(File.ReadAllText(Path.Combine(project.OutDir, "modules", "Orders.Core.md")), "module note survives");
    }

    [TestMethod] // the whole real pipeline, once: scan (Roslyn), scan-git, map — the fakes above prove the orchestration, this proves the commands fit it
    public void Real_pipeline_publishes_a_complete_index_with_a_marker()
    {
        L2TestSetup.EnsureFixtureRestored();
        var repo = GitFixtureHelper.NewRepo();
        for (var i = 0; i < 3; i++)
            GitFixtureHelper.Commit(repo, $"fix #1000 pass {i}", ("a.cs", $"v{i}"), ("b.cs", $"v{i}"));
        var project = NewProject(repoDir: repo, solution: TestPaths.FixtureSolution);

        var result = Sync(project, SyncSteps.Real);

        Assert.IsTrue(result.Ok, result.Stderr);
        foreach (var file in new[] { "symbols.jsonl", "edges.jsonl", "meta.json", "ticket-files.jsonl", "co-change.jsonl", SyncMarker.FileName })
            Assert.IsTrue(File.Exists(Path.Combine(project.IndexDir, file)), file);
        Assert.IsTrue(File.Exists(Path.Combine(project.OutDir, "MAP.md")));
        var marker = SyncMarker.TryRead(project.IndexDir, out _)!;
        CollectionAssert.AreEqual(
            new[] { SyncStepResult.Ok, SyncStepResult.Ok, SyncStepResult.Skipped, SyncStepResult.Skipped, SyncStepResult.Ok },
            marker.Steps.Select(s => s.Status).ToArray());
        Assert.AreEqual(0, StagingLeftovers(project).Count);
    }

    // ---- fixtures -------------------------------------------------------------------------------------------------

    private sealed class TestProject
    {
        public required ProjectRegistry Registry { get; init; }
        public required ProjectEntry Entry { get; init; }
        public required string OutDir { get; init; }
        public string IndexDir => Path.Combine(OutDir, "index");

        public void SeedOldIndex(params string[] extraFiles)
        {
            Directory.CreateDirectory(IndexDir);
            File.WriteAllText(Path.Combine(IndexDir, "symbols.jsonl"), "old");
            File.WriteAllText(Path.Combine(IndexDir, "meta.json"), "{}");
            foreach (var f in extraFiles) File.WriteAllText(Path.Combine(IndexDir, f), "old");
        }
    }

    private static TestProject NewProject(bool withFrontend = false, bool gitRepo = false, string? repoDir = null, string? solution = null)
    {
        var root = TestPaths.NewTempDir();
        Directory.CreateDirectory(Path.Combine(root, "src"));
        var solutionPath = solution ?? Path.Combine(root, "src", "App.sln");
        if (solution == null) File.WriteAllText(solutionPath, "");

        var repo = repoDir ?? Path.Combine(root, "src");
        if (gitRepo && repoDir == null) GitFixtureHelper.InitInPlace(repo);
        if (withFrontend) Directory.CreateDirectory(Path.Combine(root, "fe"));

        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            projects = new[]
            {
                new
                {
                    name = "app",
                    solution = solutionPath,
                    output = Path.Combine(root, "out", "app"),
                    repo,
                    frontend = withFrontend ? Path.Combine(root, "fe") : null,
                },
            },
        });
        File.WriteAllText(Path.Combine(root, ProjectRegistry.FileName), json);
        var registry = ProjectRegistry.LoadFrom(Path.Combine(root, ProjectRegistry.FileName));
        var entry = registry.Require("app");
        return new TestProject { Registry = registry, Entry = entry, OutDir = registry.ResolvePath(entry.Output) };
    }

    private static (bool Ok, string Stdout, string Stderr) Sync(TestProject project, SyncSteps steps)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var previousOut = Console.Out;
        var previousErr = Console.Error;
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var ok = SyncCommand.SyncOne(project.Registry, project.Entry, steps);
            return (ok, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousErr);
        }
    }

    private static List<string> StagingLeftovers(TestProject project)
    {
        var parent = Path.GetDirectoryName(project.OutDir)!;
        return Directory.Exists(parent)
            ? Directory.GetDirectories(parent, "app.sync-staging-*").ToList()
            : new List<string>();
    }

    private static string OutOf(string[] args) => args[Array.IndexOf(args, "--out") + 1];

    private static SyncSteps Fake() => new(FakeScan, FakeScanGit, FakeScanFe, FakeLink, FakeMap) { PublishRetryDelays = NoRetryDelays };

    private static int FakeScan(string[] args)
    {
        var index = Path.Combine(OutOf(args), "index");
        Directory.CreateDirectory(index);
        File.WriteAllText(Path.Combine(index, "symbols.jsonl"), "new");
        JsonUtil.WriteIndented(Path.Combine(index, "meta.json"), new MetaModel
        {
            IndexedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            GitCommit = "abc123",
            SolutionPath = "App.sln",
            ProjectCount = 1,
            SymbolCount = 1,
            EdgeCount = 0,
        });
        return 0;
    }

    private static int FakeScanGit(string[] args)
    {
        var index = Path.Combine(OutOf(args), "index");
        File.WriteAllText(Path.Combine(index, "ticket-files.jsonl"), "");
        File.WriteAllText(Path.Combine(index, "co-change.jsonl"), "");
        return 0;
    }

    private static int FakeScanFe(string[] args)
    {
        File.WriteAllText(Path.Combine(OutOf(args), "index", "frontend-calls.jsonl"), "");
        return 0;
    }

    private static int FakeLink(string[] args)
    {
        File.WriteAllText(Path.Combine(args[Array.IndexOf(args, "--index") + 1], "api-links.jsonl"), "");
        return 0;
    }

    private static int FakeMap(string[] args)
    {
        var outDir = OutOf(args);
        Directory.CreateDirectory(Path.Combine(outDir, "modules"));
        File.WriteAllText(Path.Combine(outDir, "MAP.md"), "new map");
        File.WriteAllText(Path.Combine(outDir, "modules", "App.md"), "module");
        return 0;
    }

    private static void WriteOneSymbol(string outDir, string project)
        => JsonlWriter.Write(Path.Combine(outDir, "index", "symbols.jsonl"), new[]
        {
            new SymbolRecord
            {
                Id = "T:Orders.Core.Thing", Kind = "class", Name = "Thing", Project = project,
                File = "Thing.cs", Line = 1, Accessibility = "public",
            },
        });
}
