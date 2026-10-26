using CodeMap.Cli.Cli;
using CodeMap.Query.ArgParsing;
using CodeMap.Query.Config;
using CodeMap.Query.Git;
using CodeMap.Query.Impact;
using CodeMap.Query.Map;
using CodeMap.Query.Models;
using static CodeMap.Tests.RegisteredProject;

namespace CodeMap.Tests;

/// <summary>
/// `impact`, `slice` and `map` used to read the staleness banner's git state, and `slice` its source files, from
/// the process working directory. That is fine when a person runs them from inside the repo; it is wrong for an
/// MCP server, where the client picks the cwd. With `--project` the repo is known from the registry, so the
/// output must not depend on where the process happens to be.
///
/// Every test here builds a real git repo plus a hand-written index and a registry, then runs the real command
/// from the repo and again from an unrelated directory.
/// </summary>
[TestClass]
public class CwdIndependenceTests
{
    // solution at src/ (the registry's `repo` omitted, so RepoOf defaults to the solution dir — not the git root),
    // the same with `repo` given, and a solution at the repo root.
    [DataTestMethod]
    [DataRow("src", false)]
    [DataRow("src", true)]
    [DataRow("", false)]
    public void Impact_with_project_gives_the_same_report_from_any_working_directory(string solutionDir, bool explicitRepo)
    {
        var fx = Build(solutionDir, explicitRepo);
        var elsewhere = TestPaths.NewTempDir();

        var fromRepo = RunCommand(ImpactCommand.Run, fx.Repo, "--project", "app", "--config", fx.ConfigPath, "--symbol", TargetId);
        var fromElsewhere = RunCommand(ImpactCommand.Run, elsewhere, "--project", "app", "--config", fx.ConfigPath, "--symbol", TargetId);

        // The edit to Svc.cs is uncommitted: only a banner that really ran git in the repo can say so.
        StringAssert.Contains(fromElsewhere, "1 relevant file(s) have uncommitted changes");
        Assert.AreEqual(fromRepo, fromElsewhere);
    }

    [DataTestMethod]
    [DataRow("src", false)]
    [DataRow("src", true)]
    [DataRow("", false)]
    public void Slice_with_project_gives_the_same_report_from_any_working_directory(string solutionDir, bool explicitRepo)
    {
        var fx = Build(solutionDir, explicitRepo);
        var elsewhere = TestPaths.NewTempDir();

        var fromRepo = RunCommand(SliceCommand.Run, fx.Repo, "--project", "app", "--config", fx.ConfigPath, "--symbol", TargetId);
        var fromElsewhere = RunCommand(SliceCommand.Run, elsewhere, "--project", "app", "--config", fx.ConfigPath, "--symbol", TargetId);

        StringAssert.Contains(fromElsewhere, "### Orders/Svc.cs:4");
        StringAssert.Contains(fromElsewhere, "Cancel()");
        StringAssert.Contains(fromElsewhere, "1 relevant file(s) have uncommitted changes");
        Assert.AreEqual(fromRepo, fromElsewhere);
    }

    [TestMethod] // a <Compile Include="..\Shared\*.cs"> file sits outside the solution dir but inside the repo
    public void Slice_with_project_reads_a_linked_file_that_is_outside_the_solution_dir_but_inside_the_repo()
    {
        var fx = Build("src", explicitRepo: false);

        var markdown = RunCommand(SliceCommand.Run, TestPaths.NewTempDir(), "--project", "app", "--config", fx.ConfigPath, "--symbol", SharedId);

        StringAssert.Contains(markdown, "Helper()");
        StringAssert.Contains(markdown, "### ../Shared/Util.cs:");
    }

    [TestMethod]
    public void Slice_with_project_refuses_to_read_a_file_outside_the_repo()
    {
        var fx = Build("src", explicitRepo: false);

        var markdown = RunCommand(SliceCommand.Run, TestPaths.NewTempDir(), "--project", "app", "--config", fx.ConfigPath, "--symbol", EscapingId);

        Assert.IsFalse(markdown.Contains(LeakMarker), "a symbols.jsonl path that climbs out of the repo must not be read");
        Assert.IsFalse(markdown.Contains("```csharp"), "no code block should be rendered for a refused path");
    }

    [TestMethod] // --index has no registry to learn the repo from: the old "run it from the repo root" convention stays
    public void Slice_with_index_still_resolves_against_the_working_directory()
    {
        var fx = Build("src", explicitRepo: false);

        var fromRepo = RunCommand(SliceCommand.Run, fx.Repo, "--index", fx.IndexDir, "--symbol", TargetId);
        var fromElsewhere = RunCommand(SliceCommand.Run, TestPaths.NewTempDir(), "--index", fx.IndexDir, "--symbol", TargetId);

        StringAssert.Contains(fromRepo, "### Orders/Svc.cs:4");
        Assert.IsFalse(fromElsewhere.Contains("### Orders/Svc.cs:4"), "from another directory --index cannot find the source, as before");
    }

    [TestMethod]
    public void Resolver_returns_the_git_root_for_a_project_and_nothing_for_a_bare_index()
    {
        var fx = Build("src", explicitRepo: false);

        var byProject = IndexPathResolver.ResolveLocation(
            Args.Parse(new[] { "--project", "app", "--config", fx.ConfigPath }, IndexPathResolver.OptionNames, Array.Empty<string>()));
        var byIndex = IndexPathResolver.ResolveLocation(
            Args.Parse(new[] { "--index", fx.IndexDir }, IndexPathResolver.OptionNames, Array.Empty<string>()));

        Assert.AreEqual(fx.IndexDir, byProject.IndexDir);
        // registry.RepoOf would say <repo>/src here (the solution dir); the git root is the folder meta.json's solutionPath is relative to.
        Assert.AreEqual(Path.GetFullPath(fx.Repo), Path.GetFullPath(byProject.RepoRoot!));
        Assert.AreEqual(Path.GetFullPath(fx.IndexDir), Path.GetFullPath(byIndex.IndexDir));
        Assert.IsNull(byIndex.RepoRoot);
    }

    [TestMethod]
    public void Map_banner_uses_the_given_repo_root_instead_of_the_working_directory()
    {
        var fx = Build("src", explicitRepo: false);
        var head = GitRepoInfo.TryGetHeadCommit(fx.Repo)!;
        var meta = new MetaModel { IndexedAt = "x", GitCommit = head, SolutionPath = "src/App.sln", ProjectCount = 1, SymbolCount = 0, EdgeCount = 0 };
        var generator = new MapGenerator(new List<SymbolRecord>(), new List<EdgeRecord>(), diagnostics: null, meta: meta, repoRoot: fx.Repo);

        var previous = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(TestPaths.NewTempDir());
            var map = generator.BuildMapMarkdown(null);

            StringAssert.Contains(map, "matches the scan");
            Assert.IsFalse(map.Contains("could not run git"));
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }
    }
}
