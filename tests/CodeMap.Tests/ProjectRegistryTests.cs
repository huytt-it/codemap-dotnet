using CodeMap.Query.ArgParsing;
using CodeMap.Query.Config;

namespace CodeMap.Tests;

/// <summary>
/// `codemap.projects.json` — the registry that lets one file hold every codebase's paths, so `--project X`
/// replaces four absolute paths and an agent can read where an index lives instead of being handed a
/// hardcoded one. The rules that matter are: relative paths resolve against the CONFIG FILE, not the current
/// directory (otherwise the file breaks the moment you run from a subfolder, or clone the tree elsewhere), and
/// every user-facing mistake produces a usage error rather than a stack trace.
/// </summary>
[TestClass]
public class ProjectRegistryTests
{
    [TestMethod]
    public void Relative_paths_resolve_against_the_config_file_not_the_current_directory()
    {
        var dir = WriteRegistry("""
            { "projects": [ { "name": "app", "solution": "src/App.sln", "output": "idx/app" } ] }
            """);

        var registry = ProjectRegistry.LoadFrom(Path.Combine(dir, ProjectRegistry.FileName));
        var entry = registry.Require("app");

        Assert.AreEqual(Path.GetFullPath(Path.Combine(dir, "src", "App.sln")), registry.ResolvePath(entry.Solution));
        Assert.AreEqual(Path.GetFullPath(Path.Combine(dir, "idx", "app", "index")), registry.IndexDirOf(entry));
    }

    [TestMethod]
    public void Absolute_paths_are_left_alone()
    {
        var abs = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "elsewhere", "App.sln")).Replace("\\", "/");
        var dir = WriteRegistry($$"""
            { "projects": [ { "name": "app", "solution": "{{abs}}", "output": "idx" } ] }
            """);

        var registry = ProjectRegistry.LoadFrom(Path.Combine(dir, ProjectRegistry.FileName));

        Assert.AreEqual(Path.GetFullPath(abs), registry.ResolvePath(registry.Require("app").Solution));
    }

    [TestMethod]
    public void Repo_defaults_to_the_solution_directory_when_not_given()
    {
        var dir = WriteRegistry("""
            { "projects": [ { "name": "app", "solution": "src/App.sln", "output": "idx" } ] }
            """);

        var registry = ProjectRegistry.LoadFrom(Path.Combine(dir, ProjectRegistry.FileName));

        Assert.AreEqual(Path.GetFullPath(Path.Combine(dir, "src")), registry.RepoOf(registry.Require("app")));
    }

    [TestMethod]
    public void Explicit_repo_overrides_the_solution_directory()
    {
        var dir = WriteRegistry("""
            { "projects": [ { "name": "app", "solution": "src/nested/App.sln", "output": "idx", "repo": "src" } ] }
            """);

        var registry = ProjectRegistry.LoadFrom(Path.Combine(dir, ProjectRegistry.FileName));

        Assert.AreEqual(Path.GetFullPath(Path.Combine(dir, "src")), registry.RepoOf(registry.Require("app")));
    }

    [TestMethod]
    public void Project_lookup_is_case_insensitive()
    {
        var dir = WriteRegistry("""
            { "projects": [ { "name": "MyApp", "solution": "a.sln", "output": "idx" } ] }
            """);

        var registry = ProjectRegistry.LoadFrom(Path.Combine(dir, ProjectRegistry.FileName));

        Assert.AreEqual("MyApp", registry.Require("myapp").Name);
    }

    [TestMethod]
    public void Unknown_project_name_lists_the_known_ones_instead_of_just_failing()
    {
        var dir = WriteRegistry("""
            { "projects": [
                { "name": "shop", "solution": "a.sln", "output": "i1" },
                { "name": "orders", "solution": "b.sln", "output": "i2" } ] }
            """);
        var registry = ProjectRegistry.LoadFrom(Path.Combine(dir, ProjectRegistry.FileName));

        var ex = Assert.ThrowsException<CliUsageException>(() => registry.Require("nope"));

        StringAssert.Contains(ex.Message, "shop");
        StringAssert.Contains(ex.Message, "orders");
    }

    [TestMethod]
    public void Duplicate_project_names_are_rejected_at_load_time()
    {
        var dir = WriteRegistry("""
            { "projects": [
                { "name": "app", "solution": "a.sln", "output": "i1" },
                { "name": "APP", "solution": "b.sln", "output": "i2" } ] }
            """);

        var ex = Assert.ThrowsException<CliUsageException>(
            () => ProjectRegistry.LoadFrom(Path.Combine(dir, ProjectRegistry.FileName)));

        StringAssert.Contains(ex.Message, "duplicate");
    }

    [TestMethod]
    public void Malformed_json_is_a_usage_error_naming_the_file_not_a_crash()
    {
        var dir = WriteRegistry("{ \"projects\": [ ");

        var ex = Assert.ThrowsException<CliUsageException>(
            () => ProjectRegistry.LoadFrom(Path.Combine(dir, ProjectRegistry.FileName)));

        StringAssert.Contains(ex.Message, ProjectRegistry.FileName);
    }

    [TestMethod]
    public void Discover_walks_up_from_a_subdirectory()
    {
        var root = WriteRegistry("""
            { "projects": [ { "name": "app", "solution": "a.sln", "output": "idx" } ] }
            """);
        var nested = Path.Combine(root, "src", "deep", "deeper");
        Directory.CreateDirectory(nested);

        var previous = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(nested);
            var registry = ProjectRegistry.Discover(null);

            Assert.IsNotNull(registry, "the registry in an ancestor directory should be found");
            Assert.AreEqual("app", registry.Require("app").Name);
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }
    }

    [TestMethod]
    public void Explicit_config_path_that_does_not_exist_is_a_usage_error()
    {
        var missing = Path.Combine(TestPaths.NewTempDir(), "nope.json");

        var ex = Assert.ThrowsException<CliUsageException>(() => ProjectRegistry.Discover(missing));

        StringAssert.Contains(ex.Message, "not found");
    }

    private static string WriteRegistry(string json)
    {
        var dir = TestPaths.NewTempDir();
        File.WriteAllText(Path.Combine(dir, ProjectRegistry.FileName), json);
        return dir;
    }
}
