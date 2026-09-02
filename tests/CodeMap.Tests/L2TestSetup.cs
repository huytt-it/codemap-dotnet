using System.Diagnostics;
using CodeMap.Roslyn;

namespace CodeMap.Tests;

/// <summary>
/// Two things an L2 scan needs that only ever happen automatically inside ScanCommand's own L2 branch — never
/// inside the test host, since tests call SemanticScanner directly:
///   1. MsBuildBootstrap.Register() — without it MSBuildWorkspace silently fails to resolve MSBuild in-process
///      and every project "degrades", making L2 tests pass for the wrong reason (found the hard way: every
///      project reported "not present in the loaded MSBuildWorkspace solution" even after a successful restore).
///   2. `dotnet restore` on the fixture — MSBuildWorkspace needs obj/project.assets.json to load a project.
/// Runs once per test assembly load (all L2 test classes share the same fixture solution).
/// </summary>
internal static class L2TestSetup
{
    private static readonly Lazy<bool> Restored = new(() =>
    {
        MsBuildBootstrap.Register();
        return RestoreFixtureSolution();
    });

    public static void EnsureFixtureRestored() => _ = Restored.Value;

    private static bool RestoreFixtureSolution()
    {
        // Name the .sln explicitly: the fixture directory holds both SampleSolution.sln and SampleSolution.slnx
        // (so the .slnx parser can be tested against a known-equivalent .sln), and a bare `dotnet restore` in a
        // directory with two solution files fails with MSB1011 rather than picking one.
        var psi = new ProcessStartInfo("dotnet", $"restore \"{TestPaths.FixtureSolution}\"")
        {
            WorkingDirectory = Path.GetDirectoryName(TestPaths.FixtureSolution),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = Process.Start(psi)!;
        process.WaitForExit(120_000);
        return process.ExitCode == 0;
    }
}
