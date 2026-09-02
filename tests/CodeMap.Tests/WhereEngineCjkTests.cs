using CodeMap.Query.Impact;
using CodeMap.Query.Models;
using CodeMap.Query.Where;

namespace CodeMap.Tests;

/// <summary>
/// `where` against a codebase whose commit history is written in Japanese. Japanese puts no spaces between
/// words, so the plain "run of letters = one token" rule that works for Vietnamese and English collapses a
/// whole commit message into a single token, and term overlap can then only fire on a byte-identical query.
/// These tests pin the bigram tokenizer that fixes it, and pin that the space-separated scripts did not
/// regress in the process.
/// </summary>
[TestClass]
public class WhereEngineCjkTests
{
    /// <summary>WhereEngine.Tokenize returns a HashSet (O(1) membership is the whole point in production code),
    /// but MSTest's CollectionAssert wants the non-generic ICollection that only List implements — this adapts
    /// without weakening what's actually being asserted (set membership/equivalence, never order).</summary>
    private static List<string> Tokens(string text) => WhereEngine.Tokenize(text).ToList();

    [TestMethod]
    public void Japanese_sentence_splits_into_overlapping_bigrams_not_one_giant_token()
    {
        var tokens = Tokens("注文をキャンセル");

        CollectionAssert.AreEquivalent(
            new[] { "注文", "文を", "をキ", "キャ", "ャン", "ンセ", "セル" },
            tokens);
    }

    [TestMethod]
    public void Japanese_query_matches_a_ticket_that_words_the_same_idea_differently()
    {
        // The query is not a substring of the message: different particles, extra words, different ending.
        // Only the content bigrams (注文 / キャ / ャン / ンセ / セル) are shared.
        var index = BuildIndex(
            symbols: new[] { ("M:Orders.OrderService.Cancel", "Cancel", "Orders.OrderService", "OrderService.cs") },
            tickets: new[]
            {
                new TicketFileRecord("301", new() { "a1" }, "2026-06-14",
                    "TICKET-301: 注文をキャンセルできない不具合を修正", new() { "OrderService.cs" }),
            });

        var results = WhereEngine.Search(index, "注文のキャンセル処理");

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("M:Orders.OrderService.Cancel", results[0].SymbolId);
    }

    [TestMethod]
    public void An_unrelated_japanese_ticket_does_not_score_at_all()
    {
        var index = BuildIndex(
            symbols: new[]
            {
                ("M:Orders.OrderService.Cancel", "Cancel", "Orders.OrderService", "OrderService.cs"),
                ("M:Stock.InventoryService.Reduce", "Reduce", "Stock.InventoryService", "InventoryService.cs"),
            },
            tickets: new[]
            {
                new TicketFileRecord("301", new() { "a1" }, "2026-06-14", "注文をキャンセルできない", new() { "OrderService.cs" }),
                new TicketFileRecord("302", new() { "a2" }, "2026-06-15", "在庫数が減らない", new() { "InventoryService.cs" }),
            });

        var results = WhereEngine.Search(index, "注文のキャンセル");

        Assert.AreEqual(1, results.Count, "the inventory ticket shares no content bigram and must not surface");
        Assert.AreEqual("M:Orders.OrderService.Cancel", results[0].SymbolId);
    }

    [TestMethod]
    public void A_latin_identifier_inside_a_japanese_message_still_earns_the_named_directly_bonus()
    {
        // Script boundary split: "OrderService" must stay one Latin token even though it is glued to katakana.
        var index = BuildIndex(
            symbols: new[]
            {
                ("M:Orders.OrderService.Cancel", "Cancel", "Orders.OrderService", "OrderService.cs"),
                ("M:Orders.OrderService.Create", "Create", "Orders.OrderService", "OrderService.cs"),
            },
            tickets: new[]
            {
                new TicketFileRecord("301", new() { "a1" }, "2026-06-14",
                    "TICKET-301: 注文のキャンセルが失敗する - Cancel を修正", new() { "OrderService.cs" }),
            });

        var results = WhereEngine.Search(index, "注文のキャンセル");

        Assert.AreEqual("M:Orders.OrderService.Cancel", results[0].SymbolId);
        Assert.IsTrue(
            results.Single(r => r.SymbolId == "M:Orders.OrderService.Cancel").Score >
            results.Single(r => r.SymbolId == "M:Orders.OrderService.Create").Score);
    }

    [TestMethod]
    public void Katakana_glued_to_a_latin_identifier_splits_at_the_script_boundary()
    {
        var tokens = Tokens("キャンセルOrderService");

        CollectionAssert.Contains(tokens, "orderservice");
        CollectionAssert.Contains(tokens, "キャ");
        CollectionAssert.DoesNotContain(tokens, "ルo", "a token must never straddle two scripts");
    }

    [TestMethod]
    public void Halfwidth_katakana_is_folded_onto_normal_katakana()
    {
        CollectionAssert.AreEquivalent(
            Tokens("キャンセル"),
            Tokens("ｷｬﾝｾﾙ"));
    }

    [TestMethod]
    public void Fullwidth_latin_is_folded_onto_ascii()
    {
        CollectionAssert.AreEquivalent(
            Tokens("Cancel"),
            Tokens("Ｃａｎｃｅｌ"));
    }

    [TestMethod]
    public void A_hiragana_only_bigram_is_dropped_as_a_grammatical_stop_word()
    {
        // でき / きな / ない are inflection, not content — they are what makes 「削除できない」 collide with 「合わない」.
        var tokens = Tokens("削除できない");

        CollectionAssert.AreEquivalent(new[] { "削除", "除で" }, tokens);
    }

    [TestMethod]
    public void Two_unrelated_japanese_phrases_no_longer_collide_on_their_shared_negation_ending()
    {
        var a = Tokens("注文の削除ができない不具合を修正");
        var b = Tokens("在庫数が合わない");

        CollectionAssert.AreEqual(Array.Empty<string>(), a.Intersect(b).ToArray());
    }

    [TestMethod]
    public void An_all_hiragana_phrase_keeps_its_bigrams_because_there_is_no_content_half_to_fall_back_on()
    {
        // A term genuinely written in hiragana (ひもづけ = "linking") must stay searchable rather than tokenize to
        // nothing — the stop-word rule only applies where the segment has kanji/katakana to carry the meaning.
        var tokens = Tokens("ひもづけ");

        CollectionAssert.AreEquivalent(new[] { "ひも", "もづ", "づけ" }, tokens);
    }

    [TestMethod]
    public void A_lone_ideograph_survives_the_minimum_length_filter()
    {
        // 金 is a whole word on its own; the length>=2 rule that usefully drops "a"/"I" must not drop it.
        CollectionAssert.Contains(Tokens("金 amount"), "金");
    }

    [TestMethod]
    public void Space_separated_scripts_still_tokenize_by_word_not_by_bigram()
    {
        CollectionAssert.AreEquivalent(new[] { "hủy", "đơn", "hàng" }, Tokens("hủy đơn hàng"));
        CollectionAssert.AreEquivalent(new[] { "cancel", "order" }, Tokens("Cancel Order"));
    }

    [TestMethod]
    public void Decomposed_and_composed_vietnamese_diacritics_produce_the_same_tokens()
    {
        // Same word, NFD vs NFC. Before NFKC normalization these were two different tokens that never matched.
        CollectionAssert.AreEquivalent(
            Tokens("hủy".Normalize(System.Text.NormalizationForm.FormD)),
            Tokens("hủy".Normalize(System.Text.NormalizationForm.FormC)));
    }

    private static ImpactIndex BuildIndex(
        (string Id, string Name, string ContainingType, string File)[] symbols,
        IEnumerable<TicketFileRecord>? tickets = null)
    {
        var symbolRecords = symbols.Select(s => new SymbolRecord
        {
            Id = s.Id,
            Kind = "Method",
            Name = s.Name,
            ContainingType = s.ContainingType,
            Project = "SampleProj",
            File = s.File,
            Line = 1,
            Accessibility = "Public",
        }).ToList();

        return new ImpactIndex
        {
            SymbolsById = symbolRecords.ToDictionary(s => s.Id, StringComparer.Ordinal),
            ReverseEdges = new(StringComparer.Ordinal),
            EntryPointsById = new(StringComparer.Ordinal),
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
