using CodeMap.Roslyn.Scan;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CodeMap.Tests;

/// <summary>
/// Drives SyntaxSymbolWalker directly over in-memory source, compiled against the same BCL-only references L1
/// uses — so each case pins one walker behavior without a fixture solution, MSBuild, or a restore.
/// </summary>
[TestClass]
public class SyntaxSymbolWalkerTests
{
    private const string ExtensionSource = """
        namespace N;

        public static class E
        {
            public static int Ext(this string s, int i) => i;
            public static T Gen<T>(this T value) => value;
        }

        public class C
        {
            public void Reduced() { "a".Ext(1); }
            public void ReducedGeneric() { 5.Gen(); }
            public void StaticForm() { E.Ext("b", 2); }
        }
        """;

    [TestMethod] // Roslyn binds `"a".Ext(1)` to the REDUCED method, whose docId drops the `this` parameter
    public void Extension_method_called_in_reduced_form_targets_the_declared_docId()
    {
        var walker = Walk(ExtensionSource);

        var edge = TestAssert.Single(walker.DirectEdges, e => e.From == "M:N.C.Reduced" && e.Kind == "call");
        Assert.AreEqual("M:N.E.Ext(System.String,System.Int32)", edge.To);
        TestAssert.Contains(walker.Symbols, s => s.Id == edge.To);
    }

    [TestMethod]
    public void Generic_extension_method_called_in_reduced_form_targets_the_declared_docId()
    {
        var walker = Walk(ExtensionSource);

        var edge = TestAssert.Single(walker.DirectEdges, e => e.From == "M:N.C.ReducedGeneric" && e.Kind == "call");
        Assert.AreEqual("M:N.E.Gen``1(``0)", edge.To);
        TestAssert.Contains(walker.Symbols, s => s.Id == edge.To);
    }

    [TestMethod] // control case: the static form was always right, and must stay right
    public void Extension_method_called_in_static_form_targets_the_declared_docId()
    {
        var walker = Walk(ExtensionSource);

        var edge = TestAssert.Single(walker.DirectEdges, e => e.From == "M:N.C.StaticForm" && e.Kind == "call");
        Assert.AreEqual("M:N.E.Ext(System.String,System.Int32)", edge.To);
    }

    private static SyntaxSymbolWalker Walk(string source, bool emitDataFlowEdges = true)
    {
        var tree = CSharpSyntaxTree.ParseText(source, path: "Test.cs");
        var compilation = CSharpCompilation.Create(
            "WalkerTest", new[] { tree }, BclReferenceProvider.GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var walker = new SyntaxSymbolWalker(compilation.GetSemanticModel(tree), "Test", "Test.cs", includeExternal: false, emitDataFlowEdges);
        walker.Visit(tree.GetRoot());
        return walker;
    }
}
