using SampleShop.Core.Payment;

namespace SampleShop.Core.Services;

public class PaymentService
{
    private readonly PaymentGateway _gateway = new();

    public void ProcessPayment(decimal amount)
    {
        _gateway.Charge("default", amount);
    }
}
