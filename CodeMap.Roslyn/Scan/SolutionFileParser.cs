using System.Text.RegularExpressions;

namespace CodeMap.Roslyn.Scan;

internal static class SolutionFileParser
{
    private static readonly Regex ProjectLine = new(
        "^Project\\(\"\\{[0-9A-Fa-f-]+\\}\"\\)\\s*=\\s*\"(?<name>[^\"]+)\"\\s*,\\s*\"(?<path>[^\"]+)\"\\s*,\\s*\"\\{[0-9A-Fa-f-]+\\}\"",
        RegexOptions.Compiled);

    /// <summary>Reads .sln via text parsing (no MSBuild needed), returns (project name, absolute .csproj path).</summary>
    public static List<(string Name, string FullPath)> ParseProjects(string solutionPath)
    {
        var solutionDir = Path.GetDirectoryName(solutionPath)!;
        var results = new List<(string, string)>();

        foreach (var line in File.ReadLines(solutionPath))
        {
            var m = ProjectLine.Match(line);
            if (!m.Success) continue;

            var relPath = m.Groups["path"].Value;
            if (!relPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) continue;

            var fullPath = Path.GetFullPath(Path.Combine(solutionDir, relPath.Replace('\\', Path.DirectorySeparatorChar)));
            results.Add((m.Groups["name"].Value, fullPath));
        }

        return results;
    }
}
