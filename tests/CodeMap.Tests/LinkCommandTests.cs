using CodeMap.Query.Json;
using CodeMap.Query.Link;
using CodeMap.Query.Models;

namespace CodeMap.Tests;

/// <summary>Spec section 6, `link`: matches frontend-calls.jsonl against entrypoints.json by (httpMethod, normalized route).</summary>
[TestClass]
public class LinkCommandTests
{
    [TestMethod]
    public void Exact_route_match_produces_an_exact_api_link()
    {
        var indexDir = SetUpIndex(
            entryPoints: new() { new EntryPoint("M:Api.OrdersController.Delete(System.Int32)", "http", "DELETE", "api/orders/{id}") },
            frontendCalls: new() { new FrontendCall("fe:a.ts:1", "a.ts", 1, "DELETE", "`/api/orders/${id}`", "api/orders/{*}", "orders", "high", new()) });

        LinkCommand.Run(new[] { "--index", indexDir });

        var links = JsonlReader.Read<ApiLink>(Path.Combine(indexDir, "api-links.jsonl"));
        Assert.AreEqual(1, links.Count);
        Assert.AreEqual("exact", links[0].MatchKind);
        Assert.AreEqual("M:Api.OrdersController.Delete(System.Int32)", links[0].BackendId);
    }

    [TestMethod]
    public void Two_backend_endpoints_normalizing_to_the_same_route_produce_an_ambiguous_link_for_each()
    {
        var indexDir = SetUpIndex(
            entryPoints: new()
            {
                new EntryPoint("M:Api.A.Get(System.Int32)", "http", "GET", "api/orders/{id}"),
                new EntryPoint("M:Api.B.Get(System.Guid)", "http", "GET", "api/orders/{guid}"),
            },
            frontendCalls: new() { new FrontendCall("fe:a.ts:1", "a.ts", 1, "GET", "`/api/orders/${id}`", "api/orders/{*}", "orders", "high", new()) });

        LinkCommand.Run(new[] { "--index", indexDir });

        var links = JsonlReader.Read<ApiLink>(Path.Combine(indexDir, "api-links.jsonl"));
        Assert.AreEqual(2, links.Count);
        Assert.IsTrue(links.All(l => l.MatchKind == "ambiguous"));
    }

    [TestMethod]
    public void Frontend_call_with_no_backend_match_is_recorded_in_diagnostics()
    {
        var indexDir = SetUpIndex(
            entryPoints: new() { new EntryPoint("M:Api.OrdersController.Delete(System.Int32)", "http", "DELETE", "api/orders/{id}") },
            frontendCalls: new() { new FrontendCall("fe:a.ts:1", "a.ts", 1, "GET", "'/api/orders/summary'", "api/orders/summary", "orders", "high", new()) });

        LinkCommand.Run(new[] { "--index", indexDir });

        var diagnostics = JsonUtil.ReadFile<DiagnosticsModel>(Path.Combine(indexDir, "diagnostics.json"))!;
        Assert.AreEqual(1, diagnostics.UnmatchedFrontendCalls.Count);
        Assert.AreEqual("fe:a.ts:1", diagnostics.UnmatchedFrontendCalls[0].FrontendId);
    }

    [TestMethod]
    public void Backend_endpoint_with_no_frontend_caller_is_recorded_as_unreferenced()
    {
        var indexDir = SetUpIndex(
            entryPoints: new() { new EntryPoint("M:Api.OrdersController.Delete(System.Int32)", "http", "DELETE", "api/orders/{id}") },
            frontendCalls: new());

        LinkCommand.Run(new[] { "--index", indexDir });

        var diagnostics = JsonUtil.ReadFile<DiagnosticsModel>(Path.Combine(indexDir, "diagnostics.json"))!;
        CollectionAssert.Contains(diagnostics.UnreferencedEndpoints, "M:Api.OrdersController.Delete(System.Int32)");
    }

    [TestMethod]
    public void Re_running_link_replaces_rather_than_accumulates_diagnostics()
    {
        var indexDir = SetUpIndex(
            entryPoints: new() { new EntryPoint("M:Api.OrdersController.Delete(System.Int32)", "http", "DELETE", "api/orders/{id}") },
            frontendCalls: new());

        LinkCommand.Run(new[] { "--index", indexDir });
        LinkCommand.Run(new[] { "--index", indexDir });

        var diagnostics = JsonUtil.ReadFile<DiagnosticsModel>(Path.Combine(indexDir, "diagnostics.json"))!;
        Assert.AreEqual(1, diagnostics.UnreferencedEndpoints.Count(id => id == "M:Api.OrdersController.Delete(System.Int32)"));
    }

    [TestMethod] // review item: `${this.baseUrl}/orders` normalizes to "orders" and never equalled "api/orders" — the base URL carried the missing prefix
    public void Frontend_call_that_lost_its_base_url_matches_a_backend_route_by_suffix()
    {
        var indexDir = SetUpIndex(
            entryPoints: new() { new EntryPoint("M:Api.OrdersController.List", "http", "GET", "api/orders") },
            frontendCalls: new() { new FrontendCall("fe:a.ts:1", "a.ts", 1, "GET", "`${this.baseUrl}/orders`", "orders", "orders", "high", new(), BaseUrlDropped: true) });

        LinkCommand.Run(new[] { "--index", indexDir });

        var links = JsonlReader.Read<ApiLink>(Path.Combine(indexDir, "api-links.jsonl"));
        Assert.AreEqual(1, links.Count);
        Assert.AreEqual("suffix", links[0].MatchKind);
        Assert.AreEqual("M:Api.OrdersController.List", links[0].BackendId);
        var diagnostics = JsonUtil.ReadFile<DiagnosticsModel>(Path.Combine(indexDir, "diagnostics.json"))!;
        Assert.AreEqual(0, diagnostics.UnmatchedFrontendCalls.Count);
    }

    [TestMethod] // wildcard segments compare like they do for exact matching: {*} on both sides
    public void Suffix_match_compares_parameterized_segments()
    {
        var indexDir = SetUpIndex(
            entryPoints: new() { new EntryPoint("M:Api.OrdersController.Delete(System.Int32)", "http", "DELETE", "api/orders/{id}") },
            frontendCalls: new() { new FrontendCall("fe:a.ts:1", "a.ts", 1, "DELETE", "`${this.baseUrl}/orders/${id}`", "orders/{*}", "orders", "high", new(), BaseUrlDropped: true) });

        LinkCommand.Run(new[] { "--index", indexDir });

        var links = JsonlReader.Read<ApiLink>(Path.Combine(indexDir, "api-links.jsonl"));
        Assert.AreEqual("suffix", links.Single().MatchKind);
    }

    [TestMethod] // the suffix rule is only licensed by a dropped base URL; a route the FE wrote out in full that merely lacks a prefix is a real mismatch
    public void Suffix_match_is_not_attempted_when_the_frontend_kept_its_full_url()
    {
        var indexDir = SetUpIndex(
            entryPoints: new() { new EntryPoint("M:Api.OrdersController.List", "http", "GET", "api/orders") },
            frontendCalls: new() { new FrontendCall("fe:a.ts:1", "a.ts", 1, "GET", "'/orders'", "orders", "orders", "high", new(), BaseUrlDropped: false) });

        LinkCommand.Run(new[] { "--index", indexDir });

        Assert.AreEqual(0, JsonlReader.Read<ApiLink>(Path.Combine(indexDir, "api-links.jsonl")).Count);
        var diagnostics = JsonUtil.ReadFile<DiagnosticsModel>(Path.Combine(indexDir, "diagnostics.json"))!;
        Assert.AreEqual(1, diagnostics.UnmatchedFrontendCalls.Count);
    }

    [TestMethod]
    public void Exact_match_takes_precedence_over_a_suffix_candidate()
    {
        var indexDir = SetUpIndex(
            entryPoints: new()
            {
                new EntryPoint("M:Api.Root.List", "http", "GET", "orders"),
                new EntryPoint("M:Api.Versioned.List", "http", "GET", "api/orders"),
            },
            frontendCalls: new() { new FrontendCall("fe:a.ts:1", "a.ts", 1, "GET", "`${base}/orders`", "orders", "orders", "high", new(), BaseUrlDropped: true) });

        LinkCommand.Run(new[] { "--index", indexDir });

        var link = JsonlReader.Read<ApiLink>(Path.Combine(indexDir, "api-links.jsonl")).Single();
        Assert.AreEqual("exact", link.MatchKind);
        Assert.AreEqual("M:Api.Root.List", link.BackendId);
    }

    [TestMethod] // a suffix must end on a segment boundary: "orders" is not a suffix of "api/suborders"
    public void Suffix_match_works_on_whole_segments_not_characters()
    {
        var indexDir = SetUpIndex(
            entryPoints: new() { new EntryPoint("M:Api.SubOrders.List", "http", "GET", "api/suborders") },
            frontendCalls: new() { new FrontendCall("fe:a.ts:1", "a.ts", 1, "GET", "`${base}/orders`", "orders", "orders", "high", new(), BaseUrlDropped: true) });

        LinkCommand.Run(new[] { "--index", indexDir });

        Assert.AreEqual(0, JsonlReader.Read<ApiLink>(Path.Combine(indexDir, "api-links.jsonl")).Count);
    }

    [TestMethod]
    public void Suffix_match_requires_the_same_http_method()
    {
        var indexDir = SetUpIndex(
            entryPoints: new() { new EntryPoint("M:Api.OrdersController.Create", "http", "POST", "api/orders") },
            frontendCalls: new() { new FrontendCall("fe:a.ts:1", "a.ts", 1, "GET", "`${base}/orders`", "orders", "orders", "high", new(), BaseUrlDropped: true) });

        LinkCommand.Run(new[] { "--index", indexDir });

        Assert.AreEqual(0, JsonlReader.Read<ApiLink>(Path.Combine(indexDir, "api-links.jsonl")).Count);
    }

    [TestMethod] // `${base}/${id}` leaves only "{*}" — it would be a suffix of nearly every parameterized route, which says nothing
    public void Suffix_match_is_not_attempted_for_a_route_with_no_literal_segment()
    {
        var indexDir = SetUpIndex(
            entryPoints: new() { new EntryPoint("M:Api.OrdersController.Get(System.Int32)", "http", "GET", "api/orders/{id}") },
            frontendCalls: new() { new FrontendCall("fe:a.ts:1", "a.ts", 1, "GET", "`${base}/${id}`", "{*}", "orders", "high", new(), BaseUrlDropped: true) });

        LinkCommand.Run(new[] { "--index", indexDir });

        Assert.AreEqual(0, JsonlReader.Read<ApiLink>(Path.Combine(indexDir, "api-links.jsonl")).Count);
    }

    [TestMethod] // equally good suffix candidates: all of them are linked and flagged ambiguous (the renderers already know that kind)
    public void Several_backend_routes_ending_in_the_frontend_route_are_all_linked_as_ambiguous()
    {
        var indexDir = SetUpIndex(
            entryPoints: new()
            {
                new EntryPoint("M:Api.V1.List", "http", "GET", "api/v1/orders"),
                new EntryPoint("M:Api.V2.List", "http", "GET", "api/v2/orders"),
                new EntryPoint("M:Api.Other.List", "http", "GET", "api/customers"),
            },
            frontendCalls: new() { new FrontendCall("fe:a.ts:1", "a.ts", 1, "GET", "`${base}/orders`", "orders", "orders", "high", new(), BaseUrlDropped: true) });

        LinkCommand.Run(new[] { "--index", indexDir });

        var links = JsonlReader.Read<ApiLink>(Path.Combine(indexDir, "api-links.jsonl"));
        CollectionAssert.AreEquivalent(new[] { "M:Api.V1.List", "M:Api.V2.List" }, links.Select(l => l.BackendId).ToList());
        Assert.IsTrue(links.All(l => l.MatchKind == "ambiguous"));
    }

    [TestMethod] // frontend-calls.jsonl written by an older scan-fe has no baseUrlDropped property
    public void Frontend_calls_file_without_the_base_url_dropped_property_still_links_exactly()
    {
        var indexDir = TestPaths.NewTempDir();
        JsonUtil.WriteIndented(Path.Combine(indexDir, "entrypoints.json"), new List<EntryPoint> { new("M:Api.OrdersController.List", "http", "GET", "api/orders") });
        File.WriteAllText(Path.Combine(indexDir, "frontend-calls.jsonl"),
            "{\"id\":\"fe:a.ts:1\",\"file\":\"a.ts\",\"line\":1,\"httpMethod\":\"GET\",\"rawUrl\":\"'/api/orders'\",\"route\":\"api/orders\",\"feature\":\"orders\",\"confidence\":\"high\",\"injectedBy\":[]}\n");

        LinkCommand.Run(new[] { "--index", indexDir });

        Assert.AreEqual("exact", JsonlReader.Read<ApiLink>(Path.Combine(indexDir, "api-links.jsonl")).Single().MatchKind);
    }

    // An action with no verb attribute accepts every verb, and scan writes it as "ANY". Matching on the literal pair
    // (method, route) never linked one: no frontend call is made with the verb "ANY".
    [TestMethod]
    public void Action_without_a_verb_constraint_links_to_a_call_of_any_verb()
    {
        var indexDir = SetUpIndex(
            entryPoints: new() { new EntryPoint("M:Web.OrdersController.Cancel(System.Int32)", "http", "ANY", "orders/cancel/{id}") },
            frontendCalls: new() { new FrontendCall("fe:a.js:1", "a.js", 1, "POST", "'/orders/cancel/' + id", "orders/cancel/{*}", "orders", "low", new()) });

        LinkCommand.Run(new[] { "--index", indexDir });

        var link = JsonlReader.Read<ApiLink>(Path.Combine(indexDir, "api-links.jsonl")).Single();
        Assert.AreEqual("M:Web.OrdersController.Cancel(System.Int32)", link.BackendId);
        Assert.AreEqual("exact", link.MatchKind);
    }

    [TestMethod] // the MVC Edit pair: GET Edit(id) has no verb attribute, [HttpPost] Edit(model) has one; ASP.NET picks the constrained action for POST
    public void Action_with_a_matching_verb_wins_over_an_unconstrained_one_on_the_same_route()
    {
        var indexDir = SetUpIndex(
            entryPoints: new()
            {
                new EntryPoint("M:Web.OrdersController.Edit(System.Int32)", "http", "ANY", "orders/edit/{id}"),
                new EntryPoint("M:Web.OrdersController.Edit(Web.OrderModel)", "http", "POST", "orders/edit/{id}"),
            },
            frontendCalls: new()
            {
                new FrontendCall("fe:a.js:1", "a.js", 1, "POST", "'/orders/edit/' + id", "orders/edit/{*}", "orders", "low", new()),
                new FrontendCall("fe:a.js:2", "a.js", 2, "GET", "'/orders/edit/' + id", "orders/edit/{*}", "orders", "low", new()),
            });

        LinkCommand.Run(new[] { "--index", indexDir });

        var links = JsonlReader.Read<ApiLink>(Path.Combine(indexDir, "api-links.jsonl"));
        Assert.AreEqual("M:Web.OrdersController.Edit(Web.OrderModel)", links.Single(l => l.FrontendId == "fe:a.js:1").BackendId);
        Assert.AreEqual("M:Web.OrdersController.Edit(System.Int32)", links.Single(l => l.FrontendId == "fe:a.js:2").BackendId);
        Assert.IsTrue(links.All(l => l.MatchKind == "exact"));
    }

    [TestMethod]
    public void Unconstrained_action_is_also_found_by_suffix()
    {
        var indexDir = SetUpIndex(
            entryPoints: new() { new EntryPoint("M:Web.OrdersController.Cancel", "http", "ANY", "shop/orders/cancel") },
            frontendCalls: new() { new FrontendCall("fe:a.ts:1", "a.ts", 1, "POST", "`${base}/orders/cancel`", "orders/cancel", "orders", "high", new(), BaseUrlDropped: true) });

        LinkCommand.Run(new[] { "--index", indexDir });

        Assert.AreEqual("suffix", JsonlReader.Read<ApiLink>(Path.Combine(indexDir, "api-links.jsonl")).Single().MatchKind);
    }

    [TestMethod] // `{id?}` may be left out of the URL entirely; normalizing it to "{*}" demanded a segment that need not be there
    public void Trailing_optional_parameter_may_be_left_out_of_the_call()
    {
        var indexDir = SetUpIndex(
            entryPoints: new() { new EntryPoint("M:Api.OrdersController.Get(System.Nullable{System.Int32})", "http", "GET", "api/orders/{id?}") },
            frontendCalls: new()
            {
                new FrontendCall("fe:a.ts:1", "a.ts", 1, "GET", "'/api/orders'", "api/orders", "orders", "high", new()),
                new FrontendCall("fe:a.ts:2", "a.ts", 2, "GET", "`/api/orders/${id}`", "api/orders/{*}", "orders", "high", new()),
            });

        LinkCommand.Run(new[] { "--index", indexDir });

        var links = JsonlReader.Read<ApiLink>(Path.Combine(indexDir, "api-links.jsonl"));
        CollectionAssert.AreEquivalent(new[] { "fe:a.ts:1", "fe:a.ts:2" }, links.Select(l => l.FrontendId).ToList());
    }

    [TestMethod] // the default MVC route is {controller}/{action=Index}/{id?}: "/orders" reaches Orders.Index
    public void Conventional_index_action_is_reachable_by_the_controller_path_alone()
    {
        var indexDir = SetUpIndex(
            entryPoints: new() { new EntryPoint("M:Web.OrdersController.Index", "http", "ANY", "orders/index/{id?}", RouteInferred: true) },
            frontendCalls: new() { new FrontendCall("fe:a.js:1", "a.js", 1, "GET", "'/orders'", "orders", "orders", "low", new()) });

        LinkCommand.Run(new[] { "--index", indexDir });

        Assert.AreEqual("M:Web.OrdersController.Index", JsonlReader.Read<ApiLink>(Path.Combine(indexDir, "api-links.jsonl")).Single().BackendId);
    }

    [TestMethod] // only the convention has an Index default; an attribute route that happens to end in "index" means exactly that
    public void Declared_route_ending_in_index_is_not_shortened()
    {
        var indexDir = SetUpIndex(
            entryPoints: new() { new EntryPoint("M:Api.SearchController.Index", "http", "GET", "api/search/index") },
            frontendCalls: new() { new FrontendCall("fe:a.ts:1", "a.ts", 1, "GET", "'/api/search'", "api/search", "search", "high", new()) });

        LinkCommand.Run(new[] { "--index", indexDir });

        Assert.AreEqual(0, JsonlReader.Read<ApiLink>(Path.Combine(indexDir, "api-links.jsonl")).Count);
    }

    private static string SetUpIndex(List<EntryPoint> entryPoints, List<FrontendCall> frontendCalls)
    {
        var dir = TestPaths.NewTempDir();
        JsonUtil.WriteIndented(Path.Combine(dir, "entrypoints.json"), entryPoints);
        JsonlWriter.Write(Path.Combine(dir, "frontend-calls.jsonl"), frontendCalls);
        return dir;
    }
}
