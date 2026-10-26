using System.Text.Json;
using CodeMap.Cli.Cli;
using CodeMap.Query.ArgParsing;
using CodeMap.Query.Config;
using CodeMap.Query.Git;
using CodeMap.Query.Impact;
using CodeMap.Query.Map;
using CodeMap.Query.Models;

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
    private const string TargetId = "M:Orders.Svc.Cancel";
    private const string CallerId = "M:Orders.Ctl.Post";
    private const string SharedId = "M:Shared.Util.Helper";
    private const string EscapingId = "M:Evil.Secret.Leak";
    private const string LeakMarker = "THIS-TEXT-LIVES-OUTSIDE-THE-REPO";

    private sealed record Fixture(string ConfigPath, string Repo, string IndexDir, string SolutionDir);

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

    private delegate int CommandRun(string[] args);

    /// <summary>Runs a real command with the process cwd set to <paramref name="cwd"/>, writing through --out so nothing depends on capturing the console.</summary>
    private static string RunCommand(CommandRun command, string cwd, params string[] args)
    {
        var outFile = Path.Combine(TestPaths.NewTempDir(), "out.md");
        var previous = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(cwd);
            Assert.AreEqual(0, command(args.Concat(new[] { "--out", outFile }).ToArray()));
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }

        return File.ReadAllText(outFile);
    }

    /// <summary>
    /// root/repo (git) with the solution in <paramref name="solutionDir"/>, root/out/index (a hand-written index
    /// as of HEAD), root/config/codemap.projects.json, and root/outside/Evil.cs next to — not inside — the repo.
    /// After the commit Svc.cs is edited without committing, so a correct banner has something to report.
    /// </summary>
    private static Fixture Build(string solutionDir, bool explicitRepo)
    {
        var root = TestPaths.NewTempDir();
        var repo = Path.Combine(root, "repo");
        Directory.CreateDirectory(repo);
        GitFixtureHelper.InitInPlace(repo);

        string InSolution(string file) => solutionDir.Length == 0 ? file : $"{solutionDir}/{file}";
        const string svcSource = "namespace Orders;\npublic class Svc\n{\n    public void Cancel() { }\n}\n";
        GitFixtureHelper.Commit(repo, "initial",
            (InSolution("App.sln"), "Microsoft Visual Studio Solution File"),
            (InSolution("Orders/Svc.cs"), svcSource),
            (InSolution("Orders/Ctl.cs"), "namespace Orders;\npublic class Ctl\n{\n    public void Post() { }\n}\n"),
            ("Shared/Util.cs", "namespace Shared;\npublic class Util\n{\n    public void Helper() { }\n}\n"));
        var head = GitRepoInfo.TryGetHeadCommit(repo)!;
        File.WriteAllText(Path.Combine(repo, InSolution("Orders/Svc.cs")), svcSource.Replace("Cancel() { }", "Cancel() { var edited = 1; }"));

        var outsideDir = Path.Combine(root, "outside");
        Directory.CreateDirectory(outsideDir);
        File.WriteAllText(
            Path.Combine(outsideDir, "Evil.cs"),
            $"namespace Evil;\npublic class Secret\n{{\n    public void Leak() {{ }} // {LeakMarker}\n}}\n");

        var solutionPath = InSolution("App.sln");
        var indexDir = Path.Combine(root, "out", "index");
        Directory.CreateDirectory(indexDir);

        // symbols.jsonl paths are relative to the solution dir; "../" climbs out of it.
        var sharedFromSolution = solutionDir.Length == 0 ? "Shared/Util.cs" : "../Shared/Util.cs";
        var escapeFromSolution = (solutionDir.Length == 0 ? "../" : "../../") + "outside/Evil.cs";
        WriteJsonl(Path.Combine(indexDir, "symbols.jsonl"),
            Symbol(TargetId, "Cancel", "Orders.Svc", "Orders/Svc.cs", 4),
            Symbol(CallerId, "Post", "Orders.Ctl", "Orders/Ctl.cs", 4),
            Symbol(SharedId, "Helper", "Shared.Util", sharedFromSolution, 4),
            Symbol(EscapingId, "Leak", "Evil.Secret", escapeFromSolution, 4));
        WriteJsonl(Path.Combine(indexDir, "edges.jsonl"),
            new { from = CallerId, to = TargetId, kind = "call", file = "Orders/Ctl.cs", line = 4 });
        File.WriteAllText(
            Path.Combine(indexDir, "entrypoints.json"),
            JsonSerializer.Serialize(new[] { new { id = CallerId, type = "http", httpMethod = "POST", route = "api/orders" } }));
        File.WriteAllText(
            Path.Combine(indexDir, "meta.json"),
            JsonSerializer.Serialize(new
            {
                indexedAt = "2026-01-01T00:00:00Z",
                gitCommit = head,
                solutionPath,
                projectCount = 1,
                symbolCount = 4,
                edgeCount = 1,
            }));

        var configDir = Path.Combine(root, "config");
        Directory.CreateDirectory(configDir);
        var configPath = Path.Combine(configDir, ProjectRegistry.FileName);
        var repoField = explicitRepo ? ", \"repo\": \"../repo\"" : "";
        File.WriteAllText(configPath, $$"""
            { "projects": [ { "name": "app", "solution": "../repo/{{solutionPath}}", "output": "../out"{{repoField}} } ] }
            """);

        return new Fixture(configPath, Path.GetFullPath(repo), Path.GetFullPath(indexDir), Path.GetFullPath(Path.Combine(repo, solutionDir)));
    }

    private static object Symbol(string id, string name, string containingType, string file, int line) => new
    {
        id,
        kind = "Method",
        name,
        containingType,
        project = "Orders",
        file,
        line,
        accessibility = "Public",
    };

    private static void WriteJsonl(string path, params object[] rows)
        => File.WriteAllLines(path, rows.Select(r => JsonSerializer.Serialize(r)));
}
