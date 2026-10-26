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
    /// <summary>What `slice` shows of a member's body unless asked for more. Types are cut to their header regardless.</summary>
    public const int DefaultMaxSnippetLines = 40;

    /// <param name="IsFuzzyMatch">True when the declaration was found by name, containing type and kind, because the exact docId could not be reproduced — see <see cref="Locate"/>.</param>
    /// <param name="FuzzyCandidateCount">With a fuzzy match, how many declarations in the file matched that way (more than one means overloads; the nearest to the scanned line was picked). 0 for an exact match.</param>
    public sealed record Result(int Line, string Snippet, bool IsFuzzyMatch = false, int FuzzyCandidateCount = 0);

    /// <param name="maxSnippetLines">Longest snippet to return for a member; the rest is replaced by a count of what was cut.</param>
    public static Result? Locate(string absoluteFilePath, SymbolRecord target, int maxSnippetLines = DefaultMaxSnippetLines)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSnippetLines, 1);
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
        // fallback trades a little precision for correctly relocating the overwhelming majority of real symbols
        // instead of false-negative "not found". Where it loses precision is overloads: they all share a name,
        // so of the matches the one nearest the line the scan recorded is taken (a member rarely moves far
        // between a scan and now), and the result says it was a fuzzy match so the reader can doubt it.
        var candidates = new List<(SyntaxNode Node, ISymbol Symbol)>();
        foreach (var node in root.DescendantNodes())
        {
            var symbol = TryGetDeclaredSymbol(model, node);
            if (symbol == null) continue;
            candidates.Add((node, symbol));
            if (symbol.GetDocumentationCommentId() == target.Id)
                return BuildResult(tree, node, maxSnippetLines);
        }

        var fuzzy = candidates
            .Where(c => FuzzyMatches(c.Symbol, target))
            .Select(c => (c.Node, Line: LineOf(c.Node)))
            .OrderBy(c => Math.Abs(c.Line - target.Line))
            .ThenBy(c => c.Line)
            .ToList();
        if (fuzzy.Count == 0) return null;

        return BuildResult(tree, fuzzy[0].Node, maxSnippetLines, fuzzyCandidateCount: fuzzy.Count);
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

    private static Result BuildResult(SyntaxTree tree, SyntaxNode node, int maxSnippetLines, int fuzzyCandidateCount = 0)
        => new(LineOf(node), ExtractSnippet(tree, node, maxSnippetLines), IsFuzzyMatch: fuzzyCandidateCount > 0, fuzzyCandidateCount);

    private static int LineOf(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    private static ISymbol? TryGetDeclaredSymbol(SemanticModel model, SyntaxNode node) => node switch
    {
        BaseTypeDeclarationSyntax or DelegateDeclarationSyntax or MethodDeclarationSyntax or ConstructorDeclarationSyntax
            or PropertyDeclarationSyntax or EventDeclarationSyntax or VariableDeclaratorSyntax => model.GetDeclaredSymbol(node),
        _ => null,
    };

    private static string ExtractSnippet(SyntaxTree tree, SyntaxNode node, int maxSnippetLines)
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
        if (lines.Length <= maxSnippetLines) return fullText;

        return string.Join('\n', lines.Take(maxSnippetLines)) + $"\n... ({lines.Length - maxSnippetLines} more line(s) truncated — raise --max-lines to see them)";
    }
}
