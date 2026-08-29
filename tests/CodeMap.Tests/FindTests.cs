using CodeMap.Query.Find;
using CodeMap.Query.Models;

namespace CodeMap.Tests;

/// <summary>`find` (spec section 3): approximate name match, ranked. No scan needed — pure function of a symbol list.</summary>
[TestClass]
public class FindTests
{
    private static readonly List<SymbolRecord> Symbols = new()
    {
        Sym("M:Orders.OrderService.Cancel(System.Int32)", "Cancel", "Orders.OrderService"),
        Sym("M:Orders.OrderService.CancelBatch(System.Int32[])", "CancelBatch", "Orders.OrderService"),
        Sym("M:Orders.FakeOrderService.Cancel(System.Int32)", "Cancel", "Orders.FakeOrderService"),
        Sym("T:Orders.OrderService", "OrderService", null),
    };

    [TestMethod]
    public void Exact_display_name_match_ranks_first()
    {
        // "OrderService.Cancel" alone would substring-match BOTH OrderService.Cancel and FakeOrderService.Cancel —
        // use the full display name so this query is unambiguous.
        var results = FindCommand.Search(Symbols, "Orders.OrderService.Cancel");
        Assert.AreEqual("M:Orders.OrderService.Cancel(System.Int32)", results[0].Symbol.Id);
    }

    [TestMethod]
    public void Substring_query_matches_multiple_candidates()
    {
        var results = FindCommand.Search(Symbols, "Cancel");
        // Cancel, CancelBatch, and FakeOrderService.Cancel all contain "Cancel"
        Assert.IsTrue(results.Count >= 3);
    }

    [TestMethod]
    public void No_match_returns_an_empty_list_not_a_crash()
    {
        var results = FindCommand.Search(Symbols, "ThisMatchesNothingAtAll");
        Assert.AreEqual(0, results.Count);
    }

    [TestMethod]
    public void Match_is_case_insensitive()
    {
        var results = FindCommand.Search(Symbols, "orderservice.cancel");
        Assert.IsTrue(results.Count > 0);
    }

    private static SymbolRecord Sym(string id, string name, string? containingType) => new()
    {
        Id = id,
        Kind = containingType == null ? "Class" : "Method",
        Name = name,
        ContainingType = containingType,
        Project = "Orders.Core",
        File = "x.cs",
        Line = 1,
        Accessibility = "Public",
    };
}
