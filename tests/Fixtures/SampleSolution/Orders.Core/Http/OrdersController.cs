using Orders;
using Orders.Mediation;

namespace Orders.Http;

/// <summary>Fixture for the `http` entry point kind (spec section 5 & section 9's fixture list: "route chứa tham số"). Completes the 4-tier chain: Controller → Service → Helper → Repository.</summary>
[Route("api/[controller]")]
public class OrdersController : ControllerBase
{
    private readonly IOrderService _orderService;

    public OrdersController(IOrderService orderService)
    {
        _orderService = orderService;
    }

    [HttpDelete("{id}")]
    public void Delete(int id)
    {
        _orderService.Cancel(id);
    }

    /// <summary>Fixture for MediatR's virtual `via:"mediatr"` call edge — inline `new` argument to .Send(), the pattern TryRecordMediatrSend matches.</summary>
    [HttpPost("cancel-via-mediator")]
    public void CancelViaMediator(IMediator mediator, int id)
    {
        mediator.Send(new CancelOrderCommand { OrderId = id });
    }
}
