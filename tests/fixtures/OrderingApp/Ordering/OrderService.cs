using Microsoft.Extensions.Options;

namespace OrderingApp.Ordering;

// Consumes OrderOptions via IOptions<T> constructor injection (the options_consumes edge), and is the
// registered implementation of IOrderService (the di_resolves_to edge from Program.ConfigureServices).
public sealed class OrderService : IOrderService
{
    private readonly OrderOptions _options;

    public OrderService(IOptions<OrderOptions> options) => _options = options.Value;

    public int Create(int quantity) => quantity > _options.MaxItems ? _options.MaxItems : quantity;
}
