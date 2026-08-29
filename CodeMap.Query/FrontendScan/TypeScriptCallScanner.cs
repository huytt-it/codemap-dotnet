using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using CodeMap.Query.Json;

namespace CodeMap.Query.FrontendScan;

/// <summary>
/// Spec section 6, "Angular / TypeScript (confidence: high)": shells out to `node` running ts-call-scan.js
/// (embedded resource), which uses the TypeScript Compiler API — the Microsoft `typescript` npm package is the
/// only allowed frontend-scan dependency (spec section 2), and it isn't vendored: it's resolved from the
/// scanned frontend project's own node_modules, the same copy the project itself builds with. A machine with no
/// `node` or no `typescript` installed degrades gracefully (jQuery-only scan, one clear diagnostic) rather than
/// failing the whole command.
/// </summary>
public static class TypeScriptCallScanner
{
    /// <summary>
    /// InjectedBy: components whose constructor takes the HTTP call's containing service class as a parameter
    /// (Angular DI, one hop only — see ts-call-scan.js). Empty when the call isn't inside a class, the call is
    /// already inside an @Component class (IsComponentItself — no resolution needed, the component itself IS
    /// the screen), or no other class injects the containing service directly.
    /// </summary>
    public sealed record RawCall(string File, int Line, string HttpMethod, string RawUrl, List<string> InjectedBy, bool IsComponentItself);

    public sealed record ScanOutcome(List<RawCall> Calls, string? SkippedReason);

    public static ScanOutcome Scan(string frontendRoot)
    {
        var nodePath = FindOnPath();
        if (nodePath == null)
            return new ScanOutcome(new(), "node not found on PATH — Angular/TypeScript scanning skipped (jQuery scan still runs)");

        var typescriptModule = FindTypeScriptModule(frontendRoot);
        if (typescriptModule == null)
            return new ScanOutcome(new(), $"typescript package not found under {frontendRoot} (run 'npm install' in the frontend project) — Angular/TypeScript scanning skipped (jQuery scan still runs)");

        var scriptPath = ExtractEmbeddedScript();
        try
        {
            var psi = new ProcessStartInfo(nodePath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add(scriptPath);
            psi.ArgumentList.Add(frontendRoot);
            psi.ArgumentList.Add(typescriptModule);

            using var process = Process.Start(psi);
            if (process == null)
                return new ScanOutcome(new(), "failed to start node — Angular/TypeScript scanning skipped");

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();

            if (process.ExitCode != 0)
                return new ScanOutcome(new(), $"ts-call-scan.js failed (exit {process.ExitCode}): {stderr}");

            var raw = JsonSerializer.Deserialize<List<RawCall>>(stdout, JsonUtil.Compact) ?? new();
            return new ScanOutcome(raw, null);
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { /* best-effort temp cleanup */ }
        }
    }

    /// <summary>Walks up from <paramref name="startDir"/> looking for node_modules/typescript — the same resolution order Node's own `require('typescript')` would use from a file inside the frontend project.</summary>
    private static string? FindTypeScriptModule(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "node_modules", "typescript");
            if (File.Exists(Path.Combine(candidate, "package.json")))
                return candidate.Replace('\\', '/');
            dir = dir.Parent;
        }

        return FindGlobalTypeScriptModule();
    }

    /// <summary>Last-resort fallback for a frontend project that hasn't run `npm install` locally but has a global `typescript` install — not the intended path (a real Angular project always has its own), just avoids a false "not found" when one is reachable.</summary>
    private static string? FindGlobalTypeScriptModule()
    {
        var npmPath = FindOnPath("npm");
        if (npmPath == null) return null;

        try
        {
            var psi = new ProcessStartInfo(npmPath) { RedirectStandardOutput = true, UseShellExecute = false };
            psi.ArgumentList.Add("root");
            psi.ArgumentList.Add("-g");
            using var process = Process.Start(psi);
            if (process == null) return null;
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(5000);
            if (process.ExitCode != 0 || output.Length == 0) return null;

            var candidate = Path.Combine(output, "typescript");
            return File.Exists(Path.Combine(candidate, "package.json")) ? candidate.Replace('\\', '/') : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? FindOnPath() => FindOnPath("node");

    /// <summary>
    /// CreateProcess (what Process.Start(psi) calls with UseShellExecute=false) does not consult PATHEXT the way
    /// cmd.exe does, so on Windows spawning "npm" directly fails silently (it's npm.cmd, a batch script) — every
    /// candidate extension has to be tried explicitly against each PATH directory.
    /// </summary>
    private static string? FindOnPath(string exeBaseName)
    {
        var exeNames = OperatingSystem.IsWindows()
            ? new[] { $"{exeBaseName}.exe", $"{exeBaseName}.cmd", $"{exeBaseName}.bat" }
            : new[] { exeBaseName };
        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        foreach (var dir in pathDirs)
        {
            foreach (var exeName in exeNames)
            {
                var candidate = Path.Combine(dir, exeName);
                if (File.Exists(candidate)) return candidate;
            }
        }

        return null;
    }

    private static string ExtractEmbeddedScript()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames().Single(n => n.EndsWith("ts-call-scan.js", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        var tempPath = Path.Combine(Path.GetTempPath(), $"codemap-ts-call-scan-{Guid.NewGuid():N}.js");
        using (var fileStream = File.Create(tempPath))
            stream.CopyTo(fileStream);
        return tempPath;
    }
}
