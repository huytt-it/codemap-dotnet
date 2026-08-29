using CodeMap.Query.Impact;
using CodeMap.Query.Models;
using CodeMap.Query.Where;

namespace CodeMap.Tests;

/// <summary>
/// WhereEngine unit tests against synthetic in-memory data — spec section 3, "find vs where" and the three
/// ranking sources. `WhereEngineIntegrationTests` covers the literal spec section 9 acceptance criterion end to
/// end through a real git repo + real Roslyn scan; these tests isolate each of the three signals.
/// </summary>
[TestClass]
public class WhereEngineTests
{
    [TestMethod]
    public void Ticket_message_term_overlap_surfaces_symbols_declared_in_the_touched_file()
    {
        var index = BuildIndex(
            symbols: new[] { ("M:Orders.OrderService.Cancel", "Cancel", "Orders.OrderService", "OrderService.cs") },
            tickets: new[] { new TicketFileRecord("4821", new() { "a1" }, "2026-06-14", "fix hủy đơn khi thanh toán", new() { "OrderService.cs" }) });

        var results = WhereEngine.Search(index, "hủy đơn hàng");

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("M:Orders.OrderService.Cancel", results[0].SymbolId);
    }

    [TestMethod]
    public void Symbol_named_directly_in_the_ticket_message_outranks_a_sibling_in_the_same_file()
    {
        var index = BuildIndex(
            symbols: new[]
            {
                ("M:Orders.OrderService.Cancel", "Cancel", "Orders.OrderService", "OrderService.cs"),
                ("M:Orders.OrderService.Create", "Create", "Orders.OrderService", "OrderService.cs"),
            },
            tickets: new[] { new TicketFileRecord("4821", new() { "a1" }, "2026-06-14", "fix hủy đơn hàng - sửa Cancel", new() { "OrderService.cs" }) });

        var results = WhereEngine.Search(index, "hủy đơn hàng");

        Assert.AreEqual("M:Orders.OrderService.Cancel", results[0].SymbolId);
        var cancelScore = results.Single(r => r.SymbolId == "M:Orders.OrderService.Cancel").Score;
        var createScore = results.Single(r => r.SymbolId == "M:Orders.OrderService.Create").Score;
        Assert.IsTrue(cancelScore > createScore);
    }

    [TestMethod]
    public void Exact_ticket_number_in_the_query_matches_even_without_message_overlap()
    {
        var index = BuildIndex(
            symbols: new[] { ("M:Orders.OrderService.Cancel", "Cancel", "Orders.OrderService", "OrderService.cs") },
            tickets: new[] { new TicketFileRecord("4821", new() { "a1" }, "2026-06-14", "completely unrelated wording", new() { "OrderService.cs" }) });

        var results = WhereEngine.Search(index, "#4821");

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("M:Orders.OrderService.Cancel", results[0].SymbolId);
    }

    [TestMethod]
    public void Route_term_overlap_scores_the_backend_entry_point_symbol()
    {
        var index = BuildIndex(
            symbols: new[] { ("M:Api.OrdersController.Cancel", "Cancel", "Api.OrdersController", "OrdersController.cs") },
            entryPoints: new[] { new EntryPoint("M:Api.OrdersController.Cancel", "http", "POST", "api/orders/cancel") });

        var results = WhereEngine.Search(index, "cancel");

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("M:Api.OrdersController.Cancel", results[0].SymbolId);
    }

    [TestMethod]
    public void Fe_feature_term_overlap_scores_the_linked_backend_symbol()
    {
        var index = BuildIndex(
            symbols: new[] { ("M:Api.OrdersController.Delete", "Delete", "Api.OrdersController", "OrdersController.cs") },
            entryPoints: new[] { new EntryPoint("M:Api.OrdersController.Delete", "http", "DELETE", "api/orders/{id}") },
            frontendCalls: new[] { new FrontendCall("fe:x.ts:1", "x.ts", 1, "DELETE", "'/x'", "api/orders/{*}", "orders", "high") },
            apiLinks: new[] { new ApiLink("fe:x.ts:1", "M:Api.OrdersController.Delete", "exact") });

        var results = WhereEngine.Search(index, "orders");

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("M:Api.OrdersController.Delete", results[0].SymbolId);
    }

    [TestMethod]
    public void Name_substring_scores_when_the_query_is_already_code_like()
    {
        var index = BuildIndex(symbols: new[] { ("M:Orders.OrderService.Cancel", "Cancel", "Orders.OrderService", "OrderService.cs") });

        var results = WhereEngine.Search(index, "OrderService Cancel");

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("M:Orders.OrderService.Cancel", results[0].SymbolId);
    }

    [TestMethod]
    public void No_signal_at_all_returns_an_empty_list_instead_of_guessing()
    {
        var index = BuildIndex(symbols: new[] { ("M:Orders.OrderService.Cancel", "Cancel", "Orders.OrderService", "OrderService.cs") });

        var results = WhereEngine.Search(index, "completely unrelated query text");

        Assert.AreEqual(0, results.Count);
    }

    [TestMethod] // the literal spec section 9 acceptance criterion, with realistic decoy noise around it
    public void Vietnamese_business_query_finds_the_right_method_in_the_top_5_among_noise()
    {
        var symbols = new List<(string, string, string, string)>
        {
            ("M:Orders.OrderService.Cancel", "Cancel", "Orders.OrderService", "OrderService.cs"),
            ("M:Orders.OrderService.Create", "Create", "Orders.OrderService", "OrderService.cs"),
            ("M:Orders.OrderService.Update", "Update", "Orders.OrderService", "OrderService.cs"),
            ("M:Billing.InvoiceService.Generate", "Generate", "Billing.InvoiceService", "InvoiceService.cs"),
            ("M:Shipping.ShipmentService.Dispatch", "Dispatch", "Shipping.ShipmentService", "ShipmentService.cs"),
        };
        var tickets = new List<TicketFileRecord>
        {
            new("4821", new() { "a1" }, "2026-06-14", "Fix TICKET-4821: hủy đơn hàng khi khách đã thanh toán, sửa OrderService.Cancel", new() { "OrderService.cs" }),
            new("5010", new() { "a2" }, "2026-07-01", "TICKET-5010: cải thiện hiệu suất xuất hóa đơn", new() { "InvoiceService.cs" }),
        };

        var index = BuildIndex(symbols.ToArray(), tickets: tickets);
        var results = WhereEngine.Search(index, "hủy đơn hàng");

        var top5 = results.Take(5).Select(r => r.SymbolId).ToList();
        CollectionAssert.Contains(top5, "M:Orders.OrderService.Cancel");
        Assert.AreEqual("M:Orders.OrderService.Cancel", results[0].SymbolId); // named directly in the matching ticket, so it should lead
    }

    private static ImpactIndex BuildIndex(
        (string Id, string Name, string ContainingType, string File)[] symbols,
        IEnumerable<TicketFileRecord>? tickets = null,
        IEnumerable<EntryPoint>? entryPoints = null,
        IEnumerable<FrontendCall>? frontendCalls = null,
        IEnumerable<ApiLink>? apiLinks = null)
    {
        var symbolRecords = symbols.Select(s => new SymbolRecord
        {
            Id = s.Id,
            Kind = "Method",
            Name = s.Name,
            ContainingType = s.ContainingType,
            Project = "TestProject",
            File = s.File,
            Line = 1,
            Accessibility = "Public",
        }).ToList();

        var epList = (entryPoints ?? Enumerable.Empty<EntryPoint>()).ToList();

        return new ImpactIndex
        {
            SymbolsById = symbolRecords.ToDictionary(s => s.Id, StringComparer.Ordinal),
            ReverseEdges = new(StringComparer.Ordinal),
            EntryPointsById = epList.ToDictionary(e => e.Id, StringComparer.Ordinal),
            Tickets = (tickets ?? Enumerable.Empty<TicketFileRecord>()).ToList(),
            CoChanges = new(),
            FrontendCallsById = (frontendCalls ?? Enumerable.Empty<FrontendCall>()).ToDictionary(c => c.Id, StringComparer.Ordinal),
            ApiLinksByBackendId = (apiLinks ?? Enumerable.Empty<ApiLink>()).GroupBy(l => l.BackendId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal),
            Diagnostics = null,
            Meta = null,
            ConfirmedImplementationTypes = new(),
            InterfaceCallSiteCandidateTypes = new(),
        };
    }
}
