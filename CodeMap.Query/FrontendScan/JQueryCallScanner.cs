using System.Text.RegularExpressions;

namespace CodeMap.Query.FrontendScan;

/// <summary>
/// Spec section 6, "jQuery (confidence: low)": regex over .js files for `$.ajax({...})`, `$.get(`, `$.post(`.
/// Not a JS parser — a quote-aware brace/paren balancer is as far as this goes, which is enough for the
/// realistic shapes these calls take. Confidence is always "low" for anything found this way, per spec; a call
/// site found but whose URL expression can't even be isolated (no `url:` field, no first argument) is reported
/// separately so it can be logged to diagnostics instead of silently dropped.
/// </summary>
public static partial class JQueryCallScanner
{
    public sealed record RawCall(string File, int Line, string HttpMethod, string RawUrl);
    public sealed record UnparsedCall(string File, int Line, string HttpMethod);
    public sealed record ScanResult(List<RawCall> Calls, List<UnparsedCall> Unparsed);

    public static ScanResult Scan(string root)
    {
        var calls = new List<RawCall>();
        var unparsed = new List<UnparsedCall>();

        foreach (var file in EnumerateJsFiles(root))
        {
            var text = File.ReadAllText(file);
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            ScanFile(text, relative, calls, unparsed);
        }

        return new ScanResult(calls, unparsed);
    }

    private static IEnumerable<string> EnumerateJsFiles(string root)
    {
        if (!Directory.Exists(root)) yield break;
        foreach (var file in Directory.EnumerateFiles(root, "*.js", SearchOption.AllDirectories))
        {
            if (file.Replace('\\', '/').Contains("/node_modules/", StringComparison.Ordinal)) continue;
            var name = Path.GetFileName(file);
            if (name.EndsWith(".min.js", StringComparison.Ordinal) || name.EndsWith(".spec.js", StringComparison.Ordinal) || name.EndsWith(".test.js", StringComparison.Ordinal)) continue;
            yield return file;
        }
    }

    private static void ScanFile(string text, string relativeFile, List<RawCall> calls, List<UnparsedCall> unparsed)
    {
        foreach (Match m in AjaxCallPattern().Matches(text))
        {
            var parenStart = m.Index + m.Length - 1; // index of the call's '('
            var line = LineOf(text, m.Index);
            var body = ExtractBalanced(text, parenStart, '(', ')');
            if (body == null) { unparsed.Add(new UnparsedCall(relativeFile, line, "GET")); continue; }

            var httpMethod = ExtractAjaxMethod(body) ?? "GET";
            var rawUrl = ExtractAjaxUrl(body);
            if (rawUrl == null) { unparsed.Add(new UnparsedCall(relativeFile, line, httpMethod)); continue; }

            calls.Add(new RawCall(relativeFile, line, httpMethod, rawUrl));
        }

        foreach (Match m in GetPostCallPattern().Matches(text))
        {
            var httpMethod = m.Groups[1].Value.ToUpperInvariant();
            var parenStart = m.Index + m.Length - 1;
            var line = LineOf(text, m.Index);
            var body = ExtractBalanced(text, parenStart, '(', ')');
            if (body == null) { unparsed.Add(new UnparsedCall(relativeFile, line, httpMethod)); continue; }

            var rawUrl = ExtractValueExpression(body, 0);
            if (string.IsNullOrWhiteSpace(rawUrl)) { unparsed.Add(new UnparsedCall(relativeFile, line, httpMethod)); continue; }

            calls.Add(new RawCall(relativeFile, line, httpMethod, rawUrl));
        }
    }

    private static string? ExtractAjaxMethod(string body)
    {
        var m = AjaxMethodFieldPattern().Match(body);
        return m.Success ? m.Groups[1].Value.ToUpperInvariant() : null;
    }

    private static string? ExtractAjaxUrl(string body)
    {
        var m = AjaxUrlFieldPattern().Match(body);
        if (!m.Success) return null;
        var value = ExtractValueExpression(body, m.Index + m.Length);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>Extracts the substring inside a balanced (respecting quotes) bracket pair starting at <paramref name="openIndex"/>.</summary>
    private static string? ExtractBalanced(string text, int openIndex, char open, char close)
    {
        var depth = 0;
        char? quote = null;
        for (var i = openIndex; i < text.Length; i++)
        {
            var c = text[i];
            if (quote != null)
            {
                if (c == quote && text[i - 1] != '\\') quote = null;
                continue;
            }

            if (c is '\'' or '"' or '`') { quote = c; continue; }
            if (c == open) depth++;
            else if (c == close)
            {
                depth--;
                if (depth == 0) return text.Substring(openIndex + 1, i - openIndex - 1);
            }
        }

        return null;
    }

    /// <summary>Reads a value expression from <paramref name="startIndex"/> up to the next top-level comma or the enclosing bracket's end (quote- and nesting-aware).</summary>
    private static string ExtractValueExpression(string text, int startIndex)
    {
        var depth = 0;
        char? quote = null;
        var i = startIndex;
        for (; i < text.Length; i++)
        {
            var c = text[i];
            if (quote != null)
            {
                if (c == quote && text[i - 1] != '\\') quote = null;
                continue;
            }

            if (c is '\'' or '"' or '`') { quote = c; continue; }
            if (c is '(' or '[' or '{') { depth++; continue; }
            if (c is ')' or ']' or '}')
            {
                if (depth == 0) break;
                depth--;
                continue;
            }

            if (c == ',' && depth == 0) break;
        }

        return text[startIndex..i].Trim();
    }

    private static int LineOf(string text, int index) => text.AsSpan(0, index).Count('\n') + 1;

    [GeneratedRegex(@"\$\.ajax\s*\(")]
    private static partial Regex AjaxCallPattern();

    [GeneratedRegex(@"\$\.(get|post)\s*\(")]
    private static partial Regex GetPostCallPattern();

    [GeneratedRegex(@"(?:type|method)\s*:\s*['""](\w+)['""]")]
    private static partial Regex AjaxMethodFieldPattern();

    [GeneratedRegex(@"url\s*:\s*")]
    private static partial Regex AjaxUrlFieldPattern();
}
