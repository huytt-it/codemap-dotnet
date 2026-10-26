using CodeMap.Query.Impact;
using CodeMap.Roslyn.Scan;

namespace CodeMap.Tests;

/// <summary>
/// A call through a base type — `BaseRepository r; r.Save()` — binds to the abstract/virtual member, and what runs
/// is an override. Without an edge to each override, impact on OrderRepository.Save missed every caller going
/// through the base type. Mirrors the interface expansion: the direct edge stays, and each override reachable
/// from the receiver's static type gets a `call` edge marked via:"override".
/// </summary>
[TestClass]
public class OverrideExpansionTests
{
    private const string Source = """
        namespace O;

        public abstract class BaseRepository
        {
            public abstract void Save();
            public virtual void Delete() { }
            public void SaveAll() { Save(); }
        }

        public class OrderRepository : BaseRepository
        {
            public override void Save() { }
            public override void Delete() { base.Delete(); }
        }

        public class CustomerRepository : BaseRepository
        {
            public override void Save() { }
        }

        public class AuditedOrderRepository : OrderRepository
        {
            public override void Save() { }
        }

        public abstract class ReadOnlyRepository : BaseRepository { }

        public class LookupRepository : ReadOnlyRepository
        {
            public override void Save() { }
        }

        public class Repo<T>
        {
            public virtual void Add(T item) { }
        }

        public class Order { }

        public class OrderRepo : Repo<Order>
        {
            public override void Add(Order item) { }
        }

        public class Service
        {
            public void Abstract(BaseRepository r) => r.Save();
            public void Virtual(BaseRepository r) => r.Delete();
            public void ThroughDerived(OrderRepository r) => r.Save();
            public void ThroughIntermediate(ReadOnlyRepository r) => r.Save();
            public void Generic(Repo<Order> r) => r.Add(new Order());
        }
        """;

    private static readonly ScanResults Results = InMemoryScan.Run(Source);

    private static List<string> OverrideTargets(string from)
        => Results.Edges.Where(e => e.From == from && e.Via == "override").Select(e => e.To).OrderBy(t => t, StringComparer.Ordinal).ToList();

    [TestMethod]
    public void Call_to_an_abstract_member_reaches_every_override_including_transitive_ones()
    {
        CollectionAssert.AreEqual(
            new[] { "M:O.AuditedOrderRepository.Save", "M:O.CustomerRepository.Save", "M:O.LookupRepository.Save", "M:O.OrderRepository.Save" },
            OverrideTargets("M:O.Service.Abstract(O.BaseRepository)"));
        TestAssert.Single(Results.Edges, e => e.From == "M:O.Service.Abstract(O.BaseRepository)" && e.To == "M:O.BaseRepository.Save" && e.Via == null);
    }

    [TestMethod]
    public void Call_to_a_virtual_member_reaches_its_override()
        => CollectionAssert.AreEqual(new[] { "M:O.OrderRepository.Delete" }, OverrideTargets("M:O.Service.Virtual(O.BaseRepository)"));

    [TestMethod] // the template-method shape: an implicit-this call inside the base class
    public void Implicit_this_call_in_the_base_reaches_every_override()
        => CollectionAssert.AreEqual(
            new[] { "M:O.AuditedOrderRepository.Save", "M:O.CustomerRepository.Save", "M:O.LookupRepository.Save", "M:O.OrderRepository.Save" },
            OverrideTargets("M:O.BaseRepository.SaveAll"));

    [TestMethod] // `r.Save()` binds to OrderRepository.Save itself; only what overrides that can run
    public void Call_through_a_derived_type_reaches_the_overrides_below_it()
        => CollectionAssert.AreEqual(new[] { "M:O.AuditedOrderRepository.Save" }, OverrideTargets("M:O.Service.ThroughDerived(O.OrderRepository)"));

    [TestMethod] // ReadOnlyRepository doesn't override Save, so `r.Save()` binds to BaseRepository.Save — yet a ReadOnlyRepository is never an OrderRepository
    public void Only_overrides_below_the_receivers_static_type_are_reached()
    {
        TestAssert.Single(Results.Edges, e => e.From == "M:O.Service.ThroughIntermediate(O.ReadOnlyRepository)" && e.To == "M:O.BaseRepository.Save" && e.Via == null);
        CollectionAssert.AreEqual(new[] { "M:O.LookupRepository.Save" }, OverrideTargets("M:O.Service.ThroughIntermediate(O.ReadOnlyRepository)"));
    }

    [TestMethod] // `base.Delete()` is non-virtual: it runs exactly BaseRepository.Delete
    public void Base_call_is_not_expanded()
    {
        TestAssert.Single(Results.Edges, e => e.From == "M:O.OrderRepository.Delete" && e.To == "M:O.BaseRepository.Delete" && e.Via == null);
        CollectionAssert.AreEqual(Array.Empty<string>(), OverrideTargets("M:O.OrderRepository.Delete"));
    }

    [TestMethod]
    public void Override_of_a_generic_base_member_is_reached_through_the_constructed_base()
        => CollectionAssert.AreEqual(new[] { "M:O.OrderRepo.Add(O.Order)" }, OverrideTargets("M:O.Service.Generic(O.Repo{O.Order})"));

    [TestMethod] // the end-to-end symptom
    public void Impact_on_an_override_finds_the_caller_that_goes_through_the_base_type()
    {
        var indexDir = TestPaths.NewTempDir();
        Query.Json.JsonlWriter.Write(Path.Combine(indexDir, "symbols.jsonl"), Results.Symbols);
        Query.Json.JsonlWriter.Write(Path.Combine(indexDir, "edges.jsonl"), Results.Edges);

        var result = ImpactEngine.Traverse(ImpactIndex.Load(indexDir), "M:O.CustomerRepository.Save", depth: 1);

        CollectionAssert.AreEquivalent( // Predecessors: every caller reached (by name, the in-memory project is tiered as tests)
            new[] { "M:O.Service.Abstract(O.BaseRepository)", "M:O.BaseRepository.SaveAll" },
            result.Predecessors.Keys.ToList());
    }
}
