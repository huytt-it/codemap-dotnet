using CodeMap.Query.Json;
using CodeMap.Roslyn.Scan;

namespace CodeMap.Tests;

/// <summary>
/// Review Fix Pass v1, Task "sửa renderer (ưu tiên binding thật)" — docs/BENCHMARK-INTERFACE-EXPANSION.md found
/// di.json unusable as ground truth for "is this the real DI binding" because it merges real registrations with
/// a structural "implements" fallback populated from the SAME semantic fact that drives interface-expand edges
/// (proven with this exact fixture: FakeOrderService has no AddScoped/attribute registration anywhere, yet
/// still showed up in di.json's IOrderService list). di-confirmed.json is the fix — same shape, built from
/// ONLY real evidence (fluent registration, [Injectable] attribute, manual override), no structural fallback.
/// </summary>
[TestClass]
public class DiConfirmedJsonTests
{
    private static Dictionary<string, List<string>> _diConfirmed = null!;

    [ClassInitialize]
    public static void ClassInit(TestContext _)
    {
        L2TestSetup.EnsureFixtureRestored();

        var outDir = TestPaths.NewTempDir();
        new SemanticScanner(includeExternal: false).Scan(TestPaths.FixtureSolution, outDir);

        _diConfirmed = JsonUtil.ReadFile<Dictionary<string, List<string>>>(Path.Combine(outDir, "index", "di-confirmed.json")) ?? new();
    }

    [TestMethod] // the exact bug found in docs/BENCHMARK-INTERFACE-EXPANSION.md, reproduced locally
    public void Structural_only_implementer_with_no_real_registration_is_excluded()
    {
        Assert.IsTrue(_diConfirmed.TryGetValue("T:Orders.IOrderService", out var impls));
        CollectionAssert.DoesNotContain(impls, "T:Orders.FakeOrderService");
    }

    [TestMethod]
    public void Fluent_registered_implementation_is_included()
    {
        Assert.IsTrue(_diConfirmed.TryGetValue("T:Orders.IOrderService", out var impls));
        CollectionAssert.Contains(impls, "T:Orders.OrderService");
    }

    [TestMethod] // P10 attribute-convention binding must ALSO count as confirmed, not just fluent
    public void Attribute_convention_binding_is_included()
    {
        Assert.IsTrue(_diConfirmed.TryGetValue("T:Orders.INotifier", out var impls));
        CollectionAssert.Contains(impls, "T:Orders.EmailNotifier");
    }
}
