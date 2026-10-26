using CodeMap.Query.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CodeMap.Roslyn.Scan;

/// <summary>
/// L1: CSharpSyntaxTree only, does not require the solution to build (no MSBuild evaluation, no NuGet restore).
/// Every .cs file from every project is merged into ONE compilation (with only the BCL references of the running
/// runtime) so cross-project symbols in the solution still resolve correctly — inherits/implements never need to
/// guess by name; only things genuinely outside the solution (NuGet packages, dynamic registration) land in diagnostics.
///
/// The same binding gives call/new/read/write edges, entry points and DI data for everything declared in the
/// solution, so this writes the same index files L2 does. What L1 misses is a call whose overload can't be picked
/// without a NuGet type (an argument or receiver typed with one): the walker drops an unresolved call rather than
/// guessing, so L1 under-reports callers but doesn't invent them. Measured on eShopOnWeb: 236 call edges against
/// L2's 254, all 25 HTTP entry points, 16 of L2's 17 DI-mapped services (was 0 call edges and no entrypoints.json).
/// </summary>
internal sealed class SyntaxOnlyScanner
{
    private readonly bool _includeExternal;

    public SyntaxOnlyScanner(bool includeExternal) => _includeExternal = includeExternal;

    public void Scan(string solutionPath, string outDir)
    {
        var solutionDir = Path.GetDirectoryName(solutionPath)!;
        var indexDir = Path.Combine(outDir, "index");
        Directory.CreateDirectory(indexDir);

        var diagnostics = new DiagnosticsModel();
        var config = ScanResults.LoadConfig(solutionDir);
        var projectsRaw = SolutionFileParser.ParseProjects(solutionPath);
        if (projectsRaw.Count == 0)
            Console.Error.WriteLine("Warning: no .csproj project found in the solution.");

        var fileToProject = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var trees = new List<SyntaxTree>();

        foreach (var (name, path) in projectsRaw)
        {
            if (!File.Exists(path))
            {
                diagnostics.DegradedProjects.Add(new DegradedProject(name, $"Project file not found: {path}"));
                continue;
            }

            ParsedProject parsed;
            try
            {
                parsed = ProjectFileParser.Parse(name, path);
            }
            catch (Exception ex)
            {
                diagnostics.DegradedProjects.Add(new DegradedProject(name, $"Failed to read project file: {ex.Message}"));
                continue;
            }

            var fileFailures = new List<string>();
            foreach (var file in parsed.CompileFiles)
            {
                try
                {
                    var text = File.ReadAllText(file);
                    trees.Add(CSharpSyntaxTree.ParseText(text, path: file));
                    fileToProject[file] = name;
                }
                catch (Exception ex)
                {
                    fileFailures.Add($"{Path.GetFileName(file)}: {ex.Message}");
                }
            }

            if (fileFailures.Count > 0)
                diagnostics.DegradedProjects.Add(new DegradedProject(
                    name, $"{fileFailures.Count} file(s) could not be read: {string.Join("; ", fileFailures.Take(5))}"));
        }

        var results = new ScanResults();

        if (trees.Count > 0)
        {
            var compilation = CSharpCompilation.Create(
                assemblyName: Path.GetFileNameWithoutExtension(solutionPath),
                syntaxTrees: trees,
                references: BclReferenceProvider.GetReferences(),
                options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            foreach (var tree in trees)
            {
                var model = compilation.GetSemanticModel(tree);
                var project = fileToProject.TryGetValue(tree.FilePath, out var p) ? p : "?";
                var relFile = ToRelativePath(solutionDir, tree.FilePath);

                var walker = new SyntaxSymbolWalker(model, project, relFile, _includeExternal, config.DiAttribute);
                walker.Visit(tree.GetRoot());
                results.Add(walker, project, diagnostics,
                    "Could not resolve the base type (outside the solution: NuGet package, or dynamic type registration).");
            }
        }

        var diMappedCount = results.CompleteAndWrite(indexDir, config, diagnostics);
        MetaWriter.Write(indexDir, solutionPath, solutionDir, projectsRaw.Count,
            diagnostics.DegradedProjects.Select(d => d.Project).ToList(), results.Symbols.Count, results.Edges.Count);

        Console.WriteLine(
            $"Scan (L1) done: {results.Symbols.Count} symbols, {results.Edges.Count} edges, {diMappedCount} DI-mapped interface(s), " +
            $"{results.EntryPoints.Count} entry point(s), " +
            $"{diagnostics.DegradedProjects.Count} degraded project(s), {diagnostics.UnresolvedInheritance.Count} unresolved base type(s), " +
            $"{diagnostics.DuplicateDocIdsAcrossProjects.Count} cross-project docId collision(s).");
        Console.WriteLine($"Output: {indexDir}");
    }

    private static string ToRelativePath(string baseDir, string? fullPath)
    {
        if (fullPath == null) return "";
        var rel = Path.GetRelativePath(baseDir, fullPath);
        return rel.Replace(Path.DirectorySeparatorChar, '/');
    }
}
