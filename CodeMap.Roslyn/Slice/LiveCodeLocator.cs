using CodeMap.Query.Models;
using CodeMap.Roslyn.Scan;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace CodeMap.Roslyn.Slice;

/// <summary>
/// Spec section 7, "Đọc code lúc query, không lưu code trong index": index scans run once a day, so any stored
/// code would be stale. `slice` instead re-opens the file from disk and re-parses JUST that one file (no
/// MSBuildWorkspace, no solution-wide compilation — a throwaway single-file CSharpCompilation with only BCL
/// references, same trick as L1) to re-locate the symbol by docId and read its CURRENT text. A day-old index
/// still points at today's code; if the symbol moved or was renamed, callers get a clear "not found" instead of
/// stale or wrong code.
/// </summary>
public static class LiveCodeLocator
{
    public sealed record Result(int Line, string Snippet);

    public static Result? Locate(string absoluteFilePath, SymbolRecord target)
    {
        if (!File.Exists(absoluteFilePath)) return null;

        string text;
        try
        {
            text = File.ReadAllText(absoluteFilePath);
        }
        catch
        {
            return null;
        }

        var tree = CSharpSyntaxTree.ParseText(text, path: absoluteFilePath);
        var compilation = CSharpCompilation.Create(
            assemblyName: "LiveCodeLocator",
            syntaxTrees: new[] { tree },
            references: BclReferenceProvider.GetReferences(),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var model = compilation.GetSemanticModel(tree);
        var root = tree.GetRoot();

        // Two passes over the same candidates: exact docId match first (correct whenever the symbol's own
        // signature only involves BCL types or types declared in this same file — the common case), then a
        // fallback match by (name, containing type, member kind) for when it doesn't. A single-file compilation
        // can't resolve a parameter/return type declared in ANOTHER file (e.g. a MediatR request class in its
        // own file), so the exact docId this throwaway compilation computes can legitimately differ from the
        // one the real solution-wide scan produced — even though it's clearly still the same symbol. The
        // fallback trades a little precision (it could, rarely, latch onto the wrong overload) for correctly
        // relocating the overwhelming majority of real symbols instead of false-negative "not found".
        var candidates = new List<(SyntaxNode Node, ISymbol Symbol)>();
        foreach (var node in root.DescendantNodes())
        {
            var symbol = TryGetDeclaredSymbol(model, node);
            if (symbol == null) continue;
            candidates.Add((node, symbol));
            if (symbol.GetDocumentationCommentId() == target.Id)
                return BuildResult(tree, node);
        }

        foreach (var (node, symbol) in candidates)
        {
            if (!FuzzyMatches(symbol, target)) continue;
            return BuildResult(tree, node);
        }

        return null;
    }

    private static bool FuzzyMatches(ISymbol symbol, SymbolRecord target)
    {
        if (!string.Equals(symbol.Name, target.Name, StringComparison.Ordinal)) return false;

        var containingTypeSimpleName = symbol.ContainingType?.Name;
        var targetContainingSimpleName = target.ContainingType?.Split('.').LastOrDefault(); // ContainingType is dotted display text, e.g. "Ns.Outer.Inner"
        if (!string.Equals(containingTypeSimpleName, targetContainingSimpleName, StringComparison.Ordinal)) return false;

        return target.Kind switch
        {
            "Method" or "Constructor" => symbol is IMethodSymbol,
            "Property" => symbol is IPropertySymbol,
            "Field" => symbol is IFieldSymbol,
            "Event" => symbol is IEventSymbol,
            "Class" or "Interface" or "Struct" or "Record" or "Enum" or "Delegate" => symbol is INamedTypeSymbol,
            _ => true,
        };
    }

    private static Result BuildResult(SyntaxTree tree, SyntaxNode node)
    {
        var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        return new Result(line, ExtractSnippet(tree, node));
    }

    private static ISymbol? TryGetDeclaredSymbol(SemanticModel model, SyntaxNode node) => node switch
    {
        BaseTypeDeclarationSyntax or DelegateDeclarationSyntax or MethodDeclarationSyntax or ConstructorDeclarationSyntax
            or PropertyDeclarationSyntax or EventDeclarationSyntax or VariableDeclaratorSyntax => model.GetDeclaredSymbol(node),
        _ => null,
    };

    private const int MaxSnippetLines = 40;

    private static string ExtractSnippet(SyntaxTree tree, SyntaxNode node)
    {
        // A type declaration's own text includes its whole body — show just the header line instead of
        // potentially thousands of lines of class contents.
        if (node is BaseTypeDeclarationSyntax { OpenBraceToken.Span.Start: > 0 } typeDecl)
        {
            var headerSpan = TextSpan.FromBounds(typeDecl.SpanStart, typeDecl.OpenBraceToken.SpanStart);
            return tree.GetText().ToString(headerSpan).Trim() + " { ... }";
        }

        var fullText = node.ToFullString().Trim();
        var lines = fullText.Split('\n');
        if (lines.Length <= MaxSnippetLines) return fullText;

        return string.Join('\n', lines.Take(MaxSnippetLines)) + $"\n... ({lines.Length - MaxSnippetLines} more line(s) truncated)";
    }
}
