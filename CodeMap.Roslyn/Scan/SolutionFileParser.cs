using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CodeMap.Roslyn.Scan;

internal static class SolutionFileParser
{
    private static readonly Regex ProjectLine = new(
        "^Project\\(\"\\{[0-9A-Fa-f-]+\\}\"\\)\\s*=\\s*\"(?<name>[^\"]+)\"\\s*,\\s*\"(?<path>[^\"]+)\"\\s*,\\s*\"\\{[0-9A-Fa-f-]+\\}\"",
        RegexOptions.Compiled);

    /// <summary>
    /// Reads the solution via text/XML parsing (no MSBuild needed), returns (project name, absolute .csproj path).
    /// Handles both the classic .sln text format and .slnx, the XML format the .NET 10 SDK now emits by default —
    /// a repo created with a current SDK has no .sln at all, and parsing it as .sln silently yields zero projects.
    /// </summary>
    public static List<(string Name, string FullPath)> ParseProjects(string solutionPath)
    {
        var solutionDir = Path.GetDirectoryName(solutionPath)!;

        return Path.GetExtension(solutionPath).Equals(".slnx", StringComparison.OrdinalIgnoreCase)
            ? ParseSlnx(solutionPath, solutionDir)
            : ParseSln(solutionPath, solutionDir);
    }

    private static List<(string Name, string FullPath)> ParseSln(string solutionPath, string solutionDir)
    {
        var results = new List<(string, string)>();

        foreach (var line in File.ReadLines(solutionPath))
        {
            var m = ProjectLine.Match(line);
            if (!m.Success) continue;

            var relPath = m.Groups["path"].Value;
            if (!relPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) continue;

            results.Add((m.Groups["name"].Value, Resolve(solutionDir, relPath)));
        }

        return results;
    }

    /// <summary>
    /// .slnx is plain XML: &lt;Project Path="a/b.csproj" /&gt;, optionally nested inside &lt;Folder&gt; elements.
    /// Descendants (not Elements) so solution folders at any depth are included. Unlike .sln there is no Name
    /// attribute, so the project name is the file name — which is what .sln conventionally carries anyway.
    /// </summary>
    private static List<(string Name, string FullPath)> ParseSlnx(string solutionPath, string solutionDir)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Load(solutionPath);
        }
        catch (System.Xml.XmlException ex)
        {
            // Same spirit as the rest of the scanner: say what is wrong rather than reporting an empty solution.
            Console.Error.WriteLine($"Warning: could not parse '{Path.GetFileName(solutionPath)}' as .slnx XML: {ex.Message}");
            return new List<(string, string)>();
        }

        var results = new List<(string, string)>();

        foreach (var project in doc.Descendants("Project"))
        {
            var relPath = project.Attribute("Path")?.Value;
            if (string.IsNullOrWhiteSpace(relPath)) continue;
            if (!relPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) continue;

            var fullPath = Resolve(solutionDir, relPath);
            results.Add((Path.GetFileNameWithoutExtension(fullPath), fullPath));
        }

        return results;
    }

    /// <summary>.sln always uses backslashes; .slnx uses forward slashes. Normalize both to this OS's separator.</summary>
    private static string Resolve(string solutionDir, string relPath) =>
        Path.GetFullPath(Path.Combine(solutionDir, relPath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)));
}
