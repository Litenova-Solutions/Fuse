using System.IO.Pipelines;
using Fuse.Cli.Rpc;
using Fuse.Collection.Templates;
using Fuse.Fusion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using StreamJsonRpc;

namespace Fuse.Cli.Tests;

// Drives the host service over a real JSON-RPC connection (an in-memory duplex pipe pair, the same formatter and
// header framing the named-pipe transport uses) to validate the wire wiring end to end: a client calls the
// fuse/* methods by name and gets back deserialized DTOs, and fuse/shutdown completes the host's shutdown task.
public sealed class FuseHostServiceRpcTests : IDisposable
{
    private readonly ServiceProvider _provider = new ServiceCollection().AddFuseForTests().BuildServiceProvider();

    private FuseHostService NewService() => new(
        _provider.GetRequiredService<FusionOrchestrator>(),
        _provider.GetRequiredService<ProjectTemplateRegistry>(),
        NullLogger<FuseHostService>.Instance);

    [Fact]
    public async Task Client_CallsHandshakeStatsAndShutdown_OverTheWire()
    {
        // Two pipes form a full-duplex transport: client writes to clientToServer, server reads it, and vice versa.
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var service = NewService();
        using var serverRpc = FuseHostConnection.Attach(clientToServer.Reader, serverToClient.Writer, service);

        var clientFormatter = new SystemTextJsonFormatter();
        clientFormatter.JsonSerializerOptions.TypeInfoResolverChain.Insert(0, FuseHostJsonContext.Default);
        using var clientRpc = new JsonRpc(
            new HeaderDelimitedMessageHandler(clientToServer.Writer, serverToClient.Reader, clientFormatter));
        clientRpc.StartListening();

        var handshake = await clientRpc.InvokeAsync<FuseHostHandshake>("fuse/handshake");
        Assert.Equal(FuseHostService.ProtocolVersion, handshake.ProtocolVersion);
        Assert.False(string.IsNullOrWhiteSpace(handshake.HostVersion));

        var stats = await clientRpc.InvokeAsync<FuseHostStats>("fuse/stats");
        Assert.Equal(Environment.ProcessId, stats.ProcessId);
        Assert.True(stats.WorkingSetBytes > 0);

        Assert.False(service.ShutdownRequested.IsCompleted);
        await clientRpc.NotifyAsync("fuse/shutdown");
        await service.ShutdownRequested.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(service.ShutdownRequested.IsCompleted);
    }

    [Fact]
    public async Task Index_WarmsTheEngineAndCountsFiles()
    {
        var source = Path.Combine(Path.GetTempPath(), "fuse-host-index", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "Widget.cs"), "public class Widget { public void Run() { } }");
        File.WriteAllText(Path.Combine(source, "Gadget.cs"), "public class Gadget { public void Go() { } }");

        try
        {
            var result = await NewService().IndexAsync(source);

            Assert.Equal("Warm", result.IndexState);
            Assert.True(result.FileCount >= 2, $"expected at least 2 files, got {result.FileCount}");
        }
        finally
        {
            Directory.Delete(source, recursive: true);
        }
    }

    [Fact]
    public async Task Index_MissingDirectory_ReportsNotIndexed()
    {
        var missing = Path.Combine(Path.GetTempPath(), "fuse-host-missing", Guid.NewGuid().ToString("N"));

        var result = await NewService().IndexAsync(missing);

        Assert.Equal("NotIndexed", result.IndexState);
        Assert.Equal(0, result.FileCount);
    }

    public void Dispose() => _provider.Dispose();
}
