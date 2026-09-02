using System.Text;
using System.Text.RegularExpressions;
using CodeMap.Query.Impact;
using CodeMap.Query.Models;

namespace CodeMap.Query.Where;

/// <summary>
/// Spec section 3, "find vs where": `find` assumes you already know the symbol name; `where` bridges business
/// vocabulary ("hủy đơn hàng", a ticket description) to a symbol — the missing step between a ticket and code.
/// No AI/embeddings (fully offline, spec section 1): purely lexical, three independent signals summed into one
/// score (spec section 3's "khớp trên ba nguồn, xếp hạng theo tổng điểm"). Reuses ImpactIndex — it already loads
/// everything this needs (symbols, entry points, tickets, frontend calls, api-links).
/// </summary>
public static partial class WhereEngine
{
    private const int MaxResults = 10;

    /// <summary>Cap on printed reasons per candidate. Without it, a "God file" a couple hundred tickets happen
    /// to touch would print a couple hundred near-identical reason lines — noise for a human, and wasted tokens
    /// for the tool's actual purpose (attaching `where` output to an AI chat, spec section 1).</summary>
    private const int MaxReasons = 5;

    // No formula is given in the spec (same situation as ImpactEngine's risk score) — this ordering is a
    // judgment call: a past ticket's message is the most direct evidence a change was about this exact business
    // concept; a route/FE feature name is structurally closer to business language than a raw method name; a
    // bare name substring is the weakest signal (real risk of false positives on common English words) and, for
    // a non-English query, usually won't fire at all — it's there for when the query already IS code-like.
    private const double TicketWeight = 3.0;
    private const double RouteWeight = 2.0;
    private const double NameWeight = 1.0;

    /// <summary>Extra multiplier when a ticket's own commit message names the symbol directly (e.g. "... sửa OrderService.Cancel") — the concrete link from a Vietnamese description to an actual identifier that plain token overlap can't provide on its own.</summary>
    private const double NamedInTicketBonus = 2.0;

    public static List<WhereCandidate> Search(ImpactIndex index, string query)
    {
        var queryTokens = Tokenize(query);
        if (queryTokens.Count == 0) return new();

        // Raw (score, reason) hits per symbol, kept separate rather than summed as they arrive: a symbol a
        // couple hundred tickets happen to touch (a "God file") must not out-rank one two tickets genuinely
        // agree on just by sheer count, so the count-vs-relevance tradeoff is resolved once, in BuildCandidate,
        // after every source has reported in.
        var hits = new Dictionary<string, List<(double Score, string Reason)>>(StringComparer.Ordinal);
        void Add(string symbolId, double score, string reason)
        {
            if (score <= 0 || !index.SymbolsById.ContainsKey(symbolId)) return;
            if (!hits.TryGetValue(symbolId, out var list)) hits[symbolId] = list = new();
            list.Add((score, reason));
        }

        ScoreTickets(index, queryTokens, Add);
        ScoreRoutesAndFeatures(index, queryTokens, Add);
        ScoreNameSubstrings(index, queryTokens, Add);

        return hits
            .Select(kv => BuildCandidate(index, kv.Key, kv.Value))
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.SymbolId, StringComparer.Ordinal)
            .Take(MaxResults)
            .ToList();
    }

    /// <summary>
    /// Combines every raw hit one symbol received into the one score and bounded reason list it's shown with.
    /// The strongest single hit sets the base score; each further corroborating hit adds a shrinking log bonus
    /// (log2(1) = 0, so a symbol with only one hit is scored exactly as before — this only changes symbols with
    /// several). That keeps "more historical evidence" a plus without letting raw touch-count on a God file
    /// drown out symbols with fewer, more targeted matches.
    /// </summary>
    private static WhereCandidate BuildCandidate(ImpactIndex index, string symbolId, List<(double Score, string Reason)> rawHits)
    {
        var sorted = rawHits.OrderByDescending(h => h.Score).ToList();
        var score = sorted[0].Score + Math.Log2(sorted.Count);

        var reasons = sorted.Take(MaxReasons).Select(h => h.Reason).ToList();
        if (sorted.Count > MaxReasons) reasons.Add($"(+{sorted.Count - MaxReasons} more reason(s))");

        return new WhereCandidate(symbolId, DisplayNameOf(index, symbolId), Math.Round(score, 2), reasons);
    }

    /// <summary>Source 2 (spec section 3): "ticket-files.jsonl — ticket cũ có mô tả tương tự đã sửa file nào". The only signal that can realistically bridge a natural-language query to English identifiers — a human already wrote that bridge once, in the commit message. It therefore only fires when the query is in the same language the team writes commits in.</summary>
    private static void ScoreTickets(ImpactIndex index, HashSet<string> queryTokens, Action<string, double, string> add)
    {
        // Symbol names get tokenized at most once per Search call, not once per (ticket, symbol) pair — on a
        // large codebase the same file (and its symbols) is touched by many tickets, and re-running the
        // tokenizer over identical input on every one of them was the dominant cost of this method.
        var nameTokenCache = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        HashSet<string> NameTokensOf(SymbolRecord symbol)
        {
            if (!nameTokenCache.TryGetValue(symbol.Id, out var tokens)) nameTokenCache[symbol.Id] = tokens = Tokenize(symbol.Name);
            return tokens;
        }

        foreach (var ticket in index.Tickets)
        {
            var messageTokens = Tokenize(ticket.Message);
            var overlap = queryTokens.Where(messageTokens.Contains).ToList();
            var exactTicketId = queryTokens.Any(t => t.TrimStart('#') == ticket.Ticket);
            if (overlap.Count == 0 && !exactTicketId) continue;

            var baseScore = TicketWeight * (exactTicketId ? 1.0 : (double)overlap.Count / queryTokens.Count);

            foreach (var file in ticket.Files)
            {
                // SymbolsByFile is a pre-built lookup (ImpactIndex.SymbolsByFile), not a scan of every symbol in
                // the index for every file a ticket touched — on a large codebase that scan, repeated across
                // thousands of tickets, was the single biggest cost of a `where` query.
                foreach (var symbol in index.SymbolsByFile[file])
                {
                    // Tokenize the name rather than lowercasing it: that way an identifier written fullwidth in
                    // the message still matches (NFKC), and a non-Latin identifier is compared bigram-to-bigram
                    // like everything else. Empty means the name was too short to survive tokenizing — no bonus,
                    // otherwise "all tokens present" would be vacuously true for every symbol.
                    var nameTokens = NameTokensOf(symbol);
                    var namedInMessage = nameTokens.Count > 0 && nameTokens.All(messageTokens.Contains);
                    var score = namedInMessage ? baseScore * NamedInTicketBonus : baseScore;
                    var reason = exactTicketId
                        ? $"Exact match on ticket #{ticket.Ticket} ({ticket.Date}): \"{ticket.Message}\""
                        : $"Past ticket #{ticket.Ticket} ({ticket.Date}) shares term(s) [{string.Join(", ", overlap)}]{(namedInMessage ? " and names this symbol directly" : "")}: \"{ticket.Message}\"";
                    add(symbol.Id, score, reason);
                }
            }
        }
    }

    /// <summary>Source 1 (spec section 3): "Tên feature phía FE và route API (orders/cancel gần ngôn ngữ nghiệp vụ hơn tên method)".</summary>
    private static void ScoreRoutesAndFeatures(ImpactIndex index, HashSet<string> queryTokens, Action<string, double, string> add)
    {
        foreach (var ep in index.EntryPointsById.Values)
        {
            if (ep.Route == null) continue;
            var overlap = queryTokens.Where(Tokenize(ep.Route).Contains).ToList();
            if (overlap.Count > 0)
                add(ep.Id, RouteWeight * overlap.Count / queryTokens.Count, $"Route {ep.HttpMethod} {ep.Route} shares term(s) [{string.Join(", ", overlap)}]");
        }

        foreach (var links in index.ApiLinksByBackendId.Values)
        {
            foreach (var link in links)
            {
                if (!index.FrontendCallsById.TryGetValue(link.FrontendId, out var call)) continue;
                var overlap = queryTokens.Where(Tokenize(call.Feature).Contains).ToList();
                if (overlap.Count > 0)
                    add(link.BackendId, RouteWeight * overlap.Count / queryTokens.Count, $"FE feature '{call.Feature}' shares term(s) [{string.Join(", ", overlap)}]");
            }
        }
    }

    /// <summary>Source 3 (spec section 3): "Tên type/method khớp chuỗi con" — weakest signal, mainly useful when the query is already code-like (an English term, a class name).</summary>
    private static void ScoreNameSubstrings(ImpactIndex index, HashSet<string> queryTokens, Action<string, double, string> add)
    {
        foreach (var symbol in index.SymbolsById.Values)
        {
            var displayName = symbol.ContainingType != null ? $"{symbol.ContainingType}.{symbol.Name}" : symbol.Name;
            var overlap = queryTokens.Where(Tokenize(displayName).Contains).ToList();
            if (overlap.Count > 0)
                add(symbol.Id, NameWeight * overlap.Count / queryTokens.Count, $"Symbol name shares term(s) [{string.Join(", ", overlap)}]");
        }
    }

    private static string DisplayNameOf(ImpactIndex index, string id)
    {
        var sym = index.SymbolsById.GetValueOrDefault(id);
        if (sym == null) return id.Length > 2 && id[1] == ':' ? id[2..] : id;
        return sym.ContainingType != null ? $"{sym.ContainingType}.{sym.Name}" : sym.Name;
    }

    internal static HashSet<string> Tokenize(string text)
    {
        // NFKC first: folds halfwidth katakana (ｷｬﾝｾﾙ) onto normal katakana (キャンセル), fullwidth Latin (Ｃａｎｃｅｌ)
        // onto ASCII, and composes Vietnamese diacritics — so the same word typed two different ways, by two
        // different people, over years of commit history, still produces the same tokens.
        var normalized = text.IsNormalized(NormalizationForm.FormKC) ? text : text.Normalize(NormalizationForm.FormKC);

        var tokens = new List<string>();
        foreach (Match m in WordPattern().Matches(normalized))
            AppendRunTokens(m.Value, tokens);
        // A set, not a list: every caller only ever asks "does this token occur" (Contains/All), and on a large
        // codebase this same set is tested against thousands of query tokens per ticket — O(1) membership beats
        // a linear scan repeated that many times over.
        return new HashSet<string>(tokens, StringComparer.Ordinal);
    }

    /// <summary>
    /// One regex match is a run of letters/digits, but that is only a *word* in a script that separates words
    /// with spaces. Japanese and Chinese do not: "注文をキャンセルできない" is a whole clause in a single run, and
    /// treating it as one token means only a byte-identical query could ever match it. So split the run at the
    /// script boundary (which also separates "キャンセルOrderService" into its two halves) and tokenize each part
    /// by its own rules.
    /// </summary>
    private static void AppendRunTokens(string run, List<string> tokens)
    {
        var runes = run.EnumerateRunes().ToList(); // rune, not char: rare kanji live above the BMP as surrogate pairs
        var i = 0;
        while (i < runes.Count)
        {
            var isCjk = IsCjk(runes[i]);
            var start = i;
            while (i < runes.Count && IsCjk(runes[i]) == isCjk) i++;

            if (isCjk) AppendCjkBigrams(runes, start, i, tokens);
            else
            {
                var segment = string.Concat(runes.GetRange(start, i - start)).ToLowerInvariant();
                if (segment.Length >= 2) tokens.Add(segment); // drop "a"/"I"/"1" noise, same as before
            }
        }
    }

    /// <summary>
    /// Overlapping character bigrams — the standard dictionary-free way to make Japanese/Chinese searchable
    /// (the same approach as Lucene's CJKBigramFilter). Staying dictionary-free matters here: spec section 1
    /// requires the tool to run fully offline with no model and no external data, which rules out a real
    /// morphological analyzer (MeCab/Kuromoji and their dictionaries).
    /// "注文をキャンセル" → 注文, 文を, をキ, キャ, ャン, ンセ, セル. A query written with different particles or a
    /// different sentence structure still overlaps on the content bigrams (注文, キャ, ャン, ンセ, セル), which a
    /// whole-run comparison could never do.
    /// </summary>
    private static void AppendCjkBigrams(List<Rune> runes, int start, int end, List<string> tokens)
    {
        if (end - start == 1)
        {
            tokens.Add(runes[start].ToString()); // a lone ideograph is already a whole word — keep it despite length 1
            return;
        }

        // Japanese writes content words in kanji/katakana and grammar in hiragana, so a bigram of two hiragana is
        // nearly always an inflection or particle pair. Those collide across completely unrelated tickets —
        // 「削除できない」 and 「合わない」 share ない and nothing else — which is the same false-confidence problem the
        // Vietnamese function words (bị, khi, được) cause, only far more frequent. Dropping them is the CJK half
        // of a stop-word list, and needs no dictionary: it falls straight out of which script a character is in.
        // Guarded, because an all-hiragana segment has no content half to fall back on — dropping there would
        // leave the segment with no tokens at all.
        var hasContentScript = false;
        for (var i = start; i < end && !hasContentScript; i++) hasContentScript = !IsHiragana(runes[i]);

        for (var i = start; i < end - 1; i++)
        {
            if (hasContentScript && IsHiragana(runes[i]) && IsHiragana(runes[i + 1])) continue;
            tokens.Add(string.Concat(runes[i].ToString(), runes[i + 1].ToString()));
        }
    }

    private static bool IsHiragana(Rune r) => r.Value is >= 0x3041 and <= 0x309F;

    /// <summary>
    /// Scripts written without spaces between words, so a run of them needs bigram splitting. Deliberately
    /// excludes Hangul: Korean *does* separate words with spaces, so a Hangul run is already a word and
    /// bigramming it would only blur exact matches.
    /// </summary>
    private static bool IsCjk(Rune r) => r.Value switch
    {
        >= 0x3005 and <= 0x3006 => true,   // 々 iteration mark, 〆 — categorized as letters, so they reach us
        >= 0x3040 and <= 0x30FF => true,   // hiragana + katakana (incl. the ー long-vowel mark)
        >= 0x31F0 and <= 0x31FF => true,   // katakana phonetic extensions
        >= 0x3400 and <= 0x4DBF => true,   // CJK unified ideographs extension A
        >= 0x4E00 and <= 0x9FFF => true,   // CJK unified ideographs
        >= 0xF900 and <= 0xFAFF => true,   // CJK compatibility ideographs
        >= 0xFF66 and <= 0xFF9D => true,   // halfwidth katakana (only reachable if NFKC was skipped)
        >= 0x20000 and <= 0x2FA1F => true, // extensions B–F + compatibility supplement
        _ => false,
    };

    [GeneratedRegex(@"[\p{L}\p{Nd}]+")]
    private static partial Regex WordPattern();
}
