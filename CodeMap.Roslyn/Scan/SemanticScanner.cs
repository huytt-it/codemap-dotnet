using System.Runtime.CompilerServices;
using CodeMap.Query.Config;
using CodeMap.Query.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.MSBuild;

namespace CodeMap.Roslyn.Scan;

/// <summary>
/// L2: MSBuildWorkspace.OpenSolutionAsync + real per-project SemanticModel. Produces the full edge set
/// (call/new/read/write/inherits/implements), di.json, and the expand-via-interface pass.
///
/// Fallback requirement (spec section 5): a project that fails to load must not crash the scan. Its files are
/// instead processed the same way SyntaxOnlyScanner processes L1 — parsed into a merged compilation over the BCL
/// plus the projects that did load (see RunL1Fallback) — and the project is recorded in diagnostics.json as degraded.
/// </summary>
internal sealed class SemanticScanner
{
    private readonly bool _includeExternal;

    public SemanticScanner(bool includeExternal) => _includeExternal = includeExternal;

    // NoInlining: this is the first method that touches MSBuildWorkspace. ScanCommand.Run calls
    // MsBuildBootstrap.Register() before constructing/calling this — keeping this method un-inlined
    // guarantees the JIT can't resolve MSBuildWorkspace's type as part of compiling the caller before
    // registration has actually run. See spec section 2, "Bẫy bắt buộc xử lý đúng".
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Scan(string solutionPath, string outDir)
    {
        var solutionDir = Path.GetDirectoryName(solutionPath)!;
        var indexDir = Path.Combine(outDir, "index");
        Directory.CreateDirectory(indexDir);

        var diagnostics = new DiagnosticsModel();
        var results = new ScanResults();
        var config = ScanResults.LoadConfig(solutionDir);

        var expectedProjects = SolutionFileParser.ParseProjects(solutionPath);
        if (expectedProjects.Count == 0)
            Console.Error.WriteLine("Warning: no .csproj project found in the solution.");

        var loadMessages = new List<string>();
        using var workspace = MSBuildWorkspace.Create();
        workspace.RegisterWorkspaceFailedHandler(e => loadMessages.Add(e.Diagnostic.Message));

        Solution? solution = null;
        try
        {
            solution = workspace.OpenSolutionAsync(solutionPath).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            loadMessages.Add(ex.Message);
        }

        // One Project instance per unique csproj path (a multi-targeted project loads once per TFM; we only
        // want to walk its files once, so we just take the first).
        var loadedByPath = solution?.Projects
            .Where(p => p.FilePath != null)
            .GroupBy(p => p.FilePath!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, Project>(StringComparer.OrdinalIgnoreCase);

        var fallbackFiles = new List<(string FilePath, string Project)>();
        var loadedCompilations = new List<Compilation>();

        foreach (var (name, csprojPath) in expectedProjects)
        {
            if (!loadedByPath.TryGetValue(csprojPath, out var project))
            {
                diagnostics.DegradedProjects.Add(new DegradedProject(name, DescribeLoadFailure(csprojPath, loadMessages)));
                AddProjectFilesToFallback(name, csprojPath, fallbackFiles, diagnostics);
                continue;
            }

            // An MSBuild evaluation problem (unresolvable Sdk, bad TargetFramework, ...) doesn't always throw or
            // leave the project out of `solution.Projects` — it can also "succeed" with a project that has zero
            // documents (no implicit compile-item globbing ran). That's silent data loss, not a real success:
            // treat it exactly like a load failure so the L1 fallback (which globs files itself, independent of
            // MSBuild) gets a chance to actually find its source files.
            if (project.DocumentIds.Count == 0)
            {
                diagnostics.DegradedProjects.Add(new DegradedProject(name,
                    "MSBuildWorkspace loaded this project with zero documents (likely an MSBuild evaluation problem: unresolvable Sdk, bad TargetFramework, ...)."));
                AddProjectFilesToFallback(name, csprojPath, fallbackFiles, diagnostics);
                continue;
            }

            Compilation? compilation;
            try
            {
                compilation = project.GetCompilationAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                diagnostics.DegradedProjects.Add(new DegradedProject(name, $"Failed to build the compilation: {ex.Message}"));
                AddProjectFilesToFallback(name, csprojPath, fallbackFiles, diagnostics);
                continue;
            }

            if (compilation == null)
            {
                diagnostics.DegradedProjects.Add(new DegradedProject(name, "MSBuildWorkspace produced no compilation for this project (not a C# project, or it failed to load)."));
                AddProjectFilesToFallback(name, csprojPath, fallbackFiles, diagnostics);
                continue;
            }

            loadedCompilations.Add(compilation);
            foreach (var tree in compilation.SyntaxTrees)
            {
                var model = compilation.GetSemanticModel(tree);
                var relFile = ToRelativePath(solutionDir, tree.FilePath);

                var walker = new SyntaxSymbolWalker(model, name, relFile, _includeExternal, config.DiAttribute);
                walker.Visit(tree.GetRoot());
                results.Add(walker, name, diagnostics,
                    "Could not resolve the base type (outside the solution and not otherwise referenced, or dynamic type registration).");
            }
        }

        RunL1Fallback(fallbackFiles, loadedCompilations, solutionDir, config, results, diagnostics);

        var diMappedCount = results.CompleteAndWrite(indexDir, config, diagnostics);
        MetaWriter.Write(indexDir, solutionPath, solutionDir, expectedProjects.Count,
            diagnostics.DegradedProjects.Select(d => d.Project).ToList(), results.Symbols.Count, results.Edges.Count);

        Console.WriteLine(
            $"Scan (L2) done: {results.Symbols.Count} symbols, {results.Edges.Count} edges, {diMappedCount} DI-mapped interface(s), " +
            $"{results.EntryPoints.Count} entry point(s), " +
            $"{diagnostics.DegradedProjects.Count} degraded project(s) (fell back to L1), " +
            $"{diagnostics.UnresolvedInheritance.Count} unresolved base type(s), " +
            $"{diagnostics.DuplicateDocIdsAcrossProjects.Count} cross-project docId collision(s).");
        Console.WriteLine($"Output: {indexDir}");
    }

    private static string DescribeLoadFailure(string csprojPath, List<string> loadMessages)
    {
        var matches = loadMessages.Where(m => m.Contains(Path.GetFileName(csprojPath), StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count > 0) return string.Join("; ", matches.Take(3));
        return loadMessages.Count > 0
            ? $"Project not present in the loaded MSBuildWorkspace solution. Workspace reported {loadMessages.Count} diagnostic(s), none clearly matching this project."
            : "Project not present in the loaded MSBuildWorkspace solution.";
    }

    private static void AddProjectFilesToFallback(
        string name, string csprojPath, List<(string FilePath, string Project)> fallbackFiles, DiagnosticsModel diagnostics)
    {
        if (!File.Exists(csprojPath)) return; // already covered by the "not present" reason; nothing to fall back to

        try
        {
            var parsed = ProjectFileParser.Parse(name, csprojPath);
            fallbackFiles.AddRange(parsed.CompileFiles.Select(f => (f, name)));
        }
        catch (Exception ex)
        {
            diagnostics.DegradedProjects.Add(new DegradedProject(name, $"L1 fallback also failed to read the project file: {ex.Message}"));
        }
    }

    /// <summary>
    /// Same merged-compilation approach as SyntaxOnlyScanner, scoped to the files of degraded projects, and with
    /// the same data-flow edges, entry points and DI data as L2 — a degraded project used to contribute only its
    /// declarations, so every caller inside it was invisible to impact.
    ///
    /// The projects that DID load are added as compilation references, so a degraded project's calls into them
    /// (the usual shape: a broken Web project over a healthy Core) bind to the very symbols L2 already emitted,
    /// docIds included — a parameter typed with a NuGet type keeps its fully qualified name, which re-parsing
    /// those files into this BCL-only compilation would lose. They're bound against, never walked, so none of
    /// their symbols is emitted twice.
    /// </summary>
    private void RunL1Fallback(
        List<(string FilePath, string Project)> files, List<Compilation> loadedCompilations, string solutionDir,
        CodeMapConfig config, ScanResults results, DiagnosticsModel diagnostics)
    {
        if (files.Count == 0) return;

        var trees = new List<SyntaxTree>();
        var fileToProject = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (filePath, project) in files)
        {
            try
            {
                var text = File.ReadAllText(filePath);
                trees.Add(CSharpSyntaxTree.ParseText(text, path: filePath));
                fileToProject[filePath] = project;
            }
            catch
            {
                // best-effort fallback; an unreadable file here just means one less symbol, already covered by
                // the project's own DegradedProject entry
            }
        }

        if (trees.Count == 0) return;

        var compilation = CSharpCompilation.Create(
            assemblyName: "L1Fallback",
            syntaxTrees: trees,
            references: BclReferenceProvider.GetReferences()
                .AddRange(loadedCompilations.OfType<CSharpCompilation>().Select(c => c.ToMetadataReference())),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        foreach (var tree in trees)
        {
            var model = compilation.GetSemanticModel(tree);
            var project = fileToProject.TryGetValue(tree.FilePath, out var p) ? p : "?";
            var relFile = ToRelativePath(solutionDir, tree.FilePath);

            var walker = new SyntaxSymbolWalker(model, project, relFile, _includeExternal, config.DiAttribute);
            walker.Visit(tree.GetRoot());
            results.Add(walker, project, diagnostics,
                "Could not resolve the base type at L1 fallback (outside the solution's loaded projects and this file set, or dynamic type registration).");
        }
    }

    private static string ToRelativePath(string baseDir, string? fullPath)
    {
        if (fullPath == null) return "";
        var rel = Path.GetRelativePath(baseDir, fullPath);
        return rel.Replace(Path.DirectorySeparatorChar, '/');
    }
}
