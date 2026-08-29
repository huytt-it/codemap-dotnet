using System.Text;
using System.Text.RegularExpressions;

namespace CodeMap.Query.FrontendScan;

/// <summary>
/// Spec section 6, "Normalize URL thành route pattern": turns the raw JS/TS source text of a URL expression
/// (a string literal, a template literal with `${...}` holes, or `'...' + expr + '...'` concatenation) into a
/// route pattern comparable to a normalized backend route. Returns null when the expression has no recognizable
/// path structure at all (e.g. a bare variable) — the caller logs that to diagnostics.json instead of guessing.
/// </summary>
public static partial class FrontendUrlNormalizer
{
    public static string? Normalize(string rawExpression)
    {
        var segments = Tokenize(rawExpression);
        if (segments.Count == 0) return null;

        // Spec: "Bỏ base URL và biến environment (${environment.apiUrl}, API_BASE) khỏi đầu chuỗi" — a leading
        // non-literal segment is dropped entirely, not turned into {*}, since it's not part of the route path.
        if (!segments[0].IsLiteral)
            segments.RemoveAt(0);
        if (segments.Count == 0) return null;

        var combined = string.Concat(segments.Select(s => s.IsLiteral ? s.Text : "{*}"));
        if (!combined.Contains('/')) return null; // no discernible path structure — genuinely unparseable

        var normalized = RouteNormalizer.NormalizeCommon(combined);
        return normalized.Length == 0 ? null : normalized;
    }

    private readonly record struct Segment(bool IsLiteral, string Text);

    /// <summary>
    /// Small hand-rolled tokenizer for JS/TS URL expressions — not a general JS parser, just enough to split a
    /// concatenation/template chain into literal path fragments vs. interpolated/variable holes. `+` is treated
    /// as a concatenation operator (safe here: it never appears un-quoted inside a URL literal), and both
    /// `${...}` (template literal) and `{{...}}` (spec's third interpolation form) holes count as non-literal.
    /// </summary>
    private static List<Segment> Tokenize(string expr)
    {
        var segments = new List<Segment>();
        var i = 0;
        while (i < expr.Length)
        {
            var c = expr[i];
            if (char.IsWhiteSpace(c) || c == '+') { i++; continue; }

            if (c == '`')
            {
                i++;
                var sb = new StringBuilder();
                while (i < expr.Length && expr[i] != '`')
                {
                    if (expr[i] == '$' && i + 1 < expr.Length && expr[i + 1] == '{')
                    {
                        if (sb.Length > 0) { AddLiteral(segments, sb.ToString()); sb.Clear(); }
                        i = SkipBalanced(expr, i + 2, '{', '}');
                        segments.Add(new Segment(false, ""));
                        continue;
                    }

                    sb.Append(expr[i]);
                    i++;
                }

                if (sb.Length > 0) AddLiteral(segments, sb.ToString());
                i++; // consume closing `
            }
            else if (c is '\'' or '"')
            {
                var quote = c;
                i++;
                var sb = new StringBuilder();
                while (i < expr.Length && expr[i] != quote) { sb.Append(expr[i]); i++; }
                i++; // consume closing quote
                AddLiteral(segments, sb.ToString());
            }
            else if (c == '{' && i + 1 < expr.Length && expr[i + 1] == '{')
            {
                i = SkipBalanced(expr, i + 2, '{', '}', doubleClose: true);
                segments.Add(new Segment(false, ""));
            }
            else
            {
                // A bare identifier / member-expression run (environment.apiUrl, id, API_BASE).
                var start = i;
                while (i < expr.Length && expr[i] is not ('\'' or '"' or '`' or '+')) i++;
                if (expr[start..i].Trim().Length > 0)
                    segments.Add(new Segment(false, ""));
            }
        }

        return segments;
    }

    /// <summary>Advances past a balanced-brace hole starting right after the opening delimiter; <paramref name="doubleClose"/> expects `}}` instead of a single `}`.</summary>
    private static int SkipBalanced(string expr, int start, char open, char close, bool doubleClose = false)
    {
        var depth = 1;
        var i = start;
        while (i < expr.Length && depth > 0)
        {
            if (expr[i] == open) depth++;
            else if (expr[i] == close) depth--;
            i++;
        }

        if (doubleClose && i < expr.Length && expr[i] == close) i++;
        return i;
    }

    /// <summary>
    /// A quoted string literal can still contain a `{{id}}`-style placeholder as plain text (spec's third
    /// interpolation form — a templating convention, not JS/TS syntax the tokenizer would otherwise see), so
    /// literal text always gets one more pass splitting on it before being added as segments.
    /// </summary>
    private static void AddLiteral(List<Segment> segments, string text)
    {
        var lastEnd = 0;
        foreach (Match m in DoubleCurlyPattern().Matches(text))
        {
            if (m.Index > lastEnd) segments.Add(new Segment(true, text[lastEnd..m.Index]));
            segments.Add(new Segment(false, ""));
            lastEnd = m.Index + m.Length;
        }

        if (lastEnd < text.Length) segments.Add(new Segment(true, text[lastEnd..]));
    }

    [GeneratedRegex(@"\{\{[^}]*\}\}")]
    private static partial Regex DoubleCurlyPattern();
}
