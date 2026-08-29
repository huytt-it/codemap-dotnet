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

    private static string SetUpIndex(List<EntryPoint> entryPoints, List<FrontendCall> frontendCalls)
    {
        var dir = TestPaths.NewTempDir();
        JsonUtil.WriteIndented(Path.Combine(dir, "entrypoints.json"), entryPoints);
        JsonlWriter.Write(Path.Combine(dir, "frontend-calls.jsonl"), frontendCalls);
        return dir;
    }
}
