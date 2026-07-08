using System.IO.Pipelines;
using System.IO.Pipes;
using System.Net.Sockets;
using DotMake.CommandLine;
using Fuse.Cli.Extensions;
using Fuse.Cli.Rpc;
using Fuse.Cli.Services;
using Fuse.Semantics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fuse.Cli.Commands;

/// <summary>
///     Runs the Fuse host: a long-lived process that serves the warm engine to the VS Code extension over a
///     JSON-RPC endpoint (a named pipe on Windows, a Unix domain socket elsewhere), sharing the same
///     <c>AddFuse</c> dependency graph the MCP server uses. This is the UI twin of <c>fuse mcp serve</c>: the
///     agent reads the warm engine over MCP, the developer reads the same warm engine over this transport.
/// </summary>
/// <remarks>
///     The endpoint address is derived from the repository root (see <see cref="HostEndpoint" />), so a second
///     editor window for the same repository connects to the already-running host and two repositories stay on
///     distinct endpoints. Logging goes to stderr, never the transport. The host serves connections until a
///     client calls <c>fuse/shutdown</c> or the process is cancelled. At start the host generates a random
///     session token and returns it from <c>fuse/handshake</c>; every other RPC method requires that token.
/// </remarks>
[CliCommand(
    Name = "host",
    Description = "Run the Fuse host: serve the warm engine to the VS Code extension over a named pipe or Unix socket.",
    Parent = typeof(FuseCliCommand))]
public sealed class HostCommand
{
    /// <summary>The repository root to serve; defaults to the current directory.</summary>
    [CliOption(Description = "Repository root to serve. Defaults to the current directory.")]
    public string Directory { get; set; } = System.IO.Directory.GetCurrentDirectory();

    /// <summary>
    ///     Builds the host services and serves the JSON-RPC endpoint until shutdown or cancellation.
    /// </summary>
    /// <param name="context">The CLI invocation context supplying the cancellation token.</param>
    /// <returns>A task that completes when the host stops serving.</returns>
    public async Task RunAsync(CliContext context)
    {
        var root = Path.GetFullPath(Directory);

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        builder.Services.AddSingleton<IConsoleUI, StderrConsoleUI>();
        builder.Services.AddFuse();
        builder.Services.AddSingleton<FuseHostService>();

        using var app = builder.Build();
        var service = app.Services.GetRequiredService<FuseHostService>();
        var logger = app.Services.GetRequiredService<ILogger<HostCommand>>();
        var notifier = new HostNotifier();

        // Stop accepting when the client asks the host to shut down or the process is cancelled.
        using var stopCts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        _ = service.ShutdownRequested.ContinueWith(_ => stopCts.Cancel(), TaskScheduler.Default);

        // Watch the source tree and push a fuse/invalidated notification to every connected editor when files
        // change, so the extension refreshes its index, diagnostics, and graph without polling. The .fuse cache
        // directory is ignored by the watcher, so the host's own writes do not retrigger.
        using var watcher = new Services.DebouncedFileWatcher(root, recursive: true, cancellationToken: stopCts.Token);
        watcher.Changed += async _ =>
        {
            logger.LogInformation("Workspace changed; broadcasting fuse/invalidated to {Count} clients.", notifier.ConnectionCount);
            await notifier.BroadcastAsync("fuse/invalidated");
        };

        // Resident workspace (S1), opt-in (FUSE_RESIDENT), default off: keep a live compilation for this root and
        // drive it from the same watcher's coalesced batches, so fuse_check answers resident-grade without a
        // rebuild. Off by default keeps the host path byte-identical until the S1 latency gate promotes it.
        using var resident = Services.ResidentWorkspaceHosting.OptIn()
            ? Services.ResidentWorkspaceHosting.Enable(root, watcher, app.Services.GetRequiredService<SemanticIndexer>(), message => logger.LogInformation("{Message}", message), stopCts.Token)
            : null;

        logger.LogInformation("Fuse host {Version} serving {Root}", FuseHostService.HostVersion, root);

        try
        {
            if (OperatingSystem.IsWindows())
                await ServeNamedPipeAsync(HostEndpoint.PipeName(root), service, notifier, logger, stopCts.Token);
            else
                await ServeUnixSocketAsync(HostEndpoint.SocketPath(root), service, notifier, logger, stopCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path.
        }

        logger.LogInformation("Fuse host stopped.");
    }

    // Accept loop for Windows named pipes. Each accepted connection is served on its own task so a second editor
    // window can connect while the first is active; the loop keeps offering new pipe instances until cancelled.
    private static async Task ServeNamedPipeAsync(
        string pipeName, FuseHostService service, HostNotifier notifier, ILogger logger, CancellationToken cancellationToken)
    {
        var connections = new List<Task>();
        while (!cancellationToken.IsCancellationRequested)
        {
            var server = new NamedPipeServerStream(
                pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);

            try
            {
                await server.WaitForConnectionAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                await server.DisposeAsync();
                break;
            }

            connections.Add(ServeConnectionAsync(server, server, service, notifier, logger, cancellationToken));
            connections.RemoveAll(t => t.IsCompleted);
        }

        await Task.WhenAll(connections);
    }

    // Accept loop for Unix domain sockets, mirroring the named-pipe loop.
    private static async Task ServeUnixSocketAsync(
        string socketPath, FuseHostService service, HostNotifier notifier, ILogger logger, CancellationToken cancellationToken)
    {
        if (File.Exists(socketPath))
            File.Delete(socketPath); // A stale socket file from a crashed host would block the bind.

        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        listener.Listen(backlog: 16);

        var connections = new List<Task>();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                Socket accepted;
                try
                {
                    accepted = await listener.AcceptAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                var stream = new NetworkStream(accepted, ownsSocket: true);
                connections.Add(ServeConnectionAsync(stream, stream, service, notifier, logger, cancellationToken));
                connections.RemoveAll(t => t.IsCompleted);
            }

            await Task.WhenAll(connections);
        }
        finally
        {
            try { File.Delete(socketPath); } catch (IOException) { /* best effort cleanup */ }
        }
    }

    // Serves one connection's RPC traffic until the client disconnects or the host is cancelled, then disposes
    // the transport. A faulted connection is logged and swallowed so one client cannot take the host down.
    private static async Task ServeConnectionAsync(
        Stream readStream, Stream writeStream, FuseHostService service, HostNotifier notifier, ILogger logger, CancellationToken cancellationToken)
    {
        await using var disposable = readStream as IAsyncDisposable ?? new NoopAsyncDisposable();
        try
        {
            using var rpc = FuseHostConnection.Attach(
                PipeReader.Create(readStream), PipeWriter.Create(writeStream), service, notifier);
            await rpc.Completion.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Host shutting down.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Host connection ended with an error.");
        }
    }

    private sealed class NoopAsyncDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
