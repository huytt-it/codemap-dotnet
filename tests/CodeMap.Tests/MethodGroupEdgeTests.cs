using CodeMap.Query.Models;

namespace CodeMap.Tests;

/// <summary>
/// A method named without being invoked — `.Select(MapToDto)`, `+= OnClick`, `new Func&lt;int, int&gt;(Twice)`,
/// `Run(Work)` — becomes a delegate that something else invokes later. The caller is the code that handed the
/// method over; before, only an InvocationExpression produced a call edge, so all of these lost their caller.
/// </summary>
[TestClass]
public class MethodGroupEdgeTests
{
    private const string Source = """
        using System;
        using System.Collections.Generic;
        using System.Linq;

        namespace N;

        public class Dto { }

        public class Mapper
        {
            public static Dto MapToDto(int i) => new Dto();
            public event EventHandler? Clicked;
            public void OnClick(object? sender, EventArgs e) { }
            public int Twice(int x) => x * 2;
            public void Work() { }
            public static void Run(Action a) => a();

            public List<Dto> Select(List<int> xs) => xs.Select(MapToDto).ToList();
            public void Subscribe() { Clicked += OnClick; }
            public Func<int, int> Create() => new Func<int, int>(Twice);
            public void PassAsDelegate() { Run(Work); }
            public void PassQualified() { Run(this.Work); }
            public void Assign() { Action a = Work; a(); }
            public void DirectCall() { Work(); }
            public string NameOf() => nameof(Work);
            public void Unsubscribe() { Clicked -= OnClick; }
        }
        """;

    private static readonly List<EdgeRecord> Edges = SyntaxSymbolWalkerTests.Walk(Source).DirectEdges;

    [DataTestMethod]
    [DataRow("M:N.Mapper.Select(System.Collections.Generic.List{System.Int32})", "M:N.Mapper.MapToDto(System.Int32)")]
    [DataRow("M:N.Mapper.Subscribe", "M:N.Mapper.OnClick(System.Object,System.EventArgs)")]
    [DataRow("M:N.Mapper.Create", "M:N.Mapper.Twice(System.Int32)")]
    [DataRow("M:N.Mapper.PassAsDelegate", "M:N.Mapper.Work")]
    [DataRow("M:N.Mapper.PassQualified", "M:N.Mapper.Work")]
    [DataRow("M:N.Mapper.Assign", "M:N.Mapper.Work")]
    public void Method_group_converted_to_a_delegate_is_a_call_edge_marked_via_delegate(string from, string to)
    {
        var edge = TestAssert.Single(Edges, e => e.From == from && e.To == to);
        Assert.AreEqual("call", edge.Kind);
        Assert.AreEqual("delegate", edge.Via);
    }

    [TestMethod]
    public void Ordinary_invocation_still_produces_exactly_one_unmarked_call_edge()
    {
        var edge = TestAssert.Single(Edges, e => e.From == "M:N.Mapper.DirectCall" && e.To == "M:N.Mapper.Work");
        Assert.AreEqual("call", edge.Kind);
        Assert.IsNull(edge.Via);
    }

    [TestMethod] // the delegate being passed to Run doesn't change Run's own call edge
    public void Call_receiving_the_delegate_keeps_its_own_edge()
    {
        var edge = TestAssert.Single(Edges, e => e.From == "M:N.Mapper.PassAsDelegate" && e.To == "M:N.Mapper.Run(System.Action)");
        Assert.IsNull(edge.Via);
    }

    [TestMethod] // nameof(Work) names the method; nothing can ever invoke it through that expression
    public void Nameof_is_not_an_edge()
        => TestAssert.DoesNotContain(Edges, e => e.From == "M:N.Mapper.NameOf");

    [TestMethod] // `-=` takes the handler out: nothing runs it because of this line
    public void Unsubscribing_is_not_an_edge()
        => TestAssert.DoesNotContain(Edges, e => e.From == "M:N.Mapper.Unsubscribe");
}
