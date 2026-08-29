using CodeMap.Query.Json;
using CodeMap.Query.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CodeMap.Roslyn.Scan;

/// <summary>
/// L1: CSharpSyntaxTree only, does not require the solution to build (no MSBuild evaluation, no NuGet restore).
/// Every .cs file from every project is merged into ONE compilation (with only the BCL references of the running
/// runtime) so cross-project symbols in the solution still resolve correctly — inherits/implements never need to
/// guess by name; only things genuinely outside the solution (NuGet packages, dynamic registration) land in diagnostics.
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

        var allSymbols = new List<SymbolRecord>();
        var allEdges = new List<EdgeRecord>();
        var unresolved = new List<UnresolvedBaseRef>();

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

                var walker = new SyntaxSymbolWalker(model, project, relFile, _includeExternal, emitDataFlowEdges: false);
                walker.Visit(tree.GetRoot());

                allSymbols.AddRange(walker.Symbols);
                allEdges.AddRange(walker.DirectEdges);
                unresolved.AddRange(walker.UnresolvedBaseRefs);
            }
        }

        foreach (var u in unresolved)
        {
            diagnostics.UnresolvedInheritance.Add(new UnresolvedInheritance(
                u.Project, u.File, u.Line, u.FromDocId, u.BaseSimpleName,
                "Could not resolve the base type (outside the solution: NuGet package, or dynamic type registration)."));
        }

        // The solution-wide merged compilation (see class doc) trades in one risk: docId doesn't encode the
        // assembly name, so two fully-qualified-name-identical types in two different projects (legal in .NET)
        // collide on docId. This is an inherent limitation of docId itself, not a merging bug — log it
        // transparently instead of letting symbols.jsonl carry two records sharing an "id" unnoticed.
        var crossProjectDuplicates = allSymbols
            .GroupBy(s => s.Id, StringComparer.Ordinal)
            .Where(g => g.Select(s => s.Project).Distinct().Count() > 1)
            .Select(g => new DuplicateDocId(
                g.Key,
                g.Select(s => s.Project).Distinct().ToList(),
                g.Select(s => s.File).Distinct().ToList()))
            .ToList();
        diagnostics.DuplicateDocIdsAcrossProjects.AddRange(crossProjectDuplicates);

        JsonlWriter.Write(Path.Combine(indexDir, "symbols.jsonl"), allSymbols);
        JsonlWriter.Write(Path.Combine(indexDir, "edges.jsonl"), allEdges);
        JsonUtil.WriteIndented(Path.Combine(indexDir, "diagnostics.json"), diagnostics);
        MetaWriter.Write(indexDir, solutionPath, solutionDir, projectsRaw.Count,
            diagnostics.DegradedProjects.Select(d => d.Project).ToList(), allSymbols.Count, allEdges.Count);

        Console.WriteLine(
            $"Scan (L1) done: {allSymbols.Count} symbols, {allEdges.Count} edges, " +
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
