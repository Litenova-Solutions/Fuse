using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OrderingApp.Ordering;

namespace OrderingApp;

// The composition root. Registers IOrderService -> OrderService (di_registers + di_resolves_to) and binds
// OrderOptions to the "Orders" configuration section (options_binds).
public static class Program
{
    public static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IOrderService, OrderService>();
        services.Configure<OrderOptions>(configuration.GetSection("Orders"));
    }
}
