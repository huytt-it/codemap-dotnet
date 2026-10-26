using CodeMap.Query.Models;
using CodeMap.Roslyn.Slice;

namespace CodeMap.Tests;

/// <summary>
/// spec section 7, "Đọc code lúc query, không lưu code trong index": slice re-parses the file from disk instead
/// of trusting anything cached — verified here by literally editing the file between two calls with no re-scan.
/// </summary>
[TestClass]
public class LiveCodeLocatorTests
{
    [TestMethod]
    public void Locates_a_method_by_docId_and_returns_its_current_line_and_text()
    {
        var dir = TestPaths.NewTempDir();
        var file = Path.Combine(dir, "Foo.cs");
        File.WriteAllText(file, "namespace X;\npublic class Foo\n{\n    public int Bar() => 1;\n}\n");

        var result = LiveCodeLocator.Locate(file, Sym("M:X.Foo.Bar", "Method", "Bar", "X.Foo"));

        Assert.IsNotNull(result);
        Assert.AreEqual(4, result!.Line);
        StringAssert.Contains(result.Snippet, "Bar() => 1");
    }

    [TestMethod]
    public void Editing_the_file_between_two_locates_is_reflected_immediately_no_rescan_needed()
    {
        var dir = TestPaths.NewTempDir();
        var file = Path.Combine(dir, "Foo.cs");
        File.WriteAllText(file, "namespace X;\npublic class Foo\n{\n    public int Bar() => 1;\n}\n");

        var symbol = Sym("M:X.Foo.Bar", "Method", "Bar", "X.Foo");
        var before = LiveCodeLocator.Locate(file, symbol);
        StringAssert.Contains(before!.Snippet, "=> 1");

        File.WriteAllText(file, "namespace X;\npublic class Foo\n{\n    public int Bar() => 2;\n}\n");
        var after = LiveCodeLocator.Locate(file, symbol);

        StringAssert.Contains(after!.Snippet, "=> 2");
    }

    [TestMethod]
    public void Renamed_or_deleted_symbol_returns_null_instead_of_stale_or_wrong_code()
    {
        var dir = TestPaths.NewTempDir();
        var file = Path.Combine(dir, "Foo.cs");
        File.WriteAllText(file, "namespace X;\npublic class Foo\n{\n    public int Baz() => 1;\n}\n"); // renamed Bar -> Baz

        var result = LiveCodeLocator.Locate(file, Sym("M:X.Foo.Bar", "Method", "Bar", "X.Foo"));

        Assert.IsNull(result);
    }

    [TestMethod]
    public void Missing_file_returns_null_without_throwing()
    {
        var result = LiveCodeLocator.Locate(
            Path.Combine(TestPaths.NewTempDir(), "DoesNotExist.cs"), Sym("M:X.Foo.Bar", "Method", "Bar", "X.Foo"));

        Assert.IsNull(result);
    }

    [TestMethod]
    public void Type_declaration_snippet_is_just_the_header_not_the_whole_body()
    {
        var dir = TestPaths.NewTempDir();
        var file = Path.Combine(dir, "Foo.cs");
        File.WriteAllText(file, "namespace X;\npublic class Foo : System.Exception\n{\n    public int Bar() => 1;\n}\n");

        var result = LiveCodeLocator.Locate(file, Sym("T:X.Foo", "Class", "Foo", null));

        Assert.IsNotNull(result);
        StringAssert.Contains(result!.Snippet, "class Foo");
        Assert.IsFalse(result.Snippet.Contains("Bar()")); // body content must NOT leak into a type header snippet
    }

    [TestMethod] // regression: found on eShopOnWeb — a MediatR handler's docId embeds its request type's full name, which lives in a DIFFERENT file a single-file compilation can't resolve
    public void Falls_back_to_name_plus_containing_type_match_when_the_docId_cant_be_exactly_reproduced()
    {
        var dir = TestPaths.NewTempDir();
        // Handle's parameter type "SomeRequest" is declared in ANOTHER file (not parsed here), so this
        // throwaway single-file compilation cannot resolve it — the docId it computes for Handle will differ
        // from the one the real solution-wide scan produced.
        var file = Path.Combine(dir, "Handler.cs");
        File.WriteAllText(file, "namespace X;\npublic class Handler\n{\n    public bool Handle(SomeRequest request) => true;\n}\n");

        // This is the docId a REAL scan (with SomeRequest resolved) would have produced — deliberately NOT
        // reproducible by this single-file recompilation.
        var target = Sym("M:X.Handler.Handle(X.SomeRequest)", "Method", "Handle", "X.Handler");

        var result = LiveCodeLocator.Locate(file, target);

        Assert.IsNotNull(result, "fuzzy fallback (name + containing type) should still find Handle");
        StringAssert.Contains(result!.Snippet, "Handle(SomeRequest request)");
    }

    [TestMethod]
    public void An_exact_docId_match_is_not_flagged_as_fuzzy()
    {
        var dir = TestPaths.NewTempDir();
        var file = Path.Combine(dir, "Foo.cs");
        File.WriteAllText(file, "namespace X;\npublic class Foo\n{\n    public int Bar() => 1;\n}\n");

        var result = LiveCodeLocator.Locate(file, Sym("M:X.Foo.Bar", "Method", "Bar", "X.Foo"));

        Assert.IsFalse(result!.IsFuzzyMatch);
        Assert.AreEqual(0, result.FuzzyCandidateCount);
    }

    [TestMethod] // overloads share a name; before, the first one in the file always won
    public void Fuzzy_match_among_overloads_takes_the_one_nearest_the_scanned_line()
    {
        var dir = TestPaths.NewTempDir();
        var file = Path.Combine(dir, "Handler.cs");
        // Neither parameter type is declared here, so no docId can be reproduced and only the name matches.
        File.WriteAllText(file, "namespace X;\npublic class Handler\n{\n    public bool Handle(RequestA a) => true;\n\n    public bool Handle(RequestB b) => false;\n\n    public bool Handle(RequestC c) => true;\n}\n");

        var second = LiveCodeLocator.Locate(file, Sym("M:X.Handler.Handle(X.RequestB)", "Method", "Handle", "X.Handler", line: 6));
        var third = LiveCodeLocator.Locate(file, Sym("M:X.Handler.Handle(X.RequestC)", "Method", "Handle", "X.Handler", line: 8));

        Assert.AreEqual(6, second!.Line);
        StringAssert.Contains(second.Snippet, "RequestB b");
        Assert.AreEqual(8, third!.Line);
        StringAssert.Contains(third.Snippet, "RequestC c");
        Assert.IsTrue(second.IsFuzzyMatch);
        Assert.AreEqual(3, second.FuzzyCandidateCount);
    }

    [TestMethod]
    public void Fuzzy_match_with_a_single_candidate_is_flagged_with_a_count_of_one()
    {
        var dir = TestPaths.NewTempDir();
        var file = Path.Combine(dir, "Handler.cs");
        File.WriteAllText(file, "namespace X;\npublic class Handler\n{\n    public bool Handle(SomeRequest request) => true;\n}\n");

        var result = LiveCodeLocator.Locate(file, Sym("M:X.Handler.Handle(X.SomeRequest)", "Method", "Handle", "X.Handler", line: 4));

        Assert.IsTrue(result!.IsFuzzyMatch);
        Assert.AreEqual(1, result.FuzzyCandidateCount);
    }

    [TestMethod]
    public void Snippet_length_limit_is_a_parameter_and_defaults_to_40()
    {
        var dir = TestPaths.NewTempDir();
        var file = Path.Combine(dir, "Foo.cs");
        var body = string.Join("\n", Enumerable.Range(1, 60).Select(i => $"        var v{i} = {i};"));
        File.WriteAllText(file, $"namespace X;\npublic class Foo\n{{\n    public void Bar()\n    {{\n{body}\n    }}\n}}\n");
        var symbol = Sym("M:X.Foo.Bar", "Method", "Bar", "X.Foo");

        var byDefault = LiveCodeLocator.Locate(file, symbol);
        var wide = LiveCodeLocator.Locate(file, symbol, maxSnippetLines: 200);
        var narrow = LiveCodeLocator.Locate(file, symbol, maxSnippetLines: 5);

        StringAssert.Contains(byDefault!.Snippet, "23 more line(s) truncated");
        Assert.IsFalse(wide!.Snippet.Contains("truncated"));
        StringAssert.Contains(wide.Snippet, "var v60 = 60;");
        StringAssert.Contains(narrow!.Snippet, "58 more line(s) truncated");
    }

    [TestMethod]
    public void A_snippet_limit_below_one_is_rejected()
    {
        var file = Path.Combine(TestPaths.NewTempDir(), "Foo.cs");
        File.WriteAllText(file, "namespace X;\npublic class Foo\n{\n    public int Bar() => 1;\n}\n");

        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => LiveCodeLocator.Locate(file, Sym("M:X.Foo.Bar", "Method", "Bar", "X.Foo"), maxSnippetLines: 0));
    }

    private static SymbolRecord Sym(string id, string kind, string name, string? containingType, int line = 1) => new()
    {
        Id = id,
        Kind = kind,
        Name = name,
        ContainingType = containingType,
        Project = "Test",
        File = "x.cs",
        Line = line,
        Accessibility = "Public",
    };
}
