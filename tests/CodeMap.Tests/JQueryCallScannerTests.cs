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
