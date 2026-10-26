namespace CodeMap.Tests;

/// <summary>
/// Every `new`/`read` edge must point at a symbol in symbols.jsonl, or impact on that target finds nothing to start
/// from. Constructors nobody wrote by hand (a class's implicit parameterless one, a record's or class's primary
/// constructor) and enum members were never emitted as symbols, while edges to them were: eShopOnWeb had 41
/// dangling `new` edges (DTOs and view models built with `new X { ... }`) and 6 dangling enum reads. An implicit
/// constructor only becomes a symbol once something constructs it, which ScanResults decides from all edges.
/// </summary>
[TestClass]
public class DanglingEdgeTargetTests
{
    private const string Source = """
        namespace D;

        public class Implicit { }
        public struct Point { public int X; }
        public record Person(string Name);
        public class Primary(int id) { public int Id => id; }
        public partial class Split { }
        public partial class Split { }
        public abstract class AbstractBase { }
        public class Derived : AbstractBase { }
        public static class Holder { }
        public enum Level { Low, High = 5 }
        public delegate void Notify();

        public class User
        {
            public object A() => new Implicit();
            public object B() => new Point();
            public object C() => new Person("x");
            public object D() => new Primary(1);
            public object E() => new Split();
            public object F() => new Derived();
            public Level G() => Level.High;
            public Notify H() => new Notify(Target);
            private void Target() { }
        }
        """;

    private static readonly Roslyn.Scan.ScanResults Results = InMemoryScan.Run(Source);

    [TestMethod]
    public void Every_new_and_read_edge_points_at_an_emitted_symbol()
    {
        var symbolIds = Results.Symbols.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var dangling = Results.Edges.Where(e => e.Kind is "new" or "read" && !symbolIds.Contains(e.To)).Select(e => $"{e.Kind} {e.To}").ToList();
        CollectionAssert.AreEqual(Array.Empty<string>(), dangling, string.Join(", ", dangling));
    }

    [DataTestMethod]
    [DataRow("M:D.Implicit.#ctor", 3)]
    [DataRow("M:D.Point.#ctor", 4)]
    [DataRow("M:D.Person.#ctor(System.String)", 5)]
    [DataRow("M:D.Primary.#ctor(System.Int32)", 6)]
    [DataRow("M:D.Split.#ctor", 7)]
    [DataRow("M:D.Derived.#ctor", 10)]
    public void Constructor_not_written_by_hand_is_a_symbol_at_its_type_declaration(string id, int line)
    {
        var ctor = TestAssert.Single(Results.Symbols, s => s.Id == id);
        Assert.AreEqual("Constructor", ctor.Kind);
        Assert.AreEqual(".ctor", ctor.Name);
        Assert.AreEqual(line, ctor.Line);
        Assert.AreEqual(id[2..id.IndexOf(".#ctor", StringComparison.Ordinal)], ctor.ContainingType);
    }

    [TestMethod] // every class without a constructor has an implicit one; only a constructed one is worth a symbol
    public void Implicit_constructor_nothing_constructs_is_not_a_symbol()
        => TestAssert.DoesNotContain(Results.Symbols, s => s.Id == "M:D.AbstractBase.#ctor");

    [TestMethod] // not a record's copy constructor (only `with` uses it), and a static class has none
    public void No_other_constructor_is_emitted()
    {
        TestAssert.DoesNotContain(Results.Symbols, s => s.Id == "M:D.Person.#ctor(D.Person)");
        TestAssert.DoesNotContain(Results.Symbols, s => s.Id.StartsWith("M:D.Holder.#ctor", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Enum_member_is_a_symbol_inside_its_enum()
    {
        var member = TestAssert.Single(Results.Symbols, s => s.Id == "F:D.Level.High");
        Assert.AreEqual("EnumMember", member.Kind);
        Assert.AreEqual("D.Level", member.ContainingType);
        Assert.AreEqual(12, member.Line);
    }
}
