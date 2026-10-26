using CodeMap.Query.Models;

namespace CodeMap.Tests;

/// <summary>
/// HTTP entry point detection, driven through SyntaxSymbolWalker over BCL-only compilations — so ControllerBase,
/// Controller, ApiController and every routing attribute are UNRESOLVED here, exactly as at L1. Each case is a
/// controller shape real code uses that used to produce no entry point, or a wrong route, without saying so.
/// </summary>
[TestClass]
public class HttpEntryPointDetectionTests
{
    [TestMethod] // MVC conventional routing: no verb attribute, no route attribute anywhere
    public void Action_without_attributes_gets_the_default_conventional_route_marked_inferred()
    {
        var eps = EntryPointsOf("""
            namespace W;
            public class HomeController : Controller
            {
                public object Index() => null;
                public System.Threading.Tasks.Task<object> ListAsync() => null;
                [ActionName("about-us")] public object About() => null;
                [NonAction] public void Helper() {}
                private void Hidden() {}
                public static void Shared() {}
            }
            """);

        var index = TestAssert.Single(eps, e => e.Id == "M:W.HomeController.Index");
        Assert.AreEqual("ANY", index.HttpMethod);
        Assert.AreEqual("home/index/{id?}", index.Route);
        Assert.AreEqual(true, index.RouteInferred);

        Assert.AreEqual("home/list/{id?}", TestAssert.Single(eps, e => e.Id == "M:W.HomeController.ListAsync").Route); // Async suffix dropped, as ASP.NET Core does
        Assert.AreEqual("home/about-us/{id?}", TestAssert.Single(eps, e => e.Id == "M:W.HomeController.About").Route);
        Assert.AreEqual(3, eps.Count, "NonAction, private and static methods are not actions");
    }

    [TestMethod] // used to compose with an empty controller route and record Route = ""
    public void Verb_attribute_without_template_in_a_conventionally_routed_controller_uses_the_conventional_route()
    {
        var ep = TestAssert.Single(EntryPointsOf("""
            namespace W;
            public class CartController : Controller
            {
                [HttpPost] public void Save(int id) {}
            }
            """), e => e.Id == "M:W.CartController.Save(System.Int32)");

        Assert.AreEqual("POST", ep.HttpMethod);
        Assert.AreEqual("cart/save/{id?}", ep.Route);
        Assert.AreEqual(true, ep.RouteInferred);
    }

    [TestMethod]
    public void Method_level_Route_attribute_is_composed_with_the_controller_route()
    {
        var eps = EntryPointsOf("""
            namespace W;
            [Route("api/[controller]")]
            public class ItemsController : ControllerBase
            {
                [HttpGet]
                [Route("by-name/{name}")]
                public void ByName(string name) {}

                [Route("export")]
                public void Export() {}

                [HttpGet]
                public void All() {}
            }
            """);

        var byName = TestAssert.Single(eps, e => e.Id == "M:W.ItemsController.ByName(System.String)");
        Assert.AreEqual("GET", byName.HttpMethod);
        Assert.AreEqual("api/items/by-name/{name}", byName.Route);
        Assert.IsNull(byName.RouteInferred);

        var export = TestAssert.Single(eps, e => e.Id == "M:W.ItemsController.Export");
        Assert.AreEqual("ANY", export.HttpMethod); // no verb constraint: ASP.NET Core accepts every verb
        Assert.AreEqual("api/items/export", export.Route);

        Assert.AreEqual("api/items", TestAssert.Single(eps, e => e.Id == "M:W.ItemsController.All").Route);
    }

    [TestMethod]
    public void Action_token_is_replaced_like_the_controller_token()
    {
        var ep = TestAssert.Single(EntryPointsOf("""
            namespace W;
            [Route("api/[controller]/[action]")]
            public class ReportsController : ControllerBase
            {
                [HttpGet] public void Monthly() {}
            }
            """), e => e.Id == "M:W.ReportsController.Monthly");

        Assert.AreEqual("api/reports/monthly", ep.Route);
    }

    [TestMethod] // a template starting with "/" or "~/" replaces the controller route instead of extending it
    public void Absolute_method_template_is_not_composed_with_the_controller_route()
    {
        var eps = EntryPointsOf("""
            namespace W;
            [Route("api/[controller]")]
            public class OpsController : ControllerBase
            {
                [HttpGet("/health")] public void Health() {}
                [HttpGet("~/legacy/ops")] public void Legacy() {}
            }
            """);

        Assert.AreEqual("health", TestAssert.Single(eps, e => e.Id == "M:W.OpsController.Health").Route);
        Assert.AreEqual("legacy/ops", TestAssert.Single(eps, e => e.Id == "M:W.OpsController.Legacy").Route);
    }

    [TestMethod]
    public void Const_and_nameof_templates_are_resolved()
    {
        var eps = EntryPointsOf("""
            namespace W;
            public static class Routes
            {
                public const string Base = "api/catalog";
                public const string ById = "{id}";
            }
            [Route(Routes.Base)]
            public class CatalogController : ControllerBase
            {
                [HttpGet(Routes.ById)] public void Get(int id) {}
                [HttpPost(nameof(Archive))] public void Archive() {}
            }
            """);

        Assert.AreEqual("api/catalog/{id}", TestAssert.Single(eps, e => e.Id == "M:W.CatalogController.Get(System.Int32)").Route);
        Assert.AreEqual("api/catalog/Archive", TestAssert.Single(eps, e => e.Id == "M:W.CatalogController.Archive").Route);
    }

    [TestMethod] // ASP.NET Web API 2: System.Web.Http.ApiController, verb from the method-name prefix, api/{controller}/{id}
    public void Web_API_2_controller_uses_its_conventional_route_and_verb_by_name()
    {
        var eps = EntryPointsOf("""
            namespace W;
            public class ProductsController : ApiController
            {
                public object GetAll() => null;
                public void PostProduct(object p) {}
                public void Rename(int id) {}
            }
            """);

        var getAll = TestAssert.Single(eps, e => e.Id == "M:W.ProductsController.GetAll");
        Assert.AreEqual("GET", getAll.HttpMethod);
        Assert.AreEqual("api/products/{id?}", getAll.Route);
        Assert.AreEqual(true, getAll.RouteInferred);
        Assert.AreEqual("POST", TestAssert.Single(eps, e => e.Id == "M:W.ProductsController.PostProduct(System.Object)").HttpMethod);
        Assert.AreEqual("POST", TestAssert.Single(eps, e => e.Id == "M:W.ProductsController.Rename(System.Int32)").HttpMethod); // Web API's default
    }

    [TestMethod]
    public void Web_API_2_RoutePrefix_is_composed_with_the_method_Route()
    {
        var ep = TestAssert.Single(EntryPointsOf("""
            namespace W;
            [RoutePrefix("api/v2/orders")]
            public class OrdersV2Controller : ApiController
            {
                [HttpGet]
                [Route("{id}")]
                public object Get(int id) => null;
            }
            """), e => e.Id == "M:W.OrdersV2Controller.Get(System.Int32)");

        Assert.AreEqual("GET", ep.HttpMethod);
        Assert.AreEqual("api/v2/orders/{id}", ep.Route);
        Assert.IsNull(ep.RouteInferred);
    }

    [TestMethod] // L1: a vendor base class (ABP, a company framework...) can't resolve, so its name can't be checked
    public void Controller_over_an_unresolved_base_is_recognized_by_its_name_suffix()
    {
        var eps = EntryPointsOf("""
            namespace W;
            [Route("api/[controller]")]
            public class InvoicesController : VendorApiBase
            {
                [HttpGet("{id}")] public void Get(int id) {}
            }
            [ApiController]
            [Route("api/shipping")]
            public class ShippingEndpoint : VendorThing
            {
                [HttpGet] public void Quote() {}
            }
            """);

        Assert.AreEqual("api/invoices/{id}", TestAssert.Single(eps, e => e.Id == "M:W.InvoicesController.Get(System.Int32)").Route);
        Assert.AreEqual("api/shipping", TestAssert.Single(eps, e => e.Id == "M:W.ShippingEndpoint.Quote").Route); // [ApiController] says so
    }

    [TestMethod] // the suffix alone is not enough when the base type resolved and isn't a controller
    public void Class_named_Controller_over_a_resolved_non_controller_base_is_not_an_entry_point()
    {
        var eps = EntryPointsOf("""
            namespace W;
            public class GameController
            {
                public void Move(int x) {}
            }
            """);

        Assert.AreEqual(0, eps.Count);
    }

    private static List<EntryPoint> EntryPointsOf(string source)
        => SyntaxSymbolWalkerTests.Walk(source).EntryPoints.Where(e => e.Type == "http").ToList();
}
