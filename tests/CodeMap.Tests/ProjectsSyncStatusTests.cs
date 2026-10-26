using CodeMap.Query.Config;
using CodeMap.Query.Json;
using CodeMap.Query.Models;

namespace CodeMap.Tests;

/// <summary>`codemap projects` says whether an index came out of a finished `sync`, so "built" and "built completely" stop looking the same.</summary>
[TestClass]
public class ProjectsSyncStatusTests
{
    private const string IndexedAt = "2026-10-01T03:00:00Z";

    [TestMethod]
    public void Index_with_a_matching_marker_is_reported_as_a_complete_sync()
    {
        var output = ProjectsOutput(IndexedAt, Marker(IndexedAt, new SyncStepResult { Name = "map", Status = SyncStepResult.Ok }));

        StringAssert.Contains(output, "sync     : complete");
        StringAssert.Contains(output, "2026-10-02T03:05:00Z");
    }

    [TestMethod]
    public void Steps_that_did_not_finish_cleanly_are_listed()
    {
        var output = ProjectsOutput(IndexedAt, Marker(
            IndexedAt,
            new SyncStepResult { Name = "scan", Status = SyncStepResult.Ok },
            new SyncStepResult { Name = "scan-git", Status = SyncStepResult.Partial, Detail = "tickets refused" },
            new SyncStepResult { Name = "scan-fe", Status = SyncStepResult.Failed, Detail = "node exploded" },
            new SyncStepResult { Name = "link", Status = SyncStepResult.Skipped, Detail = "scan-fe did not complete" }));

        StringAssert.Contains(output, "scan-git partial");
        StringAssert.Contains(output, "scan-fe failed");
        Assert.IsFalse(output.Contains("scan ok", StringComparison.Ordinal), "clean steps are not worth a mention");
    }

    [TestMethod] // `scan` by hand never writes a marker; the index may be perfectly fine, but nothing vouches for it
    public void Index_without_a_marker_says_it_was_not_produced_by_sync()
    {
        var output = ProjectsOutput(IndexedAt, marker: null);

        StringAssert.Contains(output, "no sync-complete.json");
        StringAssert.Contains(output, "codemap sync");
    }

    [TestMethod] // a scan run by hand on top of a synced index rewrites meta.json and leaves the old marker behind
    public void Marker_older_than_the_scan_in_the_index_is_flagged()
    {
        var output = ProjectsOutput("2026-10-05T03:00:00Z", Marker(IndexedAt));

        StringAssert.Contains(output, "changed since the last sync");
    }

    [TestMethod]
    public void Unreadable_marker_is_reported_instead_of_crashing()
    {
        var output = ProjectsOutput(IndexedAt, marker: null, rawMarker: "{ not json");

        StringAssert.Contains(output, "sync-complete.json unreadable");
    }

    [TestMethod]
    public void Project_that_was_never_built_has_no_sync_line()
    {
        var root = TestPaths.NewTempDir();
        File.WriteAllText(
            Path.Combine(root, ProjectRegistry.FileName),
            """{ "projects": [ { "name": "app", "solution": "a.sln", "output": "idx" } ] }""");

        var output = Run(Path.Combine(root, ProjectRegistry.FileName));

        StringAssert.Contains(output, "NOT BUILT");
        Assert.IsFalse(output.Contains("sync     :", StringComparison.Ordinal));
    }

    private static SyncMarker Marker(string metaIndexedAt, params SyncStepResult[] steps) => new()
    {
        SyncId = new string('a', 32),
        CompletedAt = "2026-10-02T03:05:00Z",
        Project = "app",
        MetaIndexedAt = metaIndexedAt,
        Steps = steps.ToList(),
    };

    private static string ProjectsOutput(string metaIndexedAt, SyncMarker? marker, string? rawMarker = null)
    {
        var root = TestPaths.NewTempDir();
        var configPath = Path.Combine(root, ProjectRegistry.FileName);
        File.WriteAllText(configPath, """{ "projects": [ { "name": "app", "solution": "a.sln", "output": "idx" } ] }""");

        var indexDir = Path.Combine(root, "idx", "index");
        Directory.CreateDirectory(indexDir);
        JsonUtil.WriteIndented(Path.Combine(indexDir, "meta.json"), new MetaModel
        {
            IndexedAt = metaIndexedAt, SolutionPath = "a.sln", ProjectCount = 1, SymbolCount = 3, EdgeCount = 2,
        });
        if (marker != null) JsonUtil.WriteIndented(Path.Combine(indexDir, SyncMarker.FileName), marker);
        if (rawMarker != null) File.WriteAllText(Path.Combine(indexDir, SyncMarker.FileName), rawMarker);

        return Run(configPath);
    }

    private static string Run(string configPath)
    {
        var stdout = new StringWriter();
        var previous = Console.Out;
        try
        {
            Console.SetOut(stdout);
            Assert.AreEqual(0, ProjectsCommand.Run(new[] { "--config", configPath }));
        }
        finally
        {
            Console.SetOut(previous);
        }

        return stdout.ToString();
    }
}
