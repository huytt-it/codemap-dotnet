using CodeMap.Query.FrontendScan;

namespace CodeMap.Tests;

/// <summary>Spec section 6, "Normalize URL thành route pattern" — the frontend half (Angular template literals, jQuery string concatenation).</summary>
[TestClass]
public class FrontendUrlNormalizerTests
{
    [TestMethod]
    public void Template_literal_interpolation_becomes_a_wildcard_segment()
    {
        Assert.AreEqual("api/orders/{*}", FrontendUrlNormalizer.Normalize("`/api/orders/${id}`"));
    }

    [TestMethod]
    public void Plain_string_literal_is_lowercased_and_trimmed()
    {
        Assert.AreEqual("api/orders", FrontendUrlNormalizer.Normalize("'/api/orders'"));
    }

    [TestMethod]
    public void String_concatenation_interpolation_becomes_a_wildcard_segment()
    {
        Assert.AreEqual("api/orders/{*}", FrontendUrlNormalizer.Normalize("'/api/orders/' + id"));
    }

    [TestMethod]
    public void Leading_environment_variable_is_dropped_entirely_not_turned_into_a_wildcard()
    {
        Assert.AreEqual("api/orders", FrontendUrlNormalizer.Normalize("`${environment.apiUrl}/api/orders`"));
    }

    [TestMethod]
    public void Leading_bare_identifier_prefix_is_dropped_entirely()
    {
        Assert.AreEqual("api/orders", FrontendUrlNormalizer.Normalize("API_BASE + '/api/orders'"));
    }

    [TestMethod]
    public void Query_string_is_stripped()
    {
        Assert.AreEqual("api/orders", FrontendUrlNormalizer.Normalize("'/api/orders?status=open'"));
    }

    [TestMethod]
    public void Double_curly_interpolation_becomes_a_wildcard_segment()
    {
        Assert.AreEqual("api/orders/{*}", FrontendUrlNormalizer.Normalize("'/api/orders/{{id}}'"));
    }

    [TestMethod]
    public void Bare_variable_with_no_path_structure_is_unparseable()
    {
        Assert.IsNull(FrontendUrlNormalizer.Normalize("endpoint"));
    }

    [TestMethod] // `link` may suffix-match only when the front of the path was thrown away, so the normalizer has to say so
    public void Dropped_leading_base_url_is_reported()
    {
        var withEnvironment = FrontendUrlNormalizer.NormalizeDetailed("`${environment.apiUrl}/api/orders`")!;
        Assert.AreEqual("api/orders", withEnvironment.Route);
        Assert.IsTrue(withEnvironment.BaseUrlDropped);

        var withThisField = FrontendUrlNormalizer.NormalizeDetailed("`${this.baseUrl}/orders`")!;
        Assert.AreEqual("orders", withThisField.Route);
        Assert.IsTrue(withThisField.BaseUrlDropped);

        Assert.IsTrue(FrontendUrlNormalizer.NormalizeDetailed("API_BASE + '/orders'")!.BaseUrlDropped);
    }

    [TestMethod]
    public void Url_that_starts_with_a_literal_has_no_dropped_base_url()
    {
        Assert.IsFalse(FrontendUrlNormalizer.NormalizeDetailed("`/api/orders/${id}`")!.BaseUrlDropped);
        Assert.IsFalse(FrontendUrlNormalizer.NormalizeDetailed("'/api/orders/' + id")!.BaseUrlDropped);
    }

    [TestMethod] // an interpolation in the middle is a wildcard segment, not a base URL that was dropped
    public void Interpolation_after_a_literal_prefix_is_not_a_dropped_base_url()
    {
        Assert.IsFalse(FrontendUrlNormalizer.NormalizeDetailed("'/api/' + area + '/orders'")!.BaseUrlDropped);
    }

    [TestMethod]
    public void Normalize_and_NormalizeDetailed_agree_on_the_route()
    {
        Assert.AreEqual(FrontendUrlNormalizer.Normalize("`${this.baseUrl}/orders/${id}`"), FrontendUrlNormalizer.NormalizeDetailed("`${this.baseUrl}/orders/${id}`")!.Route);
        Assert.IsNull(FrontendUrlNormalizer.NormalizeDetailed("endpoint"));
    }

    [TestMethod]
    public void Mixed_case_is_lowercased_for_comparison()
    {
        Assert.AreEqual("api/orders", FrontendUrlNormalizer.Normalize("'/API/Orders'"));
    }
}
