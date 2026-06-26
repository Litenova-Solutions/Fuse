using System.Threading.Tasks;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using OrderingApp.Ordering;

namespace OrderingApp.Api;

// Injects IOrderService and ISender (di_injects edges). The Create action is the handler for
// POST api/orders/{id} (the route_handles edge) and sends CreateOrderCommand (a sends_request edge).
[ApiController]
[Route("api/orders")]
public sealed class OrdersController : ControllerBase
{
    private readonly IOrderService _orders;
    private readonly ISender _sender;

    public OrdersController(IOrderService orders, ISender sender)
    {
        _orders = orders;
        _sender = sender;
    }

    [HttpPost("{id}")]
    public async Task<IActionResult> Create(int id)
    {
        await _sender.Send(new CreateOrderCommand(id));
        return Ok(_orders.Create(id));
    }
}
