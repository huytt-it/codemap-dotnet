using CodeMap.Query.FrontendScan;

namespace CodeMap.Tests;

/// <summary>Spec section 6, "jQuery (confidence: low)".</summary>
[TestClass]
public class JQueryCallScannerTests
{
    [TestMethod]
    public void Ajax_call_with_literal_url_and_explicit_method_is_extracted()
    {
        var dir = TestPaths.NewTempDir();
        File.WriteAllText(Path.Combine(dir, "orders.js"),
            "function cancel(id) {\n  $.ajax({ url: '/api/orders/' + id, type: 'DELETE' });\n}\n");

        var result = JQueryCallScanner.Scan(dir);

        Assert.AreEqual(1, result.Calls.Count);
        Assert.AreEqual("DELETE", result.Calls[0].HttpMethod);
        Assert.AreEqual("'/api/orders/' + id", result.Calls[0].RawUrl);
        Assert.AreEqual(2, result.Calls[0].Line);
    }

    [TestMethod]
    public void Ajax_call_without_an_explicit_method_defaults_to_GET()
    {
        var dir = TestPaths.NewTempDir();
        File.WriteAllText(Path.Combine(dir, "orders.js"), "$.ajax({ url: '/api/orders' });\n");

        var result = JQueryCallScanner.Scan(dir);

        Assert.AreEqual("GET", result.Calls.Single().HttpMethod);
    }

    [TestMethod]
    public void Get_shorthand_call_is_extracted()
    {
        var dir = TestPaths.NewTempDir();
        File.WriteAllText(Path.Combine(dir, "orders.js"), "$.get('/api/orders', function(data) { render(data); });\n");

        var result = JQueryCallScanner.Scan(dir);

        Assert.AreEqual("GET", result.Calls.Single().HttpMethod);
        Assert.AreEqual("'/api/orders'", result.Calls.Single().RawUrl);
    }

    [TestMethod]
    public void Post_shorthand_call_is_extracted()
    {
        var dir = TestPaths.NewTempDir();
        File.WriteAllText(Path.Combine(dir, "orders.js"), "$.post('/api/orders', { name: 'x' });\n");

        var result = JQueryCallScanner.Scan(dir);

        Assert.AreEqual("POST", result.Calls.Single().HttpMethod);
    }

    [TestMethod] // regression fixture: a function-call URL has no url: field text worth keeping as a "raw url" — still surfaced, just as unparsed
    public void Ajax_call_whose_url_is_a_function_call_is_still_extracted_as_a_raw_expression()
    {
        var dir = TestPaths.NewTempDir();
        File.WriteAllText(Path.Combine(dir, "legacy.js"),
            "function cancel(id) {\n  var endpoint = buildOrderEndpoint(id);\n  $.ajax({ url: endpoint, type: 'DELETE' });\n}\n");

        var result = JQueryCallScanner.Scan(dir);

        Assert.AreEqual(1, result.Calls.Count);
        Assert.AreEqual("endpoint", result.Calls[0].RawUrl); // resolvable as a raw expression; FrontendUrlNormalizer is what ultimately rejects it (no '/')
    }

    [TestMethod]
    public void Ajax_call_with_no_url_field_at_all_is_reported_as_unparsed()
    {
        var dir = TestPaths.NewTempDir();
        File.WriteAllText(Path.Combine(dir, "orders.js"), "$.ajax({ type: 'DELETE', data: {} });\n");

        var result = JQueryCallScanner.Scan(dir);

        Assert.AreEqual(0, result.Calls.Count);
        Assert.AreEqual(1, result.Unparsed.Count);
        Assert.AreEqual("DELETE", result.Unparsed[0].HttpMethod);
    }

    [TestMethod] // jQuery's own signature: $.ajax(url [, settings]) — the URL is the first argument, there is no `url:` field
    public void Ajax_call_with_the_url_as_first_argument_and_a_settings_object_is_extracted()
    {
        var dir = TestPaths.NewTempDir();
        File.WriteAllText(Path.Combine(dir, "orders.js"),
            "function cancel(id) {\n  $.ajax('/api/orders/' + id, { type: 'DELETE', success: done });\n}\n");

        var result = JQueryCallScanner.Scan(dir);

        Assert.AreEqual(0, result.Unparsed.Count);
        var call = result.Calls.Single();
        Assert.AreEqual("DELETE", call.HttpMethod);
        Assert.AreEqual("'/api/orders/' + id", call.RawUrl);
        Assert.AreEqual(2, call.Line);
    }

    [TestMethod] // the first argument is a URL (not the settings) as soon as a second argument follows it, even when it starts with an identifier
    public void Ajax_call_with_a_base_url_variable_as_first_argument_is_extracted()
    {
        var dir = TestPaths.NewTempDir();
        File.WriteAllText(Path.Combine(dir, "orders.js"), "$.ajax(API_BASE + '/orders', { method: 'POST', data: payload });\n");

        var result = JQueryCallScanner.Scan(dir);

        var call = result.Calls.Single();
        Assert.AreEqual("POST", call.HttpMethod);
        Assert.AreEqual("API_BASE + '/orders'", call.RawUrl);
    }

    [TestMethod]
    public void Ajax_call_with_only_the_url_string_defaults_to_GET()
    {
        var dir = TestPaths.NewTempDir();
        File.WriteAllText(Path.Combine(dir, "orders.js"), "$.ajax('/api/orders');\n");

        var call = JQueryCallScanner.Scan(dir).Calls.Single();

        Assert.AreEqual("GET", call.HttpMethod);
        Assert.AreEqual("'/api/orders'", call.RawUrl);
    }

    [TestMethod] // $.ajax(options): the argument is a settings variable, not a URL — nothing to resolve, so it must stay unparsed rather than become a bogus URL
    public void Ajax_call_passing_a_settings_variable_is_reported_as_unparsed()
    {
        var dir = TestPaths.NewTempDir();
        File.WriteAllText(Path.Combine(dir, "orders.js"), "$.ajax(options);\n");

        var result = JQueryCallScanner.Scan(dir);

        Assert.AreEqual(0, result.Calls.Count);
        Assert.AreEqual(1, result.Unparsed.Count);
    }

    [TestMethod]
    public void Calls_through_the_jQuery_global_are_extracted_like_the_dollar_alias()
    {
        var dir = TestPaths.NewTempDir();
        File.WriteAllText(Path.Combine(dir, "orders.js"),
            "jQuery.ajax({ url: '/api/a', type: 'PUT' });\njQuery.get('/api/b');\njQuery.post('/api/c', {});\nwindow.jQuery.get('/api/d');\n");

        var result = JQueryCallScanner.Scan(dir);

        CollectionAssert.AreEqual(
            new[] { "PUT /api/a", "GET /api/b", "POST /api/c", "GET /api/d" },
            result.Calls.OrderBy(c => c.Line).Select(c => $"{c.HttpMethod} {c.RawUrl.Trim('\'')}").ToList());
    }

    [TestMethod]
    public void GetJSON_shorthand_is_a_GET()
    {
        var dir = TestPaths.NewTempDir();
        File.WriteAllText(Path.Combine(dir, "orders.js"), "$.getJSON('/api/orders', function(data) { render(data); });\njQuery.getJSON('/api/customers');\n");

        var result = JQueryCallScanner.Scan(dir);

        Assert.AreEqual(2, result.Calls.Count);
        Assert.IsTrue(result.Calls.All(c => c.HttpMethod == "GET"));
    }

    [TestMethod] // `$.get`/`jQuery.get` must start a token: another identifier ending in "jQuery" or "$" is not the library
    public void Identifiers_that_merely_end_in_jQuery_are_not_calls()
    {
        var dir = TestPaths.NewTempDir();
        File.WriteAllText(Path.Combine(dir, "orders.js"), "myjQuery.get('/api/a');\nfoo$.post('/api/b');\nmy$.ajax({ url: '/api/c' });\n");

        var result = JQueryCallScanner.Scan(dir);

        Assert.AreEqual(0, result.Calls.Count);
        Assert.AreEqual(0, result.Unparsed.Count);
    }

    [TestMethod] // vendored libraries and build output are not application code
    public void Vendored_and_build_output_directories_are_skipped()
    {
        var dir = TestPaths.NewTempDir();
        foreach (var skipped in new[] { "wwwroot/lib/jquery", "src/Web/wwwroot/lib/plugin", "dist/app", "bin/Debug", "obj/Release", "node_modules/x" })
        {
            var skippedDir = Path.Combine(dir, skipped);
            Directory.CreateDirectory(skippedDir);
            File.WriteAllText(Path.Combine(skippedDir, "vendor.js"), "$.get('/should/not/be/found');\n");
        }

        foreach (var kept in new[] { "wwwroot/js", "src/lib", "wwwroot/libs" })
        {
            var keptDir = Path.Combine(dir, kept);
            Directory.CreateDirectory(keptDir);
            File.WriteAllText(Path.Combine(keptDir, "app.js"), $"$.get('/api/{kept.Replace('/', '-')}');\n");
        }

        var result = JQueryCallScanner.Scan(dir);

        CollectionAssert.AreEquivalent(
            new[] { "wwwroot/js/app.js", "src/lib/app.js", "wwwroot/libs/app.js" },
            result.Calls.Select(c => c.File).ToList());
    }

    [TestMethod] // only the part of the path below the scan root counts: a checkout living under a folder named dist/bin must still be scanned
    public void Skip_rules_apply_below_the_scan_root_not_to_the_root_itself()
    {
        var root = Path.Combine(TestPaths.NewTempDir(), "dist", "bin", "node_modules", "frontend");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "orders.js"), "$.get('/api/orders');\n");

        var result = JQueryCallScanner.Scan(root);

        Assert.AreEqual(1, result.Calls.Count);
    }

    [TestMethod] // MVC views: the URL literal inside <script> is a call; markup and Razor text outside it is not
    public void Cshtml_script_blocks_are_scanned_with_line_numbers_of_the_view_file()
    {
        var dir = TestPaths.NewTempDir();
        Directory.CreateDirectory(Path.Combine(dir, "Views", "Orders"));
        File.WriteAllText(Path.Combine(dir, "Views", "Orders", "Index.cshtml"),
            "@model OrdersViewModel\n<p>$.get('/api/outside-script')</p>\n@section Scripts {\n<script>\n  $(function () {\n    $.get('/api/orders', render);\n  });\n</script>\n}\n");

        var result = JQueryCallScanner.Scan(dir);

        var call = result.Calls.Single();
        Assert.AreEqual("Views/Orders/Index.cshtml", call.File);
        Assert.AreEqual("'/api/orders'", call.RawUrl);
        Assert.AreEqual(6, call.Line);
    }

    [TestMethod]
    public void Cshtml_with_several_script_blocks_and_ajax_settings_is_scanned_in_each()
    {
        var dir = TestPaths.NewTempDir();
        File.WriteAllText(Path.Combine(dir, "Page.cshtml"),
            "<script src=\"~/js/site.js\"></script>\n<script type=\"text/javascript\">\n$.ajax({ url: '/api/a', type: 'POST' });\n</script>\n<div></div>\n<SCRIPT>\njQuery.post('/api/b', {});\n</SCRIPT>\n");

        var result = JQueryCallScanner.Scan(dir);

        CollectionAssert.AreEqual(new[] { "POST /api/a@3", "POST /api/b@7" }, result.Calls.OrderBy(c => c.Line).Select(c => $"{c.HttpMethod} {c.RawUrl.Trim('\'')}@{c.Line}").ToList());
    }

    [TestMethod] // @Url.Action resolves through the backend's conventional routing, which scan-fe does not follow: report, never guess
    public void Cshtml_url_built_by_Razor_is_reported_as_unparsed_with_its_reason()
    {
        var dir = TestPaths.NewTempDir();
        File.WriteAllText(Path.Combine(dir, "Page.cshtml"),
            "<script>\n$.post('@Url.Action(\"Create\", \"Orders\")', data);\n$.get('/api/orders/@Model.Id');\n</script>\n");

        var result = JQueryCallScanner.Scan(dir);

        Assert.AreEqual(0, result.Calls.Count);
        Assert.AreEqual(2, result.Unparsed.Count);
        var action = result.Unparsed.Single(u => u.Line == 2);
        Assert.AreEqual("POST", action.HttpMethod);
        StringAssert.Contains(action.RawUrl!, "@Url.Action");
        StringAssert.Contains(action.Reason!, "Url.Action");
        StringAssert.Contains(result.Unparsed.Single(u => u.Line == 3).Reason!, "Razor");
    }

    [TestMethod]
    public void Cshtml_under_build_output_is_skipped()
    {
        var dir = TestPaths.NewTempDir();
        Directory.CreateDirectory(Path.Combine(dir, "obj"));
        File.WriteAllText(Path.Combine(dir, "obj", "Generated.cshtml"), "<script>$.get('/should/not/be/found');</script>\n");

        Assert.AreEqual(0, JQueryCallScanner.Scan(dir).Calls.Count);
    }

    [TestMethod]
    public void Node_modules_and_minified_files_are_skipped()
    {
        var dir = TestPaths.NewTempDir();
        var nodeModules = Path.Combine(dir, "node_modules", "some-lib");
        Directory.CreateDirectory(nodeModules);
        File.WriteAllText(Path.Combine(nodeModules, "index.js"), "$.get('/should/not/be/found');\n");
        File.WriteAllText(Path.Combine(dir, "bundle.min.js"), "$.get('/also/not/found');\n");
        File.WriteAllText(Path.Combine(dir, "real.js"), "$.get('/api/orders');\n");

        var result = JQueryCallScanner.Scan(dir);

        Assert.AreEqual(1, result.Calls.Count);
        Assert.AreEqual("'/api/orders'", result.Calls[0].RawUrl);
    }
}
