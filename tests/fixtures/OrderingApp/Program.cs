using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OrderingApp.Api;
using OrderingApp.Ordering;

namespace OrderingApp;

// The composition root. Registers IOrderService -> OrderService (di_registers + di_resolves_to) and binds
// OrderOptions to the "Orders" configuration section (options_binds). Also exercises the wider wiring kinds:
// a factory registration, a decorator, a hosted service, and minimal-API/gRPC/SignalR endpoints.
public static class Program
{
    public static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IOrderService, OrderService>();
        services.Configure<OrderOptions>(configuration.GetSection("Orders"));
        services.AddHostedService<OrderDispatcher>();
        // Factory registration: the lambda builds the concrete IClock implementation.
        services.AddSingleton<IClock>(sp => new SystemClock());
        // Decoration: wrap IOrderService with a logging decorator.
        services.Decorate<IOrderService, OrderServiceLogger>();
    }

    public static void MapEndpoints(WebApplication app)
    {
        app.MapGet("/health", HealthEndpoints.Check);
        app.MapGrpcService<GreeterService>();
        app.MapHub<ChatHub>("/chat");
    }
}
