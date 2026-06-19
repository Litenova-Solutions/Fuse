namespace SampleShop.Core.Payment;

public class PaymentGateway
{
    public bool Charge(string cardId, decimal amount) => amount > 0;
}
