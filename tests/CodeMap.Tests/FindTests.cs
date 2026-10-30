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
        // The full display name is an exact match (100), above the `Type.Member` ending (80) that "OrderService.Cancel" gets.
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

    // ContainingType is fully qualified in a real index, so `Type.Member` is never the whole display name. It used
    // to fall in the same substring tier as every member that merely starts with the same letters (found on
    // eShopOnWeb: ManageController.Disable2fa tied with Disable2faWarning), and as the interface's twin of a method.
    [TestMethod]
    public void Type_dot_member_outranks_longer_member_names_and_the_interface_twin()
    {
        var symbols = new List<SymbolRecord>
        {
            Sym("M:Shop.Web.ManageController.Disable2fa", "Disable2fa", "Shop.Web.ManageController"),
            Sym("M:Shop.Web.ManageController.Disable2faWarning", "Disable2faWarning", "Shop.Web.ManageController"),
            Sym("M:Shop.Web.Interfaces.IBasketService.Add(System.Int32)", "Add", "Shop.Web.Interfaces.IBasketService"),
            Sym("M:Shop.Web.Services.BasketService.Add(System.Int32)", "Add", "Shop.Web.Services.BasketService"),
        };

        var longerName = FindCommand.Search(symbols, "ManageController.Disable2fa");
        var twin = FindCommand.Search(symbols, "BasketService.Add");

        Assert.AreEqual("M:Shop.Web.ManageController.Disable2fa", longerName[0].Symbol.Id);
        Assert.IsTrue(longerName[0].Score > longerName[1].Score, "a longer member name must rank below the exact member");
        Assert.AreEqual("M:Shop.Web.Services.BasketService.Add(System.Int32)", twin[0].Symbol.Id);
        Assert.IsTrue(twin[0].Score > twin[1].Score, "the interface method must rank below the class method");
    }

    [TestMethod] // overloads are the same Type.Member, so they must still tie: that is what asks the caller which one
    public void Overloads_of_one_type_dot_member_still_tie()
    {
        var symbols = new List<SymbolRecord>
        {
            Sym("M:Shop.Svc.Cancel(System.Int32)", "Cancel", "Shop.Svc"),
            Sym("M:Shop.Svc.Cancel(System.String)", "Cancel", "Shop.Svc"),
            Sym("M:Shop.Svc.CancelAll", "CancelAll", "Shop.Svc"),
        };

        var results = FindCommand.Search(symbols, "Svc.Cancel");

        Assert.AreEqual(results[0].Score, results[1].Score);
        Assert.IsTrue(results[1].Score > results[2].Score);
        Assert.AreEqual("M:Shop.Svc.CancelAll", results[2].Symbol.Id);
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
