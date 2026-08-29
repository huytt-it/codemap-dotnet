using System.Text;
using CodeMap.Query.Json;
using CodeMap.Query.Models;
using CodeMap.Roslyn.Scan;

namespace CodeMap.Tests;

/// <summary>Tier S2 (docs/TEST-PLAN.md): does it crash on garbage input. Each test builds its own broken fixture in a temp dir.</summary>
[TestClass]
public class S2_RobustnessTests
{
    private const string SlnHeader =
        "\r\nMicrosoft Visual Studio Solution File, Format Version 12.00\r\n" +
        "# Visual Studio Version 17\r\n";

    [TestMethod] // S2.1 — .sln points at a .csproj that doesn't exist
    public void Missing_project_does_not_crash_scan()
    {
        var dir = TestPaths.NewTempDir();
        var slnPath = Path.Combine(dir, "Broken.sln");
        File.WriteAllText(slnPath, SlnHeader +
            "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"Missing\", \"Missing\\Missing.csproj\", \"{11111111-1111-1111-1111-111111111111}\"\r\nEndProject\r\n" +
            "Global\r\nEndGlobal\r\n");

        var outDir = Path.Combine(dir, "out");
        var ex = TestAssert.RecordException(() => new SyntaxOnlyScanner(false).Scan(slnPath, outDir));

        Assert.IsNull(ex);
        var diag = JsonUtil.ReadFile<DiagnosticsModel>(Path.Combine(outDir, "index", "diagnostics.json"));
        Assert.IsNotNull(diag);
        Assert.AreEqual(1, diag!.DegradedProjects.Count);
        StringAssert.Contains(diag.DegradedProjects[0].Project, "Missing");
    }

    [TestMethod] // S2.2 — .csproj is malformed XML
    public void Malformed_csproj_xml_does_not_crash_scan()
    {
        var dir = TestPaths.NewTempDir();
        var projDir = Path.Combine(dir, "Broken");
        Directory.CreateDirectory(projDir);
        var csprojPath = Path.Combine(projDir, "Broken.csproj");
        File.WriteAllText(csprojPath, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup NOT CLOSED");

        var slnPath = Path.Combine(dir, "Broken.sln");
        File.WriteAllText(slnPath, SlnHeader +
            "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"Broken\", \"Broken\\Broken.csproj\", \"{11111111-1111-1111-1111-111111111111}\"\r\nEndProject\r\n" +
            "Global\r\nEndGlobal\r\n");

        var outDir = Path.Combine(dir, "out");
        var ex = TestAssert.RecordException(() => new SyntaxOnlyScanner(false).Scan(slnPath, outDir));

        Assert.IsNull(ex);
        var diag = JsonUtil.ReadFile<DiagnosticsModel>(Path.Combine(outDir, "index", "diagnostics.json"));
        Assert.AreEqual(1, diag!.DegradedProjects.Count);
    }

    [TestMethod] // S2.3 — a solution folder + a non-C# project in the .sln must be skipped quietly, no crash
    public void Solution_folder_and_non_csharp_project_are_skipped()
    {
        var dir = TestPaths.NewTempDir();
        var slnPath = Path.Combine(dir, "Mixed.sln");
        File.WriteAllText(slnPath, SlnHeader +
            "Project(\"{2150E333-8FDC-42A3-9474-1A3956D46DE8}\") = \"SolutionItems\", \"SolutionItems\", \"{22222222-2222-2222-2222-222222222222}\"\r\nEndProject\r\n" +
            "Project(\"{F184B08F-C81C-45F6-A57F-5ABD9991F28F}\") = \"VbProj\", \"VbProj\\VbProj.vbproj\", \"{33333333-3333-3333-3333-333333333333}\"\r\nEndProject\r\n" +
            "Global\r\nEndGlobal\r\n");

        var outDir = Path.Combine(dir, "out");
        var ex = TestAssert.RecordException(() => new SyntaxOnlyScanner(false).Scan(slnPath, outDir));

        Assert.IsNull(ex);
        var diag = JsonUtil.ReadFile<DiagnosticsModel>(Path.Combine(outDir, "index", "diagnostics.json"));
        // No fake degradedProjects entry for a solution folder / a project in another language — they're
        // filtered out at the .sln parsing step, never treated as a "broken C# project".
        Assert.AreEqual(0, diag!.DegradedProjects.Count);
    }

    [TestMethod] // S2.4 — a .cs file with severe syntax errors still yields whatever parses, no crash
    public void File_with_syntax_errors_still_yields_the_parsable_part()
    {
        var (dir, slnPath) = SingleProjectFixture("Bad", ("Bad.cs",
            "public class GoodOne { public void M() {} }\n" +
            "this is not even close to valid C# @#$%^&& class class {{{ )))\n"));

        var outDir = Path.Combine(dir, "out");
        var ex = TestAssert.RecordException(() => new SyntaxOnlyScanner(false).Scan(slnPath, outDir));
        Assert.IsNull(ex);

        var symbols = JsonlReader.Read<SymbolRecord>(Path.Combine(outDir, "index", "symbols.jsonl"));
        TestAssert.Contains(symbols, s => s.Name == "GoodOne");
    }

    [TestMethod] // S2.5 — a UTF-8 BOM file with non-ASCII (Vietnamese) content doesn't lose symbols or crash
    public void Utf8_bom_file_with_non_ascii_content_does_not_crash()
    {
        // The payload is deliberately Vietnamese text — that's the point of this test (diacritics + BOM handling).
        var (dir, slnPath) = SingleProjectFixture("Vn", true, ("KhoHang.cs",
            "// Xử lý đơn hàng đã huỷ, không được để mất dấu\n" +
            "namespace KhoHang;\n" +
            "public class DonHangDaHuy { public void XoaBo() {} }\n"));

        var outDir = Path.Combine(dir, "out");
        var ex = TestAssert.RecordException(() => new SyntaxOnlyScanner(false).Scan(slnPath, outDir));
        Assert.IsNull(ex);

        var symbols = JsonlReader.Read<SymbolRecord>(Path.Combine(outDir, "index", "symbols.jsonl"));
        TestAssert.Contains(symbols, s => s.Name == "DonHangDaHuy");
        TestAssert.Contains(symbols, s => s.Name == "XoaBo");
    }

    [TestMethod] // S2.6 — an empty solution (0 projects) doesn't crash, output is still valid
    public void Solution_with_0_projects_does_not_crash()
    {
        var dir = TestPaths.NewTempDir();
        var slnPath = Path.Combine(dir, "Empty.sln");
        File.WriteAllText(slnPath, SlnHeader + "Global\r\nEndGlobal\r\n");

        var outDir = Path.Combine(dir, "out");
        var ex = TestAssert.RecordException(() => new SyntaxOnlyScanner(false).Scan(slnPath, outDir));
        Assert.IsNull(ex);

        var symbols = JsonlReader.Read<SymbolRecord>(Path.Combine(outDir, "index", "symbols.jsonl"));
        Assert.AreEqual(0, symbols.Count);
    }

    [TestMethod] // S2.7 — a base type outside the solution (simulated NuGet type) must land in diagnostics, never a guessed edge
    public void Base_type_outside_solution_goes_to_diagnostics_not_a_guess()
    {
        var (dir, slnPath) = SingleProjectFixture("Ext", ("Ext.cs",
            "namespace Ext;\n" +
            "public class MyRepo : SomeNuGetBaseRepository { }\n"));

        var outDir = Path.Combine(dir, "out");
        new SyntaxOnlyScanner(includeExternal: false).Scan(slnPath, outDir);

        var edges = JsonlReader.Read<EdgeRecord>(Path.Combine(outDir, "index", "edges.jsonl"));
        TestAssert.DoesNotContain(edges, e => e.From == "T:Ext.MyRepo");

        var diag = JsonUtil.ReadFile<DiagnosticsModel>(Path.Combine(outDir, "index", "diagnostics.json"));
        TestAssert.Contains(diag!.UnresolvedInheritance, u => u.BaseTypeName == "SomeNuGetBaseRepository");
    }

    [TestMethod] // S2.8 — a partial class spread across files: a shared docId is legitimate (multiple declaration sites), doesn't break map
    public void Partial_class_across_files_is_not_treated_as_an_error()
    {
        var (dir, slnPath) = SingleProjectFixture("Pt",
            ("Part1.cs", "namespace Pt;\npublic partial class Shared { public void A() {} }\n"),
            ("Part2.cs", "namespace Pt;\npublic partial class Shared { public void B() {} }\n"));

        var outDir = Path.Combine(dir, "out");
        new SyntaxOnlyScanner(false).Scan(slnPath, outDir);

        var symbols = JsonlReader.Read<SymbolRecord>(Path.Combine(outDir, "index", "symbols.jsonl"));
        var sharedDecls = symbols.Where(s => s.Id == "T:Pt.Shared").ToList();
        Assert.AreEqual(2, sharedDecls.Count); // 2 declaration sites sharing one docId — correct per Roslyn, not a bug
        TestAssert.Contains(sharedDecls, s => s.File == "Pt/Part1.cs");
        TestAssert.Contains(sharedDecls, s => s.File == "Pt/Part2.cs");

        // Must NOT land in duplicateDocIdsAcrossProjects — that's reserved for genuine CROSS-PROJECT collisions.
        var diag = JsonUtil.ReadFile<DiagnosticsModel>(Path.Combine(outDir, "index", "diagnostics.json"));
        Assert.AreEqual(0, diag!.DuplicateDocIdsAcrossProjects.Count);
    }

    /// <summary>Quickly builds 1 solution + 1 SDK-style project + N .cs files in a temp dir, returns (dir, slnPath).</summary>
    private static (string Dir, string SlnPath) SingleProjectFixture(string projectName, params (string FileName, string Content)[] files)
        => SingleProjectFixture(projectName, false, files);

    private static (string Dir, string SlnPath) SingleProjectFixture(string projectName, bool useBom, params (string FileName, string Content)[] files)
    {
        var dir = TestPaths.NewTempDir();
        var projDir = Path.Combine(dir, projectName);
        Directory.CreateDirectory(projDir);

        File.WriteAllText(Path.Combine(projDir, $"{projectName}.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");

        var encoding = useBom ? new UTF8Encoding(true) : new UTF8Encoding(false);
        foreach (var (fileName, content) in files)
            File.WriteAllText(Path.Combine(projDir, fileName), content, encoding);

        var slnPath = Path.Combine(dir, $"{projectName}.sln");
        File.WriteAllText(slnPath, SlnHeader +
            $"Project(\"{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}\") = \"{projectName}\", \"{projectName}\\{projectName}.csproj\", \"{{11111111-1111-1111-1111-111111111111}}\"\r\nEndProject\r\n" +
            "Global\r\nEndGlobal\r\n");

        return (dir, slnPath);
    }
}
