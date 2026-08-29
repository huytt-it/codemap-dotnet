namespace Orders.Data;

public class OrderRepository
{
    public bool Exists(int orderId)
    {
        return orderId > 0;
    }

    public void Delete(int orderId)
    {
    }
}
