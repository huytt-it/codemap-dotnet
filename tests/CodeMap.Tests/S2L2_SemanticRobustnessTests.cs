using System.Diagnostics;
using CodeMap.Query.Json;
using CodeMap.Query.Models;
using CodeMap.Roslyn.Scan;

namespace CodeMap.Tests;

/// <summary>
/// Phase 2 robustness (spec section 5): "nếu một project load lỗi, KHÔNG được sập" — a project MSBuildWorkspace
/// can't load must fall back to L1 and get recorded in diagnostics, while a sibling project that DOES load still
/// gets the full L2 treatment. One scenario only (L2 runs are comparatively slow — cold MSBuild/BuildHost start).
/// </summary>
[TestClass]
public class S2L2_SemanticRobustnessTests
{
    [TestMethod]
    public void A_project_with_a_malformed_csproj_falls_back_to_L1_without_crashing_a_sibling_project()
    {
        L2TestSetup.EnsureFixtureRestored(); // also registers MSBuildLocator in this test process, see L2TestSetup

        var dir = TestPaths.NewTempDir();

        var goodDir = Path.Combine(dir, "Good");
        Directory.CreateDirectory(goodDir);
        File.WriteAllText(Path.Combine(goodDir, "Good.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(goodDir, "GoodClass.cs"), "namespace Good;\npublic class GoodClass { public void M() {} }\n");

        var brokenDir = Path.Combine(dir, "Broken");
        Directory.CreateDirectory(brokenDir);
        // A non-existent SDK, not malformed XML: MSBuildWorkspace tolerates plain XML errors surprisingly well
        // (it can end up with an empty-but-"loaded" project instead of failing outright), but an unresolvable
        // Sdk genuinely fails MSBuild evaluation — which is the scenario spec section 5's fallback rule targets.
        File.WriteAllText(Path.Combine(brokenDir, "Broken.csproj"),
            "<Project Sdk=\"Totally.Fake.Sdk.Does.Not.Exist/1.0.0\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(brokenDir, "BrokenClass.cs"), "namespace Broken;\npublic class BrokenClass { public void M() {} }\n");

        var slnPath = Path.Combine(dir, "Mixed.sln");
        File.WriteAllText(slnPath,
            "\r\nMicrosoft Visual Studio Solution File, Format Version 12.00\r\n# Visual Studio Version 17\r\n" +
            "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"Good\", \"Good\\Good.csproj\", \"{11111111-1111-1111-1111-111111111111}\"\r\nEndProject\r\n" +
            "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"Broken\", \"Broken\\Broken.csproj\", \"{22222222-2222-2222-2222-222222222222}\"\r\nEndProject\r\n" +
            "Global\r\nEndGlobal\r\n");

        // Restore ONLY the good project directly — restoring at the solution level would abort entirely once
        // MSBuild tries (and fails) to evaluate the malformed Broken.csproj, leaving Good un-restored too.
        RestoreQuietly(goodDir);

        var outDir = TestPaths.NewTempDir();
        var ex = TestAssert.RecordException(() => new SemanticScanner(includeExternal: false).Scan(slnPath, outDir));
        Assert.IsNull(ex);

        var symbols = JsonlReader.Read<SymbolRecord>(Path.Combine(outDir, "index", "symbols.jsonl"));
        TestAssert.Contains(symbols, s => s.Name == "GoodClass"); // the good project still got fully processed

        var diagnostics = JsonUtil.ReadFile<DiagnosticsModel>(Path.Combine(outDir, "index", "diagnostics.json"));
        TestAssert.Contains(diagnostics!.DegradedProjects, d => d.Project == "Broken");

        // The fallback pool recovers Broken's own files too (via our independent glob, not MSBuild's) — L1-quality but not silently dropped.
        TestAssert.Contains(symbols, s => s.Name == "BrokenClass");
    }

    private static void RestoreQuietly(string solutionDir)
    {
        var psi = new ProcessStartInfo("dotnet", "restore")
        {
            WorkingDirectory = solutionDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(psi)!;
        process.WaitForExit(60_000);
        // Restore is expected to fail for the Broken project (malformed XML) — that's the point of this test.
        // We only need the Good project to have a valid obj/project.assets.json.
    }
}
