using System.Text.RegularExpressions;

namespace CodeMap.Query.FrontendScan;

/// <summary>
/// Spec section 6, "jQuery (confidence: low)": regex over .js files — and the &lt;script&gt; blocks of .cshtml
/// views — for `$.ajax(...)` (settings object, or `url, settings`), `$.get(`, `$.post(`, `$.getJSON(`, each also
/// through the `jQuery` global.
/// Not a JS parser — a quote-aware brace/paren balancer is as far as this goes, which is enough for the
/// realistic shapes these calls take. Confidence is always "low" for anything found this way, per spec; a call
/// site found but whose URL expression can't even be isolated (no `url:` field, no first argument) is reported
/// separately so it can be logged to diagnostics instead of silently dropped. In a view, a URL built by Razor
/// (`@Url.Action(...)`, `/api/orders/@Model.Id`) is reported the same way and never resolved: where it points
/// depends on the backend's conventional routing, which this scan does not model.
/// </summary>
public static partial class JQueryCallScanner
{
    public sealed record RawCall(string File, int Line, string HttpMethod, string RawUrl);
    /// <summary>RawUrl/Reason are set when the scanner knows more than "no URL found" — e.g. a Razor-built URL it deliberately does not resolve.</summary>
    public sealed record UnparsedCall(string File, int Line, string HttpMethod, string? RawUrl = null, string? Reason = null);
    public sealed record ScanResult(List<RawCall> Calls, List<UnparsedCall> Unparsed);

    /// <summary>Third-party and build-output folders: nothing in them is application code, and node_modules alone can hold tens of thousands of files.</summary>
    private static readonly string[] SkippedDirectoryNames = { "node_modules", "dist", "bin", "obj" };

    public static ScanResult Scan(string root)
    {
        var calls = new List<RawCall>();
        var unparsed = new List<UnparsedCall>();

        foreach (var file in EnumerateSourceFiles(root))
        {
            var text = File.ReadAllText(file);
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var isView = file.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase);
            ScanFile(isView ? KeepOnlyScriptBlocks(text) : text, relative, isView, calls, unparsed);
        }

        return new ScanResult(calls, unparsed);
    }

    /// <summary>Walks directory by directory so skipped folders are never entered. Only names below <paramref name="root"/> are judged — a checkout that itself lives under a folder called "dist" or "bin" is still scanned.</summary>
    private static IEnumerable<string> EnumerateSourceFiles(string root)
    {
        if (!Directory.Exists(root)) yield break;

        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                if (!IsSkippedDirectory(sub)) pending.Push(sub);
            }

            foreach (var file in Directory.EnumerateFiles(dir))
            {
                if (IsScannedFile(Path.GetFileName(file))) yield return file;
            }
        }
    }

    private static bool IsSkippedDirectory(string path)
    {
        var name = Path.GetFileName(path);
        if (SkippedDirectoryNames.Contains(name, StringComparer.OrdinalIgnoreCase)) return true;
        // ASP.NET Core's client-side package folder (LibMan/bower output): vendored libraries, not the app's own scripts.
        return name.Equals("lib", StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), "wwwroot", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsScannedFile(string name)
    {
        if (name.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase)) return true;
        if (!name.EndsWith(".js", StringComparison.OrdinalIgnoreCase)) return false;
        return !(name.EndsWith(".min.js", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".spec.js", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".test.js", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Blanks everything outside &lt;script&gt;...&lt;/script&gt; (newlines kept, so line numbers still point into the view): markup and Razor text are not JavaScript.</summary>
    private static string KeepOnlyScriptBlocks(string text)
    {
        var masked = new char[text.Length];
        for (var i = 0; i < text.Length; i++)
            masked[i] = text[i] is '\n' or '\r' ? text[i] : ' ';

        foreach (Match block in ScriptBlockPattern().Matches(text))
        {
            var body = block.Groups["body"];
            text.CopyTo(body.Index, masked, body.Index, body.Length);
        }

        return new string(masked);
    }

    private static void ScanFile(string text, string relativeFile, bool isView, List<RawCall> calls, List<UnparsedCall> unparsed)
    {
        foreach (Match m in AjaxCallPattern().Matches(text))
        {
            var parenStart = m.Index + m.Length - 1; // index of the call's '('
            var line = LineOf(text, m.Index);
            var body = ExtractBalanced(text, parenStart, '(', ')');
            if (body == null) { unparsed.Add(new UnparsedCall(relativeFile, line, "GET")); continue; }

            var (rawUrl, settings) = ReadAjaxArguments(body);
            var httpMethod = ExtractAjaxMethod(settings) ?? "GET";
            if (rawUrl == null) { unparsed.Add(new UnparsedCall(relativeFile, line, httpMethod)); continue; }

            AddCall(relativeFile, line, httpMethod, rawUrl, isView, calls, unparsed);
        }

        foreach (Match m in ShorthandCallPattern().Matches(text))
        {
            var httpMethod = m.Groups[1].Value.Equals("post", StringComparison.Ordinal) ? "POST" : "GET"; // get | getJSON
            var parenStart = m.Index + m.Length - 1;
            var line = LineOf(text, m.Index);
            var body = ExtractBalanced(text, parenStart, '(', ')');
            if (body == null) { unparsed.Add(new UnparsedCall(relativeFile, line, httpMethod)); continue; }

            var rawUrl = ExtractValueExpression(body, 0);
            if (string.IsNullOrWhiteSpace(rawUrl)) { unparsed.Add(new UnparsedCall(relativeFile, line, httpMethod)); continue; }

            AddCall(relativeFile, line, httpMethod, rawUrl, isView, calls, unparsed);
        }
    }

    private static void AddCall(string relativeFile, int line, string httpMethod, string rawUrl, bool isView, List<RawCall> calls, List<UnparsedCall> unparsed)
    {
        if (isView && rawUrl.Contains('@'))
        {
            var reason = ConventionalRoutingHelperPattern().IsMatch(rawUrl)
                ? "Razor @Url.Action/@Url.RouteUrl resolves through the backend's conventional routing, which scan-fe does not follow."
                : "URL contains a Razor expression (@...) that cannot be resolved statically.";
            unparsed.Add(new UnparsedCall(relativeFile, line, httpMethod, rawUrl, reason));
            return;
        }

        calls.Add(new RawCall(relativeFile, line, httpMethod, rawUrl));
    }

    /// <summary>
    /// jQuery's two call shapes: `$.ajax({ url: ..., type: ... })` and `$.ajax(url [, settings])`. An object
    /// literal first is the settings; otherwise the first argument is the URL once a second argument follows it
    /// (even if it starts with an identifier: `API_BASE + '/orders', {...}`). A lone non-object argument is a URL only
    /// if it has a string literal in it — a lone `options` is a settings variable, which names no URL.
    /// Returns the settings text to read the HTTP method from.
    /// </summary>
    private static (string? RawUrl, string Settings) ReadAjaxArguments(string body)
    {
        var firstEnd = FindValueEnd(body, 0);
        var first = body[..firstEnd].Trim();
        if (first.StartsWith('{'))
            return (ExtractAjaxUrl(body), body);

        if (firstEnd < body.Length) // a comma follows: `url, settings`
            return (first.Length > 0 ? first : null, body[(firstEnd + 1)..]);

        return (first.IndexOfAny(new[] { '\'', '"', '`' }) >= 0 ? first : null, "");
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
        => text[startIndex..FindValueEnd(text, startIndex)].Trim();

    /// <summary>Index of the top-level comma or unmatched closing bracket that ends the value expression starting at <paramref name="startIndex"/>, else the end of the text.</summary>
    private static int FindValueEnd(string text, int startIndex)
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

        return i;
    }

    private static int LineOf(string text, int index) => text.AsSpan(0, index).Count('\n') + 1;

    // `$` / `jQuery` must start a token (an optional `window.` etc. before it is fine): `myjQuery.get(` and `foo$.post(` are other objects.
    [GeneratedRegex(@"(?<![\w$])(?:\$|jQuery)\.ajax\s*\(")]
    private static partial Regex AjaxCallPattern();

    [GeneratedRegex(@"(?<![\w$])(?:\$|jQuery)\.(get|post|getJSON)\s*\(")]
    private static partial Regex ShorthandCallPattern();

    [GeneratedRegex(@"(?:type|method)\s*:\s*['""](\w+)['""]")]
    private static partial Regex AjaxMethodFieldPattern();

    [GeneratedRegex(@"url\s*:\s*")]
    private static partial Regex AjaxUrlFieldPattern();

    [GeneratedRegex(@"<script\b[^>]*>(?<body>.*?)</script\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptBlockPattern();

    [GeneratedRegex(@"@Url\.(?:Action|RouteUrl)\b")]
    private static partial Regex ConventionalRoutingHelperPattern();
}
