using CodeMap.Query.FrontendScan;
using CodeMap.Query.Json;
using CodeMap.Query.Link;
using CodeMap.Query.Models;

namespace CodeMap.Tests;

/// <summary>
/// End-to-end recall checks for scan-fe + link: shapes of real frontend code that used to leave a call
/// unlinked although the backend endpoint it hits is in the index. Each test builds a throw-away frontend
/// folder and a hand-written entrypoints.json, so it exercises the exact seams (scan-fe's normalization, the
/// frontend-calls.jsonl contract, link's matching) without needing Roslyn or the shared fixtures.
/// </summary>
[TestClass]
public class FrontendLinkRecallTests
{
    [TestMethod] // `API_BASE + '/orders'`: the base is dropped by the normalizer, the backend route is `api/orders`
    public void Jquery_call_prefixed_by_a_base_url_variable_links_to_the_backend_route_by_suffix()
    {
        var root = TestPaths.NewTempDir();
        File.WriteAllText(Path.Combine(root, "orders.js"), "function load() {\n  $.get(API_BASE + '/orders', render);\n}\n");

        var (links, diagnostics) = ScanAndLink(root, new EntryPoint("M:Api.OrdersController.List", "http", "GET", "api/orders"));

        var link = links.Single();
        Assert.AreEqual("suffix", link.MatchKind);
        Assert.AreEqual("M:Api.OrdersController.List", link.BackendId);
        Assert.AreEqual(0, diagnostics.UnmatchedFrontendCalls.Count);
    }

    /// <summary>Runs scan-fe over <paramref name="frontendRoot"/>, drops a hand-made entrypoints.json next to its output, runs link, returns what link wrote.</summary>
    private static (List<ApiLink> Links, DiagnosticsModel Diagnostics) ScanAndLink(string frontendRoot, params EntryPoint[] entryPoints)
    {
        var outDir = TestPaths.NewTempDir();
        ScanFeCommand.Run(new[] { "--root", frontendRoot, "--out", outDir });
        var indexDir = Path.Combine(outDir, "index");
        JsonUtil.WriteIndented(Path.Combine(indexDir, "entrypoints.json"), entryPoints.ToList());

        LinkCommand.Run(new[] { "--index", indexDir });

        return (JsonlReader.Read<ApiLink>(Path.Combine(indexDir, "api-links.jsonl")),
            JsonUtil.ReadFile<DiagnosticsModel>(Path.Combine(indexDir, "diagnostics.json"))!);
    }
}
