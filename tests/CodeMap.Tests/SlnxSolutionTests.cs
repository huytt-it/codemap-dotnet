using CodeMap.Query.Impact;
using CodeMap.Roslyn.Scan;

namespace CodeMap.Tests;

/// <summary>
/// .slnx is the solution format the .NET 10 SDK now emits by default (`dotnet new sln` produces no .sln at all),
/// so a repo created on a current SDK has only this format. Before it was handled, `scan` reported
/// "Warning: no .csproj project found" and then "done: 0 symbols" — a silent empty index that reads like a
/// codebase with nothing in it rather than like a failure. These tests scan the same two fixture projects
/// through both formats and assert the results match.
/// </summary>
[TestClass]
public class SlnxSolutionTests
{
    [TestMethod]
    public void Slnx_yields_the_same_projects_as_the_equivalent_sln()
    {
        var fromSln = SolutionFileParser.ParseProjects(TestPaths.FixtureSolution);
        var fromSlnx = SolutionFileParser.ParseProjects(TestPaths.FixtureSolutionSlnx);

        CollectionAssert.AreEquivalent(
            fromSln.Select(p => p.FullPath).ToList(),
            fromSlnx.Select(p => p.FullPath).ToList());
    }

    [TestMethod]
    public void Project_nested_in_a_solution_folder_is_found()
    {
        // Orders.Core sits inside <Folder Name="/core/"> in the fixture — Elements() would miss it, Descendants() finds it.
        var projects = SolutionFileParser.ParseProjects(TestPaths.FixtureSolutionSlnx);

        Assert.IsTrue(projects.Any(p => p.Name == "Orders.Core"), "a project inside a solution folder must still be parsed");
    }

    [TestMethod]
    public void Slnx_forward_slash_paths_resolve_on_this_platform()
    {
        var projects = SolutionFileParser.ParseProjects(TestPaths.FixtureSolutionSlnx);

        Assert.AreEqual(2, projects.Count);
        foreach (var p in projects)
            Assert.IsTrue(File.Exists(p.FullPath), $"parsed path should point at a real file: {p.FullPath}");
    }

    [TestMethod]
    public void Malformed_slnx_reports_the_parse_error_instead_of_an_empty_solution()
    {
        var dir = TestPaths.NewTempDir();
        var path = Path.Combine(dir, "Broken.slnx");
        File.WriteAllText(path, "<Solution><Project Path=\"a/b.csproj\" ></Solution>"); // unclosed element

        var stderr = new StringWriter();
        var original = Console.Error;
        try
        {
            Console.SetError(stderr);
            var projects = SolutionFileParser.ParseProjects(path);
            Assert.AreEqual(0, projects.Count);
        }
        finally
        {
            Console.SetError(original);
        }

        StringAssert.Contains(stderr.ToString(), "could not parse", "a broken .slnx must say so, not look like an empty solution");
    }

    [TestMethod]
    public void Real_scan_through_slnx_produces_the_same_symbol_count_as_through_sln()
    {
        L2TestSetup.EnsureFixtureRestored();

        var slnOut = TestPaths.NewTempDir();
        var slnxOut = TestPaths.NewTempDir();

        new SemanticScanner(includeExternal: false).Scan(TestPaths.FixtureSolution, slnOut);
        new SemanticScanner(includeExternal: false).Scan(TestPaths.FixtureSolutionSlnx, slnxOut);

        var viaSln = ImpactIndex.Load(Path.Combine(slnOut, "index"));
        var viaSlnx = ImpactIndex.Load(Path.Combine(slnxOut, "index"));

        Assert.AreNotEqual(0, viaSlnx.SymbolsById.Count, "the whole point: .slnx must not silently produce an empty index");
        CollectionAssert.AreEquivalent(viaSln.SymbolsById.Keys.ToList(), viaSlnx.SymbolsById.Keys.ToList());
    }
}
