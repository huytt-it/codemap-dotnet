namespace CodeMap.Tests;

/// <summary>
/// At L1 a NuGet type doesn't resolve, so neither does an argument built from one — `User.Identity.Name` in a
/// controller whose ControllerBase is unresolved. Roslyn then reports the call or `new` as an overload-resolution
/// failure with no Symbol, even when the method has a single overload and the target is not in doubt; the walker
/// dropped it, along with the MediatR Send site wrapping it. One candidate is taken; several are not guessed at.
/// </summary>
[TestClass]
public class UnresolvedArgumentEdgeTests
{
    private const string Source = """
        using System.Threading;
        using System.Threading.Tasks;
        using MediatR;

        namespace U;

        public class GetMyOrders : IRequest<int> { public GetMyOrders(string userName) { } }
        public class GetMyOrdersHandler : IRequestHandler<GetMyOrders, int>
        {
            public Task<int> Handle(GetMyOrders request, CancellationToken cancellationToken) => Task.FromResult(1);
        }

        public class Repo
        {
            public void Save(string name) { }
            public void Over(int id) { }
            public void Over(string name) { }
        }

        public class OrderController : Controller
        {
            private readonly IMediator _mediator = null!;
            private readonly Repo _repo = new Repo();

            public void Create() { var query = new GetMyOrders(User.Identity.Name); }
            public void Call() { _repo.Save(User.Identity.Name); }
            public void Ambiguous() { _repo.Over(User.Identity.Name); }
            public Task<int> MyOrders() => _mediator.Send(new GetMyOrders(User.Identity.Name));
        }
        """;

    private static readonly Roslyn.Scan.ScanResults Results = InMemoryScan.Run(Source);

    [TestMethod]
    public void New_with_an_unresolved_argument_binds_to_the_only_constructor()
        => TestAssert.Single(Results.Edges, e => e.From == "M:U.OrderController.Create" && e.Kind == "new" && e.To == "M:U.GetMyOrders.#ctor(System.String)");

    [TestMethod]
    public void Call_with_an_unresolved_argument_binds_to_the_only_overload()
        => TestAssert.Single(Results.Edges, e => e.From == "M:U.OrderController.Call" && e.Kind == "call" && e.To == "M:U.Repo.Save(System.String)");

    [TestMethod] // Over(int) or Over(string): the argument's type would decide, and it is unknown
    public void Call_with_several_candidate_overloads_is_not_guessed()
        => TestAssert.DoesNotContain(Results.Edges, e => e.From == "M:U.OrderController.Ambiguous");

    [TestMethod]
    public void Mediator_Send_of_a_request_built_from_an_unresolved_argument_reaches_its_handler()
    {
        var edge = TestAssert.Single(Results.Edges, e => e.From == "M:U.OrderController.MyOrders" && e.Via == "mediatr");
        Assert.AreEqual("M:U.GetMyOrdersHandler.Handle(U.GetMyOrders,System.Threading.CancellationToken)", edge.To);
    }
}
