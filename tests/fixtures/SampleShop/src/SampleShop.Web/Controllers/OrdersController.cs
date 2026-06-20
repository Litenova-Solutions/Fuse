using Microsoft.AspNetCore.Mvc;
using SampleShop.Core.Services;

namespace SampleShop.Web.Controllers;

[Route("api/[controller]")]
public class OrdersController : ControllerBase
{
    private readonly OrderService _orders = new();

    [HttpGet("{id}")]
    public IActionResult Get(int id) => Ok(_orders);

    [HttpPost]
    public IActionResult Create() => Ok();
}
