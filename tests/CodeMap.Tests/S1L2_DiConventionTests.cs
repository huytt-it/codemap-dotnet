using System.Diagnostics;
using CodeMap.Query.Json;
using CodeMap.Query.Models;
using CodeMap.Roslyn.Scan;

namespace CodeMap.Tests;

/// <summary>
/// P10 (spec section 5, "Trích DI theo attribute"): a class marked with the configured attribute binds to the
/// ONE real interface it implements, self-registers if it implements none, and is flagged (never guessed) if it
/// implements 2+. Fixture: tests/Fixtures/SampleSolution/Orders.Core/DiConventionScenarios.cs + codemap.config.json.
/// </summary>
[TestClass]
public class S1L2_DiConventionTests
{
    private static Dictionary<string, List<string>> _di = null!;
    private static DiagnosticsModel _diagnostics = null!;

    [ClassInitialize]
    public static void ClassInit(TestContext _)
    {
        L2TestSetup.EnsureFixtureRestored();

        var outDir = TestPaths.NewTempDir();
        new SemanticScanner(includeExternal: false).Scan(TestPaths.FixtureSolution, outDir);

        _di = JsonUtil.ReadFile<Dictionary<string, List<string>>>(Path.Combine(outDir, "index", "di.json")) ?? new();
        _diagnostics = JsonUtil.ReadFile<DiagnosticsModel>(Path.Combine(outDir, "index", "diagnostics.json"))!;
    }

    [TestMethod]
    public void Type_implementing_exactly_one_real_interface_binds_to_it()
    {
        Assert.IsTrue(_di.TryGetValue("T:Orders.IPricingService", out var impls));
        CollectionAssert.Contains(impls, "T:Orders.PricingService");
    }

    [TestMethod]
    public void Type_implementing_zero_interfaces_self_registers()
    {
        Assert.IsTrue(_di.TryGetValue("T:Orders.AuditLogger", out var impls));
        CollectionAssert.AreEqual(new[] { "T:Orders.AuditLogger" }, impls);
    }

    [TestMethod]
    public void Empty_marker_interface_does_not_count_as_real_so_type_self_registers()
    {
        Assert.IsTrue(_di.TryGetValue("T:Orders.TaggedOnly", out var impls));
        CollectionAssert.Contains(impls, "T:Orders.TaggedOnly");
    }

    [TestMethod]
    public void Type_implementing_two_real_interfaces_is_flagged_ambiguous_not_guessed()
    {
        var entry = _diagnostics.AmbiguousDiTypes.Single(a => a.TypeDocId == "T:Orders.ReportGenerator");
        CollectionAssert.AreEquivalent(
            new[] { "T:Orders.IExportable", "T:Orders.IPricingService" }, entry.CandidateInterfaces);
    }

    [TestMethod]
    public void Attribute_and_fluent_sources_disagreeing_is_flagged_as_conflict()
    {
        var conflict = _diagnostics.DiRegistrationConflicts.Single(c => c.TypeDocId == "T:Orders.EmailNotifier");
        Assert.AreEqual("T:Orders.INotifier", conflict.AttributeBoundInterface);
        CollectionAssert.Contains(conflict.FluentBoundInterfaces, "T:Orders.NotifierBase");
    }

    [TestMethod]
    public void Manual_override_resolves_ambiguity_and_clears_the_diagnostic()
    {
        var repoDir = TestPaths.NewTempDir();
        File.WriteAllText(Path.Combine(repoDir, "P10Override.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(repoDir, "Types.cs"), """
            using System;
            namespace Ov;

            [AttributeUsage(AttributeTargets.Class)]
            public sealed class InjectableAttribute : Attribute { }

            public interface IFoo { void Foo(); }
            public interface IBar { void Bar(); }

            [Injectable]
            public class Both : IFoo, IBar
            {
                public void Foo() { }
                public void Bar() { }
            }
            """);
        File.WriteAllText(Path.Combine(repoDir, "codemap.config.json"), """
            { "diAttribute": "InjectableAttribute", "diManualOverrides": { "T:Ov.Both": "T:Ov.IBar" } }
            """);

        var slnPath = Path.Combine(repoDir, "P10Override.sln");
        File.WriteAllText(slnPath,
            "\r\nMicrosoft Visual Studio Solution File, Format Version 12.00\r\n# Visual Studio Version 17\r\n" +
            "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"P10Override\", \"P10Override.csproj\", \"{11111111-1111-1111-1111-111111111111}\"\r\nEndProject\r\n" +
            "Global\r\nEndGlobal\r\n");

        RestoreQuietly(repoDir);
        L2TestSetup.EnsureFixtureRestored(); // registers MSBuildLocator in this test process

        var outDir = TestPaths.NewTempDir();
        new SemanticScanner(includeExternal: false).Scan(slnPath, outDir);

        var di = JsonUtil.ReadFile<Dictionary<string, List<string>>>(Path.Combine(outDir, "index", "di.json")) ?? new();
        var diagnostics = JsonUtil.ReadFile<DiagnosticsModel>(Path.Combine(outDir, "index", "diagnostics.json"))!;

        Assert.IsTrue(di.TryGetValue("T:Ov.IBar", out var impls));
        CollectionAssert.Contains(impls, "T:Ov.Both");
        Assert.IsFalse(diagnostics.AmbiguousDiTypes.Any(a => a.TypeDocId == "T:Ov.Both"));
    }

    private static void RestoreQuietly(string dir)
    {
        var psi = new ProcessStartInfo("dotnet", "restore")
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(psi)!;
        process.WaitForExit(60_000);
    }
}
