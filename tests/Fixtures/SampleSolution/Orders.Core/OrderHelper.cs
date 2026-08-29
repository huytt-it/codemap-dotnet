using System;
using Orders.Data;

namespace Orders;

public static class OrderHelper
{
    public static void EnsureExists(OrderRepository repository, int orderId)
    {
        if (!repository.Exists(orderId))
            throw new InvalidOperationException("Order not found");
    }
}
