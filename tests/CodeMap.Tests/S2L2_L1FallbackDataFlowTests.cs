using System.Diagnostics;
using CodeMap.Query.Json;
using CodeMap.Query.Models;
using CodeMap.Roslyn.Scan;

namespace CodeMap.Tests;

/// <summary>
/// A project MSBuildWorkspace can't load falls back to L1 (spec section 5). That fallback must still feed
/// impact: its own calls, its calls INTO the projects that did load (the usual shape — a broken Web project
/// calling a healthy Core one), and its entry points. Healthy projects are bound against but not re-emitted.
/// </summary>
[TestClass]
public class S2L2_L1FallbackDataFlowTests
{
    private static List<SymbolRecord> _symbols = null!;
    private static List<EdgeRecord> _edges = null!;
    private static List<EntryPoint> _entryPoints = null!;
    private static DiagnosticsModel _diagnostics = null!;

    [ClassInitialize]
    public static void ClassInit(TestContext _)
    {
        L2TestSetup.EnsureFixtureRestored(); // also registers MSBuildLocator in this test process, see L2TestSetup

        var dir = TestPaths.NewTempDir();

        var goodDir = Path.Combine(dir, "Good");
        Directory.CreateDirectory(goodDir);
        File.WriteAllText(Path.Combine(goodDir, "Good.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(goodDir, "GoodService.cs"),
            "namespace Good;\n" +
            "public interface IGoodService { void Do(int count); }\n" +
            "public class GoodService : IGoodService { public void Do(int count) {} }\n");

        var brokenDir = Path.Combine(dir, "Broken");
        Directory.CreateDirectory(brokenDir);
        // Unresolvable Sdk: genuinely fails MSBuild evaluation (see S2L2_SemanticRobustnessTests for why not bad XML).
        File.WriteAllText(Path.Combine(brokenDir, "Broken.csproj"),
            "<Project Sdk=\"Totally.Fake.Sdk.Does.Not.Exist/1.0.0\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>" +
            "<ItemGroup><ProjectReference Include=\"..\\Good\\Good.csproj\" /></ItemGroup></Project>");
        File.WriteAllText(Path.Combine(brokenDir, "BrokenCaller.cs"),
            "using Good;\n" +
            "namespace Broken;\n" +
            "public class BrokenCaller : GoodService\n" +
            "{\n" +
            "    public void Run(IGoodService service) { service.Do(1); Local(); }\n" +
            "    private void Local() {}\n" +
            "}\n" +
            "[Route(\"api/[controller]\")]\n" +
            "public class WidgetsController : ControllerBase\n" +
            "{\n" +
            "    [HttpGet(\"{id}\")]\n" +
            "    public void Get(int id) {}\n" +
            "}\n");

        var slnPath = Path.Combine(dir, "Mixed.sln");
        File.WriteAllText(slnPath,
            "\r\nMicrosoft Visual Studio Solution File, Format Version 12.00\r\n# Visual Studio Version 17\r\n" +
            "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"Good\", \"Good\\Good.csproj\", \"{11111111-1111-1111-1111-111111111111}\"\r\nEndProject\r\n" +
            "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"Broken\", \"Broken\\Broken.csproj\", \"{22222222-2222-2222-2222-222222222222}\"\r\nEndProject\r\n" +
            "Global\r\nEndGlobal\r\n");

        RestoreQuietly(goodDir); // Broken can't restore (fake Sdk), and a solution-level restore would abort on it

        var outDir = TestPaths.NewTempDir();
        new SemanticScanner(includeExternal: false).Scan(slnPath, outDir);

        var indexDir = Path.Combine(outDir, "index");
        _symbols = JsonlReader.Read<SymbolRecord>(Path.Combine(indexDir, "symbols.jsonl"));
        _edges = JsonlReader.Read<EdgeRecord>(Path.Combine(indexDir, "edges.jsonl"));
        _entryPoints = JsonUtil.ReadFile<List<EntryPoint>>(Path.Combine(indexDir, "entrypoints.json")) ?? new();
        _diagnostics = JsonUtil.ReadFile<DiagnosticsModel>(Path.Combine(indexDir, "diagnostics.json"))!;
    }

    [TestMethod]
    public void The_broken_project_really_fell_back()
    {
        TestAssert.Contains(_diagnostics.DegradedProjects, d => d.Project == "Broken");
        TestAssert.DoesNotContain(_diagnostics.DegradedProjects, d => d.Project == "Good");
    }

    [TestMethod]
    public void Degraded_project_emits_its_own_call_edges()
    {
        TestAssert.Contains(_edges, e => e.From == "M:Broken.BrokenCaller.Run(Good.IGoodService)" && e.To == "M:Broken.BrokenCaller.Local" && e.Kind == "call");
    }

    [TestMethod] // the cross-project case: the target is declared in a project that loaded at L2
    public void Degraded_project_call_into_a_healthy_project_binds_to_the_L2_symbol()
    {
        const string from = "M:Broken.BrokenCaller.Run(Good.IGoodService)";
        TestAssert.Contains(_edges, e => e.From == from && e.To == "M:Good.IGoodService.Do(System.Int32)" && e.Kind == "call");
        TestAssert.Contains(_edges, e => e.From == from && e.To == "M:Good.GoodService.Do(System.Int32)" && e.Via == "interface");
        TestAssert.Contains(_edges, e => e.From == "T:Broken.BrokenCaller" && e.To == "T:Good.GoodService" && e.Kind == "inherits");
    }

    [TestMethod]
    public void Healthy_project_symbols_are_not_emitted_twice()
    {
        Assert.AreEqual(1, _symbols.Count(s => s.Id == "M:Good.GoodService.Do(System.Int32)"));
        Assert.AreEqual(0, _diagnostics.DuplicateDocIdsAcrossProjects.Count);
    }

    [TestMethod]
    public void Degraded_project_controller_action_is_an_http_entry_point()
    {
        var ep = TestAssert.Single(_entryPoints, e => e.Id == "M:Broken.WidgetsController.Get(System.Int32)");
        Assert.AreEqual("GET", ep.HttpMethod);
        Assert.AreEqual("api/widgets/{id}", ep.Route);
    }

    private static void RestoreQuietly(string projectDir)
    {
        var psi = new ProcessStartInfo("dotnet", "restore")
        {
            WorkingDirectory = projectDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(psi)!;
        process.WaitForExit(60_000);
    }
}
