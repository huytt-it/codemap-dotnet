using System;
using Orders.Data;

namespace Orders;

public class OrderService : IOrderService
{
    private readonly OrderRepository _repository = new();

    public void Cancel(int orderId)
    {
        OrderHelper.EnsureExists(_repository, orderId);
        _repository.Delete(orderId);
    }

    [Obsolete]
    public void CancelBatch(int[] orderIds)
    {
        foreach (var id in orderIds)
            Cancel(id);
    }
}
