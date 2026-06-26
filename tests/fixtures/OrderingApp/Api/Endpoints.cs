using Microsoft.AspNetCore.SignalR;

namespace OrderingApp.Api;

// Minimal-API, gRPC, and SignalR endpoints (R5: endpoint wiring). Mapped in Program.MapEndpoints.
public static class HealthEndpoints
{
    public static string Check() => "healthy";
}

public sealed class GreeterService
{
    public string Greet(string name) => $"Hello {name}";
}

public sealed class ChatHub : Hub
{
}
