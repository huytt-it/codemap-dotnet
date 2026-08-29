namespace Orders;

/// <summary>Second implementation of IOrderService — used to test interface expansion (fixture, phase 2+).</summary>
public class FakeOrderService : IOrderService
{
    public void Cancel(int orderId)
    {
    }
}
