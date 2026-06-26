namespace OrderingApp.Ordering;

// A decorator for IOrderService (R5: services.Decorate<IOrderService, OrderServiceLogger>()). Kept
// parameterless so the fixture's scored edge set stays exactly the decoration edge.
public sealed class OrderServiceLogger : IOrderService
{
    public int Create(int quantity) => quantity;
}
