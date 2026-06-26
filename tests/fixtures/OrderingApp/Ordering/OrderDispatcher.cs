using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace OrderingApp.Ordering;

// A background worker (R5: hosted-service wiring). Implements IHostedService through BackgroundService.
public sealed class OrderDispatcher : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
}
