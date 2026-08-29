using System.Diagnostics;
using Microsoft.Build.Locator;

namespace CodeMap.Roslyn;

/// <summary>
/// MSBuildLocator.RegisterDefaults() relies on auto-detecting a VS/SDK instance, and on some machines with only
/// the .NET SDK installed (no VS) that detection can come back empty even though `dotnet --list-sdks` sees the
/// SDK just fine. Fallback: find the SDK path ourselves via `dotnet --list-sdks` and call RegisterMSBuildPath directly.
/// </summary>
internal static class MsBuildBootstrap
{
    public static void Register()
    {
        if (MSBuildLocator.IsRegistered) return;

        try
        {
            MSBuildLocator.RegisterDefaults();
            return;
        }
        catch (InvalidOperationException)
        {
            // fall through to the fallback below
        }

        var sdkPath = FindLatestDotnetSdkPath()
            ?? throw new InvalidOperationException(
                "No .NET SDK found (both MSBuildLocator auto-detection and the 'dotnet --list-sdks' fallback failed). " +
                "Install the .NET SDK and make sure 'dotnet' is on PATH.");

        MSBuildLocator.RegisterMSBuildPath(sdkPath);
    }

    private static string? FindLatestDotnetSdkPath()
    {
        try
        {
            var psi = new ProcessStartInfo("dotnet", "--list-sdks")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(psi);
            if (process == null) return null;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0) return null;

            string? best = null;
            Version? bestVersion = null;
            foreach (var rawLine in output.Split('\n'))
            {
                var line = rawLine.Trim();
                var bracketStart = line.IndexOf('[');
                var bracketEnd = line.LastIndexOf(']');
                if (bracketStart < 0 || bracketEnd < bracketStart) continue;

                var versionText = line[..bracketStart].Trim();
                var root = line[(bracketStart + 1)..bracketEnd].Trim();
                var parsableVersion = versionText.Split('-')[0];
                if (!Version.TryParse(parsableVersion, out var version)) continue;

                if (bestVersion == null || version > bestVersion)
                {
                    bestVersion = version;
                    best = Path.Combine(root, versionText);
                }
            }

            return best != null && Directory.Exists(best) ? best : null;
        }
        catch
        {
            return null;
        }
    }
}
