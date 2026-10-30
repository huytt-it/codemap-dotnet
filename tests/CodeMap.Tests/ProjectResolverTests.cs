using System.Text.Json;
using System.Text.Json.Serialization;
using CodeMap.Mcp;
using CodeMap.Query.Config;

namespace CodeMap.Tests;

/// <summary>
/// MCP M1: which indexed project a tool call is about, worked out by the server instead of asked of the client.
/// MCP roots are deprecated (SEP-2577) and VS Code does not send them, so the inputs are the tool's
/// <c>project</c> parameter, the server's <c>--workspace</c> argument and its working directory, in that order.
/// The registry is looked up from the workspace, never from an unrelated cwd, and the home-directory registry is
/// injected so the real ~/.codemap on the machine running the tests cannot leak in.
/// </summary>
[TestClass]
public class ProjectResolverTests
{
    [TestMethod] // the client picks the cwd; the configured workspace is what the user actually opened
    public void Workspace_argument_wins_over_the_working_directory()
    {
        var fx = new Fixture();
        var config = fx.Registry("", Project("shop", "shop/Shop.sln"), Project("blog", "blog/Blog.sln"));

        var resolved = fx.Resolver(config, workspace: fx.Dir("shop/Orders"), cwd: fx.Dir("blog")).Resolve(project: null);

        Assert.AreEqual("shop", resolved.Name);
        Assert.AreEqual(WorkspaceSource.WorkspaceArgument, resolved.Workspace.Source);
    }

    [TestMethod]
    public void Working_directory_is_the_workspace_when_no_argument_is_given()
    {
        var fx = new Fixture();
        var config = fx.Registry("", Project("shop", "shop/Shop.sln"), Project("blog", "blog/Blog.sln"));

        var resolved = fx.Resolver(config, cwd: fx.Dir("blog/Posts")).Resolve(project: null);

        Assert.AreEqual("blog", resolved.Name);
        Assert.AreEqual(WorkspaceSource.WorkingDirectory, resolved.Workspace.Source);
    }

    [TestMethod] // the parameter is how the model answers AMBIGUOUS_PROJECT or asks about another project on purpose
    public void Project_parameter_wins_over_the_workspace()
    {
        var fx = new Fixture();
        var config = fx.Registry("", Project("shop", "shop/Shop.sln"), Project("blog", "blog/Blog.sln"));

        var resolved = fx.Resolver(config, workspace: fx.Dir("shop")).Resolve(project: "BLOG");

        Assert.AreEqual("blog", resolved.Name);
        Assert.AreEqual(MatchRule.ProjectParameter, resolved.Rule);
    }

    [TestMethod] // a client that does not substitute variables passes "${workspaceFolder}" through literally
    public void Unexpanded_workspace_variable_is_ignored_with_a_warning()
    {
        var fx = new Fixture();
        var config = fx.Registry("", Project("shop", "shop/Shop.sln"));

        var resolved = fx.Resolver(config, workspace: "${workspaceFolder}", cwd: fx.Dir("shop")).Resolve(project: null);

        Assert.AreEqual("shop", resolved.Name);
        Assert.AreEqual(WorkspaceSource.WorkingDirectory, resolved.Workspace.Source);
        StringAssert.Contains(string.Join("\n", resolved.Workspace.Warnings), "${workspaceFolder}");
    }

    [TestMethod]
    public void Config_argument_wins_over_a_registry_above_the_workspace()
    {
        var fx = new Fixture();
        fx.Registry("", Project("near", "shop/Shop.sln"));
        var config = fx.Registry("config", Project("explicit", "../shop/Shop.sln"));

        Assert.AreEqual("explicit", fx.Resolver(config, workspace: fx.Dir("shop")).Resolve(project: null).Name);
    }

    [TestMethod]
    public void Registry_above_the_workspace_wins_over_the_home_registry()
    {
        var fx = new Fixture();
        fx.Registry("", Project("near", "shop/Shop.sln"));
        fx.HomeRegistry(Project("home", Path.Combine(fx.Root, "shop", "Shop.sln")));

        Assert.AreEqual("near", fx.Resolver(workspace: fx.Dir("shop/Orders")).Resolve(project: null).Name);
    }

    [TestMethod]
    public void Home_registry_is_the_fallback_when_none_is_above_the_workspace()
    {
        var fx = new Fixture();
        fx.HomeRegistry(Project("home", Path.Combine(fx.Root, "shop", "Shop.sln")));

        Assert.AreEqual("home", fx.Resolver(workspace: fx.Dir("shop/Orders")).Resolve(project: null).Name);
    }

    [TestMethod] // with --workspace given, a registry that only the cwd can see belongs to some other tree
    public void Registry_is_not_looked_up_from_the_working_directory_when_a_workspace_is_given()
    {
        var fx = new Fixture();
        fx.Registry("unrelated", Project("unrelated", "app/App.sln"));
        fx.HomeRegistry(Project("home", Path.Combine(fx.Root, "shop", "Shop.sln")));

        var resolved = fx.Resolver(workspace: fx.Dir("shop"), cwd: fx.Dir("unrelated/app")).Resolve(project: null);

        Assert.AreEqual("home", resolved.Name);
    }

    [TestMethod] // two solutions in one repo: the one whose folder holds the workspace is the one being asked about
    public void Solution_directory_beats_the_shared_repo_root()
    {
        var fx = new Fixture();
        fx.GitRepo("mono");
        var config = fx.Registry("", Project("orders", "mono/src/Orders/Orders.sln"), Project("tools", "mono/tools/Tools.sln"));

        var resolved = fx.Resolver(config, workspace: fx.Dir("mono/src/Orders/Api")).Resolve(project: null);

        Assert.AreEqual("orders", resolved.Name);
        Assert.AreEqual(MatchRule.InsideSolutionDir, resolved.Rule);
    }

    [TestMethod]
    public void Repo_root_beats_a_frontend_folder()
    {
        var fx = new Fixture();
        fx.GitRepo("web");
        var config = fx.Registry("",
            Project("web", "web/server/Web.sln"),
            Project("api", "api/Api.sln", frontend: "web/client"));

        var resolved = fx.Resolver(config, workspace: fx.Dir("web/client")).Resolve(project: null);

        Assert.AreEqual("web", resolved.Name);
        Assert.AreEqual(MatchRule.InsideRepoRoot, resolved.Rule);
    }

    [TestMethod]
    public void Workspace_inside_a_frontend_resolves_to_its_backend()
    {
        var fx = new Fixture();
        var config = fx.Registry("", Project("api", "api/Api.sln", frontend: "web"));

        var resolved = fx.Resolver(config, workspace: fx.Dir("web/src/app")).Resolve(project: null);

        Assert.AreEqual("api", resolved.Name);
        Assert.AreEqual(MatchRule.InsideFrontend, resolved.Rule);
    }

    [TestMethod] // the agent has to say which backend it is asking about, and the message has to say why
    public void Frontend_shared_by_two_backends_is_ambiguous()
    {
        var fx = new Fixture();
        var config = fx.Registry("",
            Project("orders-api", "orders/Orders.sln", frontend: "web", description: "Order service"),
            Project("billing-api", "billing/Billing.sln", frontend: "web"));

        var ex = Assert.ThrowsException<CodeMapToolException>(() => fx.Resolver(config, workspace: fx.Dir("web")).Resolve(project: null));

        Assert.AreEqual(CodeMapToolException.AmbiguousProject, ex.Code);
        StringAssert.Contains(ex.Message, "paired with 2 backends");
        StringAssert.Contains(ex.Message, "orders-api");
        StringAssert.Contains(ex.Message, "Order service");
        StringAssert.Contains(ex.Message, "billing-api");
        StringAssert.Contains(ex.Message, "project=");
    }

    [TestMethod] // e.g. VS Code opened on the folder that holds the repo
    public void Parent_folder_of_one_repo_resolves_to_it()
    {
        var fx = new Fixture();
        var config = fx.Registry("config", Project("shop", "../repos/shop/Shop.sln"));

        var resolved = fx.Resolver(config, workspace: fx.Dir("repos")).Resolve(project: null);

        Assert.AreEqual("shop", resolved.Name);
        Assert.AreEqual(MatchRule.ContainsRepoRoot, resolved.Rule);
    }

    [TestMethod]
    public void Parent_folder_of_two_repos_is_ambiguous()
    {
        var fx = new Fixture();
        var config = fx.Registry("config", Project("shop", "../repos/shop/Shop.sln"), Project("blog", "../repos/blog/Blog.sln"));

        var ex = Assert.ThrowsException<CodeMapToolException>(() => fx.Resolver(config, workspace: fx.Dir("repos")).Resolve(project: null));

        Assert.AreEqual(CodeMapToolException.AmbiguousProject, ex.Code);
        StringAssert.Contains(ex.Message, "shop");
        StringAssert.Contains(ex.Message, "blog");
    }

    // RepoOf defaults to the solution's directory, src/ here, so a workspace in docs/ would match nothing and the
    // repo root handed to the banner and to slice would be src/. git knows the real root.
    [TestMethod]
    public void Repo_root_comes_from_git_not_from_the_solution_directory()
    {
        var fx = new Fixture();
        var repo = fx.GitRepo("shop");
        var config = fx.Registry("", Project("shop", "shop/src/Shop.sln"));

        var resolved = fx.Resolver(config, workspace: fx.Dir("shop/docs")).Resolve(project: null);

        Assert.AreEqual("shop", resolved.Name);
        Assert.AreEqual(MatchRule.InsideRepoRoot, resolved.Rule);
        Assert.AreEqual(Path.GetFullPath(repo), resolved.RepoRoot, ignoreCase: OperatingSystem.IsWindows());
    }

    [TestMethod] // the parameter path must hand out the same repo root as the workspace path
    public void Project_parameter_also_gets_the_git_repo_root()
    {
        var fx = new Fixture();
        var repo = fx.GitRepo("shop");
        var config = fx.Registry("", Project("shop", "shop/src/Shop.sln"));

        var resolved = fx.Resolver(config, workspace: fx.Dir("elsewhere")).Resolve(project: "shop");

        Assert.AreEqual(Path.GetFullPath(repo), resolved.RepoRoot, ignoreCase: OperatingSystem.IsWindows());
        Assert.AreEqual(Path.Combine(fx.Root, "out", "shop", "index"), resolved.IndexDir, ignoreCase: OperatingSystem.IsWindows());
    }

    [TestMethod] // what a hand-typed --workspace or a client's path spelling looks like on Windows
    public void Windows_paths_match_regardless_of_case_separators_and_trailing_slash()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Case-insensitive paths are a Windows rule.");

        var fx = new Fixture();
        var config = fx.Registry("", Project("shop", "shop/Shop.sln"));
        var spelled = fx.Dir("shop/Orders").ToUpperInvariant().Replace('\\', '/') + "/";

        var resolved = fx.Resolver(config, workspace: spelled).Resolve(project: null);

        Assert.AreEqual("shop", resolved.Name);
        Assert.AreEqual(WorkspaceSource.WorkspaceArgument, resolved.Workspace.Source);
    }

    [TestMethod]
    public void Folder_whose_name_merely_starts_with_a_repo_name_is_not_inside_it()
    {
        var fx = new Fixture();
        fx.Dir("shop");
        var config = fx.Registry("", Project("shop", "shop/Shop.sln"));

        var ex = Assert.ThrowsException<CodeMapToolException>(() => fx.Resolver(config, workspace: fx.Dir("shop-admin")).Resolve(project: null));

        Assert.AreEqual(CodeMapToolException.NotIndexed, ex.Code);
    }

    [TestMethod] // a tools solution nested inside the main solution's folder is the more specific match
    public void Deepest_solution_directory_wins_among_nested_solutions()
    {
        var fx = new Fixture();
        var config = fx.Registry("", Project("app", "app/App.sln"), Project("tools", "app/build/tools/Tools.sln"));

        Assert.AreEqual("tools", fx.Resolver(config, workspace: fx.Dir("app/build/tools/Gen")).Resolve(project: null).Name);
        Assert.AreEqual("app", fx.Resolver(config, workspace: fx.Dir("app/Orders")).Resolve(project: null).Name);
    }

    [TestMethod] // the server lives as long as the editor; editing the registry must not need a restart
    public void Registry_edits_are_picked_up_without_a_restart()
    {
        var fx = new Fixture();
        var config = fx.Registry("", Project("shop", "shop/Shop.sln"));
        var resolver = fx.Resolver(config, workspace: fx.Dir("shop"));
        Assert.AreEqual("shop", resolver.Resolve(project: null).Name);

        fx.Registry("", Project("store", "shop/Shop.sln"));
        File.SetLastWriteTimeUtc(config, File.GetLastWriteTimeUtc(config).AddSeconds(5));

        Assert.AreEqual("store", resolver.Resolve(project: null).Name);
    }

    [TestMethod] // the message is all the model gets: it must be able to tell what was tried and what to do next
    public void Unmatched_workspace_says_what_was_tried_and_how_to_fix_it()
    {
        var fx = new Fixture();
        var config = fx.Registry("", Project("shop", "shop/Shop.sln"), Project("blog", "blog/Blog.sln"));
        var cwd = fx.Dir("elsewhere");

        var ex = Assert.ThrowsException<CodeMapToolException>(() => fx.Resolver(config, cwd: cwd).Resolve(project: null));

        Assert.AreEqual(CodeMapToolException.NotIndexed, ex.Code);
        StringAssert.StartsWith(ex.Message, "NOT_INDEXED: ");
        StringAssert.Contains(ex.Message, cwd);
        StringAssert.Contains(ex.Message, "(from cwd)");
        StringAssert.Contains(ex.Message, config);
        StringAssert.Contains(ex.Message, "shop, blog");
        StringAssert.Contains(ex.Message, "project=");
        StringAssert.Contains(ex.Message, "--workspace"); // the cwd is the client's choice, so suggest configuring it
    }

    [TestMethod]
    public void Unknown_project_parameter_lists_the_indexed_projects()
    {
        var fx = new Fixture();
        var config = fx.Registry("", Project("shop", "shop/Shop.sln"), Project("blog", "blog/Blog.sln"));

        var ex = Assert.ThrowsException<CodeMapToolException>(() => fx.Resolver(config, workspace: fx.Dir("shop")).Resolve(project: "wiki"));

        Assert.AreEqual(CodeMapToolException.NotIndexed, ex.Code);
        StringAssert.Contains(ex.Message, "'wiki'");
        StringAssert.Contains(ex.Message, "shop, blog");
    }

    [TestMethod]
    public void No_registry_anywhere_is_not_indexed_and_names_the_places_searched()
    {
        var fx = new Fixture();

        var ex = Assert.ThrowsException<CodeMapToolException>(() => fx.Resolver(workspace: fx.Dir("shop")).Resolve(project: null));

        Assert.AreEqual(CodeMapToolException.NotIndexed, ex.Code);
        StringAssert.Contains(ex.Message, ProjectRegistry.FileName);
        StringAssert.Contains(ex.Message, "~/.codemap");
    }

    [TestMethod]
    public void Broken_registry_file_is_reported_as_unreadable()
    {
        var fx = new Fixture();
        var config = Path.Combine(fx.Root, ProjectRegistry.FileName);
        File.WriteAllText(config, "{ \"projects\": [ ");

        var ex = Assert.ThrowsException<CodeMapToolException>(() => fx.Resolver(config, workspace: fx.Dir("shop")).Resolve(project: null));

        Assert.AreEqual(CodeMapToolException.RegistryUnreadable, ex.Code);
        StringAssert.Contains(ex.Message, config);
    }

    private static ProjectJson Project(string name, string solution, string? frontend = null, string? description = null)
        => new(name, description, solution, $"out/{name}", frontend);

    private sealed record ProjectJson(string Name, string? Description, string Solution, string Output, string? Frontend);

    /// <summary>A temp tree to lay repos and registries out in, plus an empty fake home directory.</summary>
    private sealed class Fixture
    {
        private static readonly JsonSerializerOptions Json = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public string Root { get; } = TestPaths.NewTempDir();

        private string Home => Path.Combine(Root, "home");

        public string Dir(string relative)
        {
            var path = Path.GetFullPath(Path.Combine(Root, relative));
            Directory.CreateDirectory(path);
            return path;
        }

        public string GitRepo(string relative)
        {
            var dir = Dir(relative);
            GitFixtureHelper.InitInPlace(dir);
            return dir;
        }

        /// <summary>
        /// Writes codemap.projects.json into <paramref name="relativeDir"/>; entry paths are relative to it, as in a
        /// real registry. Creates each solution's folder too: git is only asked for a repo root that exists.
        /// </summary>
        public string Registry(string relativeDir, params ProjectJson[] projects)
        {
            var dir = Dir(relativeDir);
            foreach (var p in projects)
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(Path.Combine(dir, p.Solution)))!);

            var path = Path.Combine(dir, ProjectRegistry.FileName);
            File.WriteAllText(path, JsonSerializer.Serialize(new { projects }, Json));
            return path;
        }

        public void HomeRegistry(params ProjectJson[] projects) => Registry(Path.Combine("home", ".codemap"), projects);

        public ProjectResolver Resolver(string? config = null, string? workspace = null, string? cwd = null)
            => new(config, workspace, cwd ?? Dir("cwd"), Home);
    }
}
