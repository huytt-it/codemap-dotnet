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

    [TestMethod]
    public void Mixed_case_is_lowercased_for_comparison()
    {
        Assert.AreEqual("api/orders", FrontendUrlNormalizer.Normalize("'/API/Orders'"));
    }
}
