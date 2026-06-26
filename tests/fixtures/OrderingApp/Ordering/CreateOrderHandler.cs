using System.Threading;
using System.Threading.Tasks;
using MediatR;

namespace OrderingApp.Ordering;

// Handles CreateOrderCommand (the mediatr_handles edge) and injects IOrderService (a di_injects edge).
public sealed class CreateOrderHandler : IRequestHandler<CreateOrderCommand, int>
{
    private readonly IOrderService _orders;

    public CreateOrderHandler(IOrderService orders) => _orders = orders;

    public Task<int> Handle(CreateOrderCommand request, CancellationToken cancellationToken) =>
        Task.FromResult(_orders.Create(request.Quantity));
}
