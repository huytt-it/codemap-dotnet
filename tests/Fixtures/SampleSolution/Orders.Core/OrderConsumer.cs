namespace Orders;

/// <summary>Fixture for L2: calls Cancel through the IOrderService interface, not a concrete type — exercises the
/// expand-via-interface pass (spec section 5, "phần quan trọng nhất"), which must duplicate this call edge onto
/// both OrderService.Cancel and FakeOrderService.Cancel, marked via:"interface".</summary>
public class OrderConsumer
{
    private readonly IOrderService _service;

    public OrderConsumer(IOrderService service)
    {
        _service = service;
    }

    public bool LastCancelSucceeded { get; set; }

    public void CancelOrder(int id)
    {
        _service.Cancel(id);
        // Explicit `this.` qualification on purpose: the spec's read/write rule is scoped to
        // MemberAccessExpressionSyntax specifically, so a bare `LastCancelSucceeded = true` (IdentifierNameSyntax,
        // implicit this) would NOT produce an edge — a known, spec-literal scope limitation.
        this.LastCancelSucceeded = true; // write
        var wasOk = this.LastCancelSucceeded; // read
    }
}
