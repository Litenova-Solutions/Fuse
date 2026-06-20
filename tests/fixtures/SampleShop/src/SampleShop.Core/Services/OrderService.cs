namespace SampleShop.Core.Services;

public class OrderService
{
    private readonly PaymentService _payments = new();

    public void PlaceOrder(int orderId)
    {
        _payments.ProcessPayment(99.99m);
    }
}
