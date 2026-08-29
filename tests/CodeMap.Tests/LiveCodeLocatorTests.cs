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

    private static SymbolRecord Sym(string id, string kind, string name, string? containingType) => new()
    {
        Id = id,
        Kind = kind,
        Name = name,
        ContainingType = containingType,
        Project = "Test",
        File = "x.cs",
        Line = 1,
        Accessibility = "Public",
    };
}
