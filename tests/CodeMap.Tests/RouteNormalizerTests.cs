using CodeMap.Query.FrontendScan;

namespace CodeMap.Tests;

[TestClass]
public class RouteNormalizerTests
{
    [TestMethod]
    public void Route_parameter_becomes_a_wildcard_segment()
    {
        Assert.AreEqual("api/orders/{*}", RouteNormalizer.NormalizeBackendRoute("api/orders/{id}"));
    }

    [TestMethod]
    public void Route_parameter_with_type_constraint_becomes_a_wildcard_segment()
    {
        Assert.AreEqual("api/orders/{*}", RouteNormalizer.NormalizeBackendRoute("api/orders/{id:int}"));
    }

    [TestMethod]
    public void Already_normalized_style_route_matches_the_frontend_normalized_form()
    {
        var backend = RouteNormalizer.NormalizeBackendRoute("api/orders/{id}");
        var frontend = FrontendUrlNormalizer.Normalize("`/api/orders/${id}`");
        Assert.AreEqual(backend, frontend);
    }

    [TestMethod]
    public void Case_is_normalized()
    {
        Assert.AreEqual("api/orders", RouteNormalizer.NormalizeBackendRoute("Api/Orders"));
    }
}
