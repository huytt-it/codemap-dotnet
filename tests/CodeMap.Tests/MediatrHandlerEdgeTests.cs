using CodeMap.Roslyn.Scan;

namespace CodeMap.Tests;

/// <summary>
/// mediator.Send/.Publish -> Handle must reach EVERY handler: a notification usually has several
/// INotificationHandler&lt;T&gt;, and one class may handle several requests. Before, the pass kept only the first
/// handler per request type, and a handler class mapped only the first request type it handled, to the first
/// method named Handle.
/// </summary>
[TestClass]
public class MediatrHandlerEdgeTests
{
    // MediatR's shapes declared in source, as the SampleSolution fixture does: resolvable without the package.
    private const string Source = """
        namespace M;

        public interface IRequest<T> { }
        public interface INotification { }
        public interface IRequestHandler<TRequest, TResponse> where TRequest : IRequest<TResponse> { TResponse Handle(TRequest request); }
        public interface INotificationHandler<TNotification> where TNotification : INotification { void Handle(TNotification notification); }
        public interface IMediator
        {
            T Send<T>(IRequest<T> request);
            void Publish(INotification notification);
        }

        public class OrderPlaced : INotification { }
        public class SendEmail : INotificationHandler<OrderPlaced> { public void Handle(OrderPlaced n) { } }
        public class UpdateStock : INotificationHandler<OrderPlaced> { public void Handle(OrderPlaced n) { } }

        public class GetA : IRequest<int> { }
        public class GetB : IRequest<string> { }
        public class AbHandler : IRequestHandler<GetA, int>, IRequestHandler<GetB, string>
        {
            public int Handle(GetA request) => 1;
            public string Handle(GetB request) => "";
        }

        public class Caller
        {
            public void Place(IMediator m) => m.Publish(new OrderPlaced());
            public void AskA(IMediator m) => m.Send(new GetA());
            public void AskB(IMediator m) => m.Send(new GetB());
        }
        """;

    private static readonly ScanResults Results = InMemoryScan.Run(Source);

    [TestMethod]
    public void Publish_reaches_every_notification_handler()
    {
        var targets = Results.Edges.Where(e => e.From == "M:M.Caller.Place(M.IMediator)" && e.Via == "mediatr").Select(e => e.To).ToList();
        CollectionAssert.AreEquivalent(new[] { "M:M.SendEmail.Handle(M.OrderPlaced)", "M:M.UpdateStock.Handle(M.OrderPlaced)" }, targets);
    }

    [DataTestMethod]
    [DataRow("M:M.Caller.AskA(M.IMediator)", "M:M.AbHandler.Handle(M.GetA)")]
    [DataRow("M:M.Caller.AskB(M.IMediator)", "M:M.AbHandler.Handle(M.GetB)")]
    public void Each_request_of_a_multi_request_handler_reaches_its_own_Handle(string from, string handle)
    {
        var edge = TestAssert.Single(Results.Edges, e => e.From == from && e.Via == "mediatr");
        Assert.AreEqual(handle, edge.To);
    }

    [TestMethod]
    public void Every_Handle_of_a_multi_request_handler_is_an_entry_point()
    {
        var handlers = Results.EntryPoints.Where(e => e.Type == "handler").Select(e => e.Id).ToList();
        CollectionAssert.AreEquivalent(
            new[] { "M:M.SendEmail.Handle(M.OrderPlaced)", "M:M.UpdateStock.Handle(M.OrderPlaced)", "M:M.AbHandler.Handle(M.GetA)", "M:M.AbHandler.Handle(M.GetB)" },
            handlers);
    }
}
