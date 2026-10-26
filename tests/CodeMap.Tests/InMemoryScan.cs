using CodeMap.Query.Config;
using CodeMap.Query.Models;
using CodeMap.Roslyn.Scan;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CodeMap.Tests;

/// <summary>
/// The scan pipeline minus MSBuild: in-memory source compiled against L1's BCL-only references, walked, then put
/// through ScanResults' solution-wide passes (interface/MediatR expansion, ...). For cases whose edges only exist
/// after those passes; a walker-only case uses SyntaxSymbolWalkerTests.Walk.
/// </summary>
internal static class InMemoryScan
{
    public static ScanResults Run(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, path: "Test.cs");
        var compilation = CSharpCompilation.Create(
            "PipelineTest", new[] { tree }, BclReferenceProvider.GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var walker = new SyntaxSymbolWalker(compilation.GetSemanticModel(tree), "Test", "Test.cs", includeExternal: false);
        walker.Visit(tree.GetRoot());

        var results = new ScanResults();
        var diagnostics = new DiagnosticsModel();
        results.Add(walker, "Test", diagnostics, "unresolved");
        results.CompleteAndWrite(TestPaths.NewTempDir(), new CodeMapConfig(), diagnostics);
        return results;
    }
}
