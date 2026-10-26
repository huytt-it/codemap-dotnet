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

    [TestMethod] // MVC view: the literal URL in <script> links to the backend; the Razor-built one is surfaced in diagnostics, not guessed
    public void Mvc_view_script_url_links_and_razor_built_url_is_reported_with_its_reason()
    {
        var root = TestPaths.NewTempDir();
        Directory.CreateDirectory(Path.Combine(root, "Views", "Orders"));
        File.WriteAllText(Path.Combine(root, "Views", "Orders", "Index.cshtml"),
            "<script>\n$.ajax('/api/orders/' + id, { type: 'DELETE' });\n$.post('@Url.Action(\"Create\", \"Orders\")', data);\n</script>\n");

        var (links, diagnostics) = ScanAndLink(root, new EntryPoint("M:Api.OrdersController.Delete(System.Int32)", "http", "DELETE", "api/orders/{id}"));

        var link = links.Single();
        Assert.AreEqual("exact", link.MatchKind);
        Assert.AreEqual("fe:Views/Orders/Index.cshtml:2", link.FrontendId);
        var unparsed = diagnostics.UnparsedFrontendUrls.Single();
        Assert.AreEqual("Views/Orders/Index.cshtml", unparsed.File);
        Assert.AreEqual(3, unparsed.Line);
        Assert.AreEqual("POST", unparsed.HttpMethod);
        StringAssert.Contains(unparsed.RawUrl, "@Url.Action");
        StringAssert.Contains(unparsed.Reason, "conventional routing");
    }

    [TestMethod] // all three Angular gaps at once: a non-"http" receiver name, a base URL field, a component injecting through inject()
    public void Angular_service_using_inject_and_a_base_url_field_is_linked_and_attributed_to_its_component()
    {
        var root = TestPaths.NewTempDir();
        Directory.CreateDirectory(Path.Combine(root, "src", "app", "orders"));
        File.WriteAllText(Path.Combine(root, "src", "app", "orders", "order-api.service.ts"),
            "import { HttpClient, inject } from '@angular/common/http';\n@Injectable({ providedIn: 'root' })\nexport class OrderApiService {\n  private readonly baseUrl = environment.apiUrl;\n  private readonly api = inject(HttpClient);\n  list() { return this.api.get(`${this.baseUrl}/orders`); }\n}\n");
        File.WriteAllText(Path.Combine(root, "src", "app", "orders", "order-list.component.ts"),
            "@Component({ selector: 'app-order-list' })\nexport class OrderListComponent {\n  private readonly orderApi = inject(OrderApiService);\n}\n");
        var outcome = TypeScriptCallScanner.Scan(root);
        if (outcome.SkippedReason != null)
            Assert.Inconclusive($"TypeScript scanning unavailable in this environment: {outcome.SkippedReason}");

        var (links, diagnostics) = ScanAndLink(root, new EntryPoint("M:Api.OrdersController.List", "http", "GET", "api/orders"));

        var link = links.Single();
        Assert.AreEqual("suffix", link.MatchKind);
        Assert.AreEqual("M:Api.OrdersController.List", link.BackendId);
        Assert.AreEqual(0, diagnostics.UnresolvedFrontendInjections.Count);
        var call = outcome.Calls.Single();
        CollectionAssert.AreEqual(new[] { "OrderListComponent" }, call.InjectedBy);
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
