using System.Text.Json;
using CodeMap.Query.Config;
using CodeMap.Query.Git;

namespace CodeMap.Tests;

/// <summary>
/// A registered project built by hand, for tests that run the real `impact`/`slice` commands: a git repo, an
/// index as of its HEAD, and a codemap.projects.json that points at both. Hand-written rather than scanned so a
/// test can say exactly what the index claims and what is (or is not) on disk.
/// </summary>
internal sealed record RegisteredProject(string ConfigPath, string Repo, string IndexDir, string SolutionDir)
{
    public const string TargetId = "M:Orders.Svc.Cancel";
    public const string CallerId = "M:Orders.Ctl.Post";
    public const string SharedId = "M:Shared.Util.Helper";
    public const string EscapingId = "M:Evil.Secret.Leak";
    public const string LeakMarker = "THIS-TEXT-LIVES-OUTSIDE-THE-REPO";

    /// <summary>The unedited text of Orders/Svc.cs; Cancel is on line 4.</summary>
    public const string SvcSource = "namespace Orders;\npublic class Svc\n{\n    public void Cancel() { }\n}\n";

    /// <summary>Absolute path of a file named the way symbols.jsonl names it (relative to the solution directory).</summary>
    public string SolutionFile(string solutionRelativePath) => Path.GetFullPath(Path.Combine(SolutionDir, solutionRelativePath));

    /// <summary>
    /// root/repo (git) with the solution in <paramref name="solutionDir"/> ("src", or "" for the repo root),
    /// root/out/index (the index, as of HEAD), root/config/codemap.projects.json, and root/outside/Evil.cs next
    /// to — not inside — the repo. After the commit Svc.cs is edited without committing, so a correct banner has
    /// something to report.
    /// </summary>
    public static RegisteredProject Build(string solutionDir, bool explicitRepo)
    {
        var root = TestPaths.NewTempDir();
        var repo = Path.Combine(root, "repo");
        Directory.CreateDirectory(repo);
        GitFixtureHelper.InitInPlace(repo);

        string InSolution(string file) => solutionDir.Length == 0 ? file : $"{solutionDir}/{file}";
        GitFixtureHelper.Commit(repo, "initial",
            (InSolution("App.sln"), "Microsoft Visual Studio Solution File"),
            (InSolution("Orders/Svc.cs"), SvcSource),
            (InSolution("Orders/Ctl.cs"), "namespace Orders;\npublic class Ctl\n{\n    public void Post() { }\n}\n"),
            ("Shared/Util.cs", "namespace Shared;\npublic class Util\n{\n    public void Helper() { }\n}\n"));
        var head = GitRepoInfo.TryGetHeadCommit(repo)!;
        File.WriteAllText(Path.Combine(repo, InSolution("Orders/Svc.cs")), SvcSource.Replace("Cancel() { }", "Cancel() { var edited = 1; }"));

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

        return new RegisteredProject(configPath, Path.GetFullPath(repo), Path.GetFullPath(indexDir), Path.GetFullPath(Path.Combine(repo, solutionDir)));
    }

    public delegate int CommandRun(string[] args);

    /// <summary>Runs a real command with the process cwd set to <paramref name="cwd"/>, writing through --out so nothing depends on capturing the console.</summary>
    public static string RunCommand(CommandRun command, string cwd, params string[] args)
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

    public static object Symbol(string id, string name, string containingType, string file, int line, string kind = "Method") => new
    {
        id,
        kind,
        name,
        containingType,
        project = "Orders",
        file,
        line,
        accessibility = "Public",
    };

    public static void WriteJsonl(string path, params object[] rows)
        => File.WriteAllLines(path, rows.Select(r => JsonSerializer.Serialize(r)));
}
