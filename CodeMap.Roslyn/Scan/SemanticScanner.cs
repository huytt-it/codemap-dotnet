using System.Runtime.CompilerServices;
using CodeMap.Query.Config;
using CodeMap.Query.Json;
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
/// instead processed the same way SyntaxOnlyScanner processes L1 — parsed into a merged, BCL-only compilation —
/// and the project is recorded in diagnostics.json as degraded.
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
        var allSymbols = new List<SymbolRecord>();
        var allEdges = new List<EdgeRecord>();
        var interfaceImpls = new List<(string InterfaceId, string ImplId)>();
        var interfaceMemberMaps = new List<(string InterfaceMemberId, string ImplMemberId)>();
        var diRegistrations = new List<(string ServiceId, string ImplId)>();
        var attributeDiBindings = new List<(string ServiceId, string ImplId)>();
        var entryPoints = new List<EntryPoint>();
        var mediatrHandlerMaps = new List<(string RequestTypeId, string HandleMethodId)>();
        var mediatrSendSites = new List<(string FromId, string ConstructedTypeId, string File, int Line)>();

        CodeMapConfig config;
        try
        {
            config = CodeMapConfig.Load(solutionDir);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: {ex.Message}. Continuing with default configuration (DI-attribute detection disabled).");
            config = new CodeMapConfig();
        }

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

            foreach (var tree in compilation.SyntaxTrees)
            {
                var model = compilation.GetSemanticModel(tree);
                var relFile = ToRelativePath(solutionDir, tree.FilePath);

                var walker = new SyntaxSymbolWalker(model, name, relFile, _includeExternal, emitDataFlowEdges: true, config.DiAttribute);
                walker.Visit(tree.GetRoot());

                allSymbols.AddRange(walker.Symbols);
                allEdges.AddRange(walker.DirectEdges);
                interfaceImpls.AddRange(walker.InterfaceImplementations);
                interfaceMemberMaps.AddRange(walker.InterfaceMemberMappings);
                diRegistrations.AddRange(walker.DiRegistrations);
                attributeDiBindings.AddRange(walker.AttributeDiBindings);
                entryPoints.AddRange(walker.EntryPoints);
                mediatrHandlerMaps.AddRange(walker.MediatrHandlerMappings);
                mediatrSendSites.AddRange(walker.MediatrSendSites);

                foreach (var u in walker.UnresolvedBaseRefs)
                    diagnostics.UnresolvedInheritance.Add(new UnresolvedInheritance(
                        u.Project, u.File, u.Line, u.FromDocId, u.BaseSimpleName,
                        "Could not resolve the base type (outside the solution and not otherwise referenced, or dynamic type registration)."));
                foreach (var d in walker.UnresolvedDiRegistrations)
                    diagnostics.UnresolvedDiRegistrations.Add(new UnresolvedDiRegistration(name, d.File, d.Line, d.Reason));
                foreach (var (typeId, candidates) in walker.AmbiguousDiTypes)
                    diagnostics.AmbiguousDiTypes.Add(new AmbiguousDiType(typeId, candidates));
            }
        }

        RunL1Fallback(fallbackFiles, solutionDir, allSymbols, allEdges, diagnostics);

        ExpandCallsThroughInterfaces(allEdges, interfaceMemberMaps);

        DetectDiRegistrationConflicts(attributeDiBindings, diRegistrations, diagnostics);
        var (diMap, diConfirmedMap) = BuildDiJson(interfaceImpls, diRegistrations, attributeDiBindings, config.DiManualOverrides, diagnostics);
        AddCrossProjectDuplicateDiagnostics(allSymbols, diagnostics);
        ExpandMediatrSendEdges(allEdges, mediatrSendSites, mediatrHandlerMaps);

        JsonlWriter.Write(Path.Combine(indexDir, "symbols.jsonl"), allSymbols);
        JsonlWriter.Write(Path.Combine(indexDir, "edges.jsonl"), allEdges);
        JsonUtil.WriteIndented(Path.Combine(indexDir, "di.json"), diMap);
        JsonUtil.WriteIndented(Path.Combine(indexDir, "di-confirmed.json"), diConfirmedMap);
        JsonUtil.WriteIndented(Path.Combine(indexDir, "diagnostics.json"), diagnostics);
        JsonUtil.WriteIndented(Path.Combine(indexDir, "entrypoints.json"), entryPoints);
        MetaWriter.Write(indexDir, solutionPath, solutionDir, expectedProjects.Count,
            diagnostics.DegradedProjects.Select(d => d.Project).ToList(), allSymbols.Count, allEdges.Count);

        Console.WriteLine(
            $"Scan (L2) done: {allSymbols.Count} symbols, {allEdges.Count} edges, {diMap.Count} DI-mapped interface(s), " +
            $"{entryPoints.Count} entry point(s), " +
            $"{diagnostics.DegradedProjects.Count} degraded project(s) (fell back to L1), " +
            $"{diagnostics.UnresolvedInheritance.Count} unresolved base type(s), " +
            $"{diagnostics.DuplicateDocIdsAcrossProjects.Count} cross-project docId collision(s).");
        Console.WriteLine($"Output: {indexDir}");
    }

    /// <summary>Resolves each `mediator.Send(new FooCommand())` / `.Publish(new FooEvent())` call site to the matching handler's Handle method, emitting a `call` edge marked via:"mediatr" (spec section 5, "Entry point").</summary>
    private static void ExpandMediatrSendEdges(
        List<EdgeRecord> edges,
        List<(string FromId, string ConstructedTypeId, string File, int Line)> sendSites,
        List<(string RequestTypeId, string HandleMethodId)> handlerMappings)
    {
        var handleMethodByRequestType = handlerMappings
            .GroupBy(m => m.RequestTypeId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().HandleMethodId, StringComparer.Ordinal);

        foreach (var (fromId, constructedTypeId, file, line) in sendSites)
        {
            if (!handleMethodByRequestType.TryGetValue(constructedTypeId, out var handleId)) continue;
            edges.Add(new EdgeRecord { From = fromId, To = handleId, Kind = "call", File = file, Line = line, Via = "mediatr" });
        }
    }

    /// <summary>Duplicates each `call` edge that targets an interface member into a new edge at each concrete implementation, marked via:"interface". Keeps the original edge too.</summary>
    private static void ExpandCallsThroughInterfaces(List<EdgeRecord> edges, List<(string InterfaceMemberId, string ImplMemberId)> mappings)
    {
        var byInterfaceMember = mappings
            .GroupBy(m => m.InterfaceMemberId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(m => m.ImplMemberId).Distinct(StringComparer.Ordinal).ToList(), StringComparer.Ordinal);

        var expanded = new List<EdgeRecord>();
        foreach (var edge in edges)
        {
            if (edge.Kind != "call") continue;
            if (!byInterfaceMember.TryGetValue(edge.To, out var implIds)) continue;

            foreach (var implId in implIds)
            {
                if (implId == edge.To) continue; // the edge already targets this exact member (rare/defensive)
                expanded.Add(new EdgeRecord { From = edge.From, To = implId, Kind = "call", File = edge.File, Line = edge.Line, Via = "interface" });
            }
        }

        edges.AddRange(expanded);
    }

    /// <summary>
    /// di.json (spec section 4) merges structural "implements" data with real DI evidence, by design — it's
    /// meant as a best-guess reference even for types with no explicit registration anywhere. But that merge
    /// makes di.json unusable as ground truth for "is this actually the DI-bound implementation" (found via
    /// docs/BENCHMARK-INTERFACE-EXPANSION.md's audit: every interface-expand candidate is, by construction,
    /// also a "structural" implementer, so di.json alone can never disagree with an expanded edge). di-confirmed.json
    /// is the same shape but built from ONLY real evidence (fluent AddScoped/AddSingleton/AddTransient, the
    /// [Injectable]-attribute convention, and manual overrides) — no structural fallback — so ImpactEngine can
    /// tell "confirmed" apart from "merely implements the interface" when judging via:"interface" edges.
    /// </summary>
    private static (SortedDictionary<string, List<string>> Full, SortedDictionary<string, List<string>> ConfirmedOnly) BuildDiJson(
        List<(string InterfaceId, string ImplId)> structural,
        List<(string ServiceId, string ImplId)> diRegistrations,
        List<(string ServiceId, string ImplId)> attributeBindings,
        Dictionary<string, string>? manualOverrides,
        DiagnosticsModel diagnostics)
    {
        var full = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        var confirmedOnly = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);

        void Add(SortedDictionary<string, List<string>> map, string key, string value)
        {
            if (!map.TryGetValue(key, out var list))
            {
                list = new List<string>();
                map[key] = list;
            }

            if (!list.Contains(value, StringComparer.Ordinal))
                list.Add(value);
        }

        foreach (var (interfaceId, implId) in structural) Add(full, interfaceId, implId);
        foreach (var (serviceId, implId) in diRegistrations) { Add(full, serviceId, implId); Add(confirmedOnly, serviceId, implId); }
        foreach (var (serviceId, implId) in attributeBindings) { Add(full, serviceId, implId); Add(confirmedOnly, serviceId, implId); }

        // Manual overrides (spec section 5, P10) always win, and apply beyond just ambiguousDiTypes — resolve
        // them here, then drop the resolved type from the ambiguous-diagnostics list.
        if (manualOverrides is { Count: > 0 })
        {
            foreach (var (typeId, interfaceId) in manualOverrides)
            {
                Add(full, interfaceId, typeId);
                Add(confirmedOnly, interfaceId, typeId);
            }

            var overriddenTypes = new HashSet<string>(manualOverrides.Keys, StringComparer.Ordinal);
            diagnostics.AmbiguousDiTypes.RemoveAll(a => overriddenTypes.Contains(a.TypeDocId));
        }

        foreach (var list in full.Values) list.Sort(StringComparer.Ordinal);
        foreach (var list in confirmedOnly.Values) list.Sort(StringComparer.Ordinal);
        return (full, confirmedOnly);
    }

    /// <summary>The attribute-convention source and the fluent AddScoped/AddSingleton/AddTransient source disagreeing about a type's bound interface — spec section 5, P10.</summary>
    private static void DetectDiRegistrationConflicts(
        List<(string ServiceId, string ImplId)> attributeBindings,
        List<(string ServiceId, string ImplId)> diRegistrations,
        DiagnosticsModel diagnostics)
    {
        var attributeByType = attributeBindings
            .Where(b => b.ServiceId != b.ImplId) // exclude self-registration — nothing to conflict against
            .GroupBy(b => b.ImplId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().ServiceId, StringComparer.Ordinal);

        var fluentByType = diRegistrations
            .GroupBy(r => r.ImplId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(r => r.ServiceId).Distinct(StringComparer.Ordinal).ToList(), StringComparer.Ordinal);

        foreach (var (typeId, attrInterface) in attributeByType)
        {
            if (fluentByType.TryGetValue(typeId, out var fluentInterfaces) &&
                !fluentInterfaces.Contains(attrInterface, StringComparer.Ordinal))
            {
                diagnostics.DiRegistrationConflicts.Add(new DiRegistrationConflict(typeId, attrInterface, fluentInterfaces));
            }
        }
    }

    private static void AddCrossProjectDuplicateDiagnostics(List<SymbolRecord> allSymbols, DiagnosticsModel diagnostics)
    {
        // Same inherent docId limitation as L1 (see SyntaxOnlyScanner) — docId doesn't encode the assembly name.
        var crossProjectDuplicates = allSymbols
            .GroupBy(s => s.Id, StringComparer.Ordinal)
            .Where(g => g.Select(s => s.Project).Distinct().Count() > 1)
            .Select(g => new DuplicateDocId(
                g.Key,
                g.Select(s => s.Project).Distinct().ToList(),
                g.Select(s => s.File).Distinct().ToList()))
            .ToList();
        diagnostics.DuplicateDocIdsAcrossProjects.AddRange(crossProjectDuplicates);
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

    /// <summary>Same merged-compilation approach as SyntaxOnlyScanner, scoped to just the files of degraded projects.</summary>
    private static void RunL1Fallback(
        List<(string FilePath, string Project)> files, string solutionDir,
        List<SymbolRecord> symbolsOut, List<EdgeRecord> edgesOut, DiagnosticsModel diagnostics)
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
            references: BclReferenceProvider.GetReferences(),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        foreach (var tree in trees)
        {
            var model = compilation.GetSemanticModel(tree);
            var project = fileToProject.TryGetValue(tree.FilePath, out var p) ? p : "?";
            var relFile = ToRelativePath(solutionDir, tree.FilePath);

            var walker = new SyntaxSymbolWalker(model, project, relFile, includeExternal: false, emitDataFlowEdges: false);
            walker.Visit(tree.GetRoot());

            symbolsOut.AddRange(walker.Symbols);
            edgesOut.AddRange(walker.DirectEdges);

            foreach (var u in walker.UnresolvedBaseRefs)
                diagnostics.UnresolvedInheritance.Add(new UnresolvedInheritance(
                    u.Project, u.File, u.Line, u.FromDocId, u.BaseSimpleName,
                    "Could not resolve the base type at L1 fallback (outside this file set, or dynamic type registration)."));
        }
    }

    private static string ToRelativePath(string baseDir, string? fullPath)
    {
        if (fullPath == null) return "";
        var rel = Path.GetRelativePath(baseDir, fullPath);
        return rel.Replace(Path.DirectorySeparatorChar, '/');
    }
}
