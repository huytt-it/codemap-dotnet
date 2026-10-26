using CodeMap.Query.Impact;
using CodeMap.Query.Models;
using CodeMap.Query.Where;

namespace CodeMap.Tests;

/// <summary>
/// Ranking behaviour of `where` that the per-signal tests in `WhereEngineTests` don't reach: how a ticket ID in
/// the query is recognised, how symbol names are split into words, which function words are ignored, and how
/// symbols that earned the same score are ordered. Every test pins a case where the old behaviour put the wrong
/// symbol in front (or the right one nowhere), not just that the new code runs.
/// </summary>
[TestClass]
public class WhereEngineRankingTests
{
    private const string UnrelatedMessage = "completely unrelated wording";

    // ---- ticket ID matching -------------------------------------------------------------------------------

    [DataTestMethod]
    [DataRow("SHO-1234")]
    [DataRow("sho-1234")]
    [DataRow("lỗi sho-1234 hôm qua")]
    [DataRow("SHO-1234を確認")]
    [DataRow("ＳＨＯ－１２３４")] // fullwidth, as typed on a Japanese IME
    public void A_ticket_id_with_letters_matches_as_a_whole_phrase_whatever_its_case(string query)
    {
        // The tokenizer drops '-' and '_', so the old token comparison ("sho", "1234") could never equal "SHO-1234".
        var index = IndexWithOneTicket("SHO-1234");

        var results = WhereEngine.Search(index, query);

        Assert.AreEqual(1, results.Count, $"'{query}' names ticket SHO-1234");
        StringAssert.StartsWith(results[0].Reasons[0], "Exact match on ticket");
    }

    [TestMethod]
    public void A_ticket_id_written_with_an_underscore_matches_as_a_whole_phrase()
    {
        var index = IndexWithOneTicket("SHO_1234");

        Assert.AreEqual(1, WhereEngine.Search(index, "sho_1234").Count);
    }

    [DataTestMethod]
    [DataRow("lỗi 500 khi thanh toán")]
    [DataRow("500")]
    [DataRow("500 lỗi")]
    public void A_bare_number_in_the_query_is_not_a_numeric_ticket_id(string query)
    {
        // The default ticket pattern captures digits only, so ticket "500" is stored as plain "500". Treating any
        // 3-6 digit number in a question (an HTTP status, a year, a quantity) as that ticket gave it a perfect score.
        var index = IndexWithOneTicket("500");

        Assert.AreEqual(0, WhereEngine.Search(index, query).Count, $"'{query}' only mentions the number 500");
    }

    [DataTestMethod]
    [DataRow("#500")]
    [DataRow("fix #500 ngay")]
    [DataRow("TICKET-500")]
    [DataRow("ticket-500")]
    [DataRow("BUG-500")]
    [DataRow("jira-500")]
    [DataRow("#500を修正")]
    public void A_numeric_ticket_id_matches_behind_one_of_the_default_prefixes(string query)
    {
        var index = IndexWithOneTicket("500");

        var results = WhereEngine.Search(index, query);

        Assert.AreEqual(1, results.Count, $"'{query}' names ticket 500");
        StringAssert.StartsWith(results[0].Reasons[0], "Exact match on ticket");
    }

    [DataTestMethod]
    [DataRow("#5000")]       // a longer number, not ticket 500
    [DataRow("#50")]         // a shorter one
    [DataRow("DEBUG-500")]   // BUG- in the middle of a word
    [DataRow("#500a")]       // letter glued to the digits
    public void A_numeric_ticket_id_needs_the_whole_number_and_a_clean_prefix(string query)
    {
        var index = IndexWithOneTicket("500");

        Assert.AreEqual(0, WhereEngine.Search(index, query).Count, $"'{query}' does not name ticket 500");
    }

    [DataTestMethod]
    [DataRow("SHO-123")]     // the number runs on past SHO-12
    [DataRow("XSHO-12")]     // the id sits inside a longer word
    [DataRow("SHO-12x")]     // letter glued to the end
    public void A_ticket_id_with_letters_is_not_matched_inside_a_longer_word_or_number(string query)
    {
        var index = IndexWithOneTicket("SHO-12");

        Assert.AreEqual(0, WhereEngine.Search(index, query).Count, $"'{query}' does not name ticket SHO-12");
    }

    private static ImpactIndex IndexWithOneTicket(string ticketId) =>
        BuildIndex(
            symbols: new[] { ("M:Orders.OrderService.Cancel", "Cancel", "Orders.OrderService", "OrderService.cs") },
            tickets: new[] { new TicketFileRecord(ticketId, new() { "a1" }, "2026-06-14", UnrelatedMessage, new() { "OrderService.cs" }) });

    // ---- helpers ------------------------------------------------------------------------------------------

    private static ImpactIndex BuildIndex(
        (string Id, string Name, string ContainingType, string File)[] symbols,
        IEnumerable<TicketFileRecord>? tickets = null,
        IEnumerable<EntryPoint>? entryPoints = null)
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

        return new ImpactIndex
        {
            SymbolsById = symbolRecords.ToDictionary(s => s.Id, StringComparer.Ordinal),
            ReverseEdges = new(StringComparer.Ordinal),
            EntryPointsById = (entryPoints ?? Enumerable.Empty<EntryPoint>()).ToDictionary(e => e.Id, StringComparer.Ordinal),
            Tickets = (tickets ?? Enumerable.Empty<TicketFileRecord>()).ToList(),
            CoChanges = new(),
            FrontendCallsById = new(StringComparer.Ordinal),
            ApiLinksByBackendId = new(StringComparer.Ordinal),
            Diagnostics = null,
            Meta = null,
            ConfirmedImplementationTypes = new(),
            InterfaceCallSiteCandidateTypes = new(),
        };
    }
}
