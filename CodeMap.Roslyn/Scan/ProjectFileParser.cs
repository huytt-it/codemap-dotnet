using System.Xml.Linq;

namespace CodeMap.Roslyn.Scan;

/// <summary>
/// Reads .csproj as plain XML (no MSBuild evaluation) to get the list of .cs files and ProjectReferences.
/// Supports both SDK-style (implicit glob) and legacy .NET Framework style (explicit Compile Include) projects.
/// </summary>
internal static class ProjectFileParser
{
    public static ParsedProject Parse(string name, string csprojPath)
    {
        var dir = Path.GetDirectoryName(csprojPath)!;
        var doc = XDocument.Load(csprojPath);
        var root = doc.Root ?? throw new InvalidOperationException("The .csproj file is empty or invalid.");
        var isSdkStyle = root.Attribute("Sdk") != null;
        var ns = root.Name.Namespace;

        var explicitIncludes = new List<string>();
        var removes = new List<string>();
        var projectRefs = new List<string>();
        var enableDefaultCompile = true;

        foreach (var pg in root.Elements(ns + "PropertyGroup"))
        {
            var el = pg.Element(ns + "EnableDefaultCompileItems");
            if (el != null && bool.TryParse(el.Value, out var b)) enableDefaultCompile = b;
        }

        foreach (var ig in root.Elements(ns + "ItemGroup"))
        {
            foreach (var compile in ig.Elements(ns + "Compile"))
            {
                var inc = compile.Attribute("Include")?.Value;
                var rem = compile.Attribute("Remove")?.Value;
                if (!string.IsNullOrEmpty(inc)) explicitIncludes.Add(inc);
                if (!string.IsNullOrEmpty(rem)) removes.Add(rem);
            }

            foreach (var pr in ig.Elements(ns + "ProjectReference"))
            {
                var inc = pr.Attribute("Include")?.Value;
                if (string.IsNullOrEmpty(inc)) continue;
                var full = Path.GetFullPath(Path.Combine(dir, inc.Replace('\\', Path.DirectorySeparatorChar)));
                projectRefs.Add(full);
            }
        }

        var files = new List<string>();
        if (isSdkStyle && enableDefaultCompile)
            files.AddRange(GlobDefaultCompileFiles(dir));

        foreach (var inc in explicitIncludes)
        foreach (var f in ExpandGlob(dir, inc))
            if (!files.Contains(f, StringComparer.OrdinalIgnoreCase))
                files.Add(f);

        if (removes.Count > 0)
        {
            var removeSet = removes.SelectMany(r => ExpandGlob(dir, r)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            files = files.Where(f => !removeSet.Contains(f)).ToList();
        }

        return new ParsedProject
        {
            Name = name,
            FullPath = csprojPath,
            Directory = dir,
            IsSdkStyle = isSdkStyle,
            CompileFiles = files.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            ProjectReferences = projectRefs.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        };
    }

    private static IEnumerable<string> GlobDefaultCompileFiles(string dir)
        => Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IsUnderExcludedDir(dir, f));

    private static bool IsUnderExcludedDir(string projectDir, string file)
    {
        var rel = Path.GetRelativePath(projectDir, file);
        var segments = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        // only excludes bin/obj directly under the project dir, matching the SDK's default behavior
        return segments.Length > 1 &&
               (segments[0].Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                segments[0].Equals("obj", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Minimal glob support: exact paths, "*", and "**" (recursive). Enough for fixtures and most real-world csproj files.</summary>
    private static IEnumerable<string> ExpandGlob(string baseDir, string pattern)
    {
        pattern = pattern.Replace('\\', '/');
        if (!pattern.Contains('*'))
        {
            var full = Path.GetFullPath(Path.Combine(baseDir, pattern));
            if (File.Exists(full)) yield return full;
            yield break;
        }

        var recursive = pattern.Contains("**");
        var filePattern = pattern.Split('/').Last();
        var dirPattern = pattern[..^filePattern.Length].TrimEnd('/');
        var searchDir = string.IsNullOrEmpty(dirPattern) || dirPattern == "**"
            ? baseDir
            : Path.GetFullPath(Path.Combine(baseDir, dirPattern.Replace("**", "").TrimEnd('/')));

        if (!Directory.Exists(searchDir)) yield break;

        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        foreach (var f in Directory.EnumerateFiles(searchDir, filePattern, option))
        {
            if (!IsUnderExcludedDir(baseDir, f))
                yield return f;
        }
    }
}
