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

        var scores = new Dictionary<string, (double Score, List<string> Reasons)>(StringComparer.Ordinal);
        void Add(string symbolId, double score, string reason)
        {
            if (score <= 0 || !index.SymbolsById.ContainsKey(symbolId)) return;
            if (!scores.TryGetValue(symbolId, out var entry)) entry = (0, new List<string>());
            entry.Score += score;
            entry.Reasons.Add(reason);
            scores[symbolId] = entry;
        }

        ScoreTickets(index, queryTokens, Add);
        ScoreRoutesAndFeatures(index, queryTokens, Add);
        ScoreNameSubstrings(index, queryTokens, Add);

        return scores
            .Select(kv => new WhereCandidate(kv.Key, DisplayNameOf(index, kv.Key), Math.Round(kv.Value.Score, 2), kv.Value.Reasons))
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.SymbolId, StringComparer.Ordinal)
            .Take(MaxResults)
            .ToList();
    }

    /// <summary>Source 2 (spec section 3): "ticket-files.jsonl — ticket cũ có mô tả tương tự đã sửa file nào". The only signal that can realistically bridge a Vietnamese query to English code — a human already wrote that bridge once, in the commit message.</summary>
    private static void ScoreTickets(ImpactIndex index, List<string> queryTokens, Action<string, double, string> add)
    {
        foreach (var ticket in index.Tickets)
        {
            var messageTokens = Tokenize(ticket.Message);
            var overlap = queryTokens.Where(messageTokens.Contains).ToList();
            var exactTicketId = queryTokens.Any(t => t.TrimStart('#') == ticket.Ticket);
            if (overlap.Count == 0 && !exactTicketId) continue;

            var baseScore = TicketWeight * (exactTicketId ? 1.0 : (double)overlap.Count / queryTokens.Count);

            foreach (var file in ticket.Files)
            {
                foreach (var symbol in index.SymbolsById.Values.Where(s => s.File == file))
                {
                    var namedInMessage = messageTokens.Contains(symbol.Name.ToLowerInvariant());
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
    private static void ScoreRoutesAndFeatures(ImpactIndex index, List<string> queryTokens, Action<string, double, string> add)
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
    private static void ScoreNameSubstrings(ImpactIndex index, List<string> queryTokens, Action<string, double, string> add)
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

    internal static List<string> Tokenize(string text) =>
        WordPattern().Matches(text).Select(m => m.Value.ToLowerInvariant()).Where(t => t.Length >= 2).Distinct().ToList();

    [GeneratedRegex(@"[\p{L}\p{Nd}]+")]
    private static partial Regex WordPattern();
}
