using MediatR;

namespace OrderingApp.Ordering;

public sealed record CreateOrderCommand(int Quantity) : IRequest<int>;
