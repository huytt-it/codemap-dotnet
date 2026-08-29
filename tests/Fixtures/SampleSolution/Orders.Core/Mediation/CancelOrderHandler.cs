using Orders;

namespace Orders.Mediation;

/// <summary>Fixture for the `handler` entry point kind + the MediatR virtual-edge pass (spec section 9's fixture list: "một MediatR handler").</summary>
public class CancelOrderCommand : IRequest<bool>
{
    public int OrderId { get; set; }
}

public class CancelOrderHandler : IRequestHandler<CancelOrderCommand, bool>
{
    private readonly IOrderService _orderService;

    public CancelOrderHandler(IOrderService orderService)
    {
        _orderService = orderService;
    }

    public bool Handle(CancelOrderCommand request)
    {
        _orderService.Cancel(request.OrderId);
        return true;
    }
}
