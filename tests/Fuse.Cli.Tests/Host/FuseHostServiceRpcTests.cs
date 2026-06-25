using System.IO.Pipelines;
using Fuse.Cli.Rpc;
using Fuse.Collection;
using Fuse.Collection.FileSystem;
using Fuse.Collection.Templates;
using Fuse.Fusion;
using Fuse.Fusion.Scoping;
using Fuse.Plugins.Abstractions;
using Fuse.Plugins.Abstractions.Dependencies;
using Fuse.Reduction.Security;
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
        _provider.GetRequiredService<FileCollectionPipeline>(),
        _provider.GetRequiredService<DependencyGraphBuilder>(),
        _provider.GetRequiredService<Func<ISourceContentProvider>>(),
        _provider.GetRequiredService<CapabilityRegistry<IDependencyExtractor>>(),
        _provider.GetRequiredService<CapabilityRegistry<ITypeNameLocator>>(),
        _provider.GetRequiredService<ISecretRedactor>(),
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

    [Fact]
    public async Task Scope_Search_EmitsTheMatchedFileAndWritesPayload()
    {
        var source = Path.Combine(Path.GetTempPath(), "fuse-host-scope", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "PaymentService.cs"),
            "public class PaymentService { public void ProcessPayment() { } }");
        File.WriteAllText(Path.Combine(source, "CatalogService.cs"),
            "public class CatalogService { public void ListProducts() { } }");

        try
        {
            var result = await NewService().ScopeAsync(source, "search", null, "process payment", null, 20000);

            Assert.Equal("search", result.Mode);
            Assert.NotEmpty(result.Files);
            Assert.Contains(result.Files, f => f.Path.Contains("PaymentService", StringComparison.Ordinal));
            Assert.NotNull(result.PayloadPath);
            Assert.True(File.Exists(result.PayloadPath));
            Assert.Contains("PaymentService", await File.ReadAllTextAsync(result.PayloadPath!));
        }
        finally
        {
            Directory.Delete(source, recursive: true);
        }
    }

    [Fact]
    public async Task Graph_Files_ReturnsNodesAndAReferenceEdge()
    {
        var source = Path.Combine(Path.GetTempPath(), "fuse-host-graph", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(source);
        // Consumer references Service's type, so the graph must carry a Consumer -> Service edge.
        File.WriteAllText(Path.Combine(source, "Service.cs"), "public class Service { public int Value() => 1; }");
        File.WriteAllText(Path.Combine(source, "Consumer.cs"),
            "public class Consumer { private Service _s = new Service(); public int Use() => _s.Value(); }");

        try
        {
            var graph = await NewService().GraphAsync(source, "Files");

            Assert.Equal("Files", graph.Detail);
            Assert.Contains(graph.Nodes, n => n.Path.Contains("Service", StringComparison.Ordinal));
            Assert.Contains(graph.Nodes, n => n.Path.Contains("Consumer", StringComparison.Ordinal));
            Assert.Contains(graph.Edges, e =>
                e.From.Contains("Consumer", StringComparison.Ordinal) && e.To.Contains("Service", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(source, recursive: true);
        }
    }

    [Fact]
    public async Task Graph_WithScopeOverlay_TagsMatchedNodesWithARole()
    {
        var source = Path.Combine(Path.GetTempPath(), "fuse-host-graphscope", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "PaymentService.cs"),
            "public class PaymentService { public void ProcessPayment() { } }");
        File.WriteAllText(Path.Combine(source, "CatalogService.cs"),
            "public class CatalogService { public void ListProducts() { } }");

        try
        {
            var graph = await NewService().GraphAsync(source, "Files", "search", null, "process payment", null);

            // The scope overlay tags the matched file's node with a role (the whole point of the overlay).
            var matched = Assert.Single(graph.Nodes, n => n.Path.Contains("PaymentService", StringComparison.Ordinal));
            Assert.False(string.IsNullOrWhiteSpace(matched.Role));
        }
        finally
        {
            Directory.Delete(source, recursive: true);
        }
    }

    [Fact]
    public async Task Graph_Directories_FoldsFilesIntoDirectorySupernodes()
    {
        var source = Path.Combine(Path.GetTempPath(), "fuse-host-graphdir", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(source, "Core"));
        Directory.CreateDirectory(Path.Combine(source, "App"));
        File.WriteAllText(Path.Combine(source, "Core", "Service.cs"), "public class Service { public int V() => 1; }");
        File.WriteAllText(Path.Combine(source, "App", "Consumer.cs"),
            "public class Consumer { private Service _s = new Service(); }");

        try
        {
            var graph = await NewService().GraphAsync(source, "Directories");

            Assert.Equal("Directories", graph.Detail);
            // Directory nodes, not file nodes: at most one node per directory, far fewer than the file count.
            Assert.Contains(graph.Nodes, n => n.Path is "Core" or "App");
            Assert.All(graph.Nodes, n => Assert.DoesNotContain(".cs", n.Path));
        }
        finally
        {
            Directory.Delete(source, recursive: true);
        }
    }

    [Fact]
    public async Task Diagnostics_ReportsASecretAtItsPreciseRange()
    {
        var source = Path.Combine(Path.GetTempPath(), "fuse-host-diag", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(source);
        // A second line carrying an AWS access key (a synthetic example), so the diagnostic must land on line 1.
        var secretLine = "    var key = \"AKIAIOSFODNN7EXAMPLE\";";
        File.WriteAllText(Path.Combine(source, "Config.cs"), "public class Config\n" + secretLine + "\n");
        // An isolated class with no inbound or outbound type reference must surface as a graph gap.
        File.WriteAllText(Path.Combine(source, "Orphan.cs"), "public class Orphan { public int N() => 1; }");

        try
        {
            var diagnostics = await NewService().DiagnosticsAsync(source);

            var finding = Assert.Single(diagnostics.Secrets);
            Assert.Equal("aws-access-key", finding.Kind);
            Assert.Contains("Config.cs", finding.Path);
            Assert.Equal(1, finding.StartLine); // zero-based: the secret is on the second line
            Assert.Equal(secretLine.IndexOf("AKIA", StringComparison.Ordinal), finding.StartColumn);

            Assert.NotEmpty(diagnostics.Hotspots);
            Assert.Contains(diagnostics.GraphGaps, g => g.Contains("Orphan", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(source, recursive: true);
        }
    }

    [Fact]
    public async Task Explain_Search_ReturnsAPlanWithSeedRolesAndTiers()
    {
        var source = Path.Combine(Path.GetTempPath(), "fuse-host-explain", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "PaymentService.cs"),
            "public class PaymentService { public void ProcessPayment() { } }");
        File.WriteAllText(Path.Combine(source, "CatalogService.cs"),
            "public class CatalogService { public void ListProducts() { } }");

        try
        {
            var explain = await NewService().ExplainAsync(source, "search", null, "process payment", null);

            Assert.Equal("search", explain.Mode);
            Assert.NotEmpty(explain.Files);
            // The plan names the matched file and assigns it a role and a tier (the explainer's whole purpose).
            var matched = Assert.Single(explain.Files, f => f.Path.Contains("PaymentService", StringComparison.Ordinal));
            Assert.False(string.IsNullOrWhiteSpace(matched.Role));
            Assert.False(string.IsNullOrWhiteSpace(matched.Tier));
        }
        finally
        {
            Directory.Delete(source, recursive: true);
        }
    }

    [Fact]
    public async Task ConcurrentGraphAndScope_AgainstOneRoot_BothSucceed()
    {
        // The playbook's concurrency check: simultaneous fuse/graph and fuse/scope against one root exercise the
        // shared orchestrator and pooled store under concurrent calls (C3 DI concurrency under the new transport).
        var source = Path.Combine(Path.GetTempPath(), "fuse-host-concurrent", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "Service.cs"), "public class Service { public int V() => 1; }");
        File.WriteAllText(Path.Combine(source, "Consumer.cs"),
            "public class Consumer { private Service _s = new Service(); public int U() => _s.V(); }");

        try
        {
            var service = NewService();
            // Fire several graph and scope calls at once on one root; none may throw and each must return data.
            var graphTasks = Enumerable.Range(0, 4).Select(_ => service.GraphAsync(source, "Files"));
            var scopeTasks = Enumerable.Range(0, 4).Select(_ => service.ScopeAsync(source, "search", null, "service", null, 20000));

            var graphs = await Task.WhenAll(graphTasks);
            var scopes = await Task.WhenAll(scopeTasks);

            Assert.All(graphs, g => Assert.NotEmpty(g.Nodes));
            Assert.All(scopes, s => Assert.Equal("search", s.Mode));
        }
        finally
        {
            Directory.Delete(source, recursive: true);
        }
    }

    public void Dispose() => _provider.Dispose();
}
