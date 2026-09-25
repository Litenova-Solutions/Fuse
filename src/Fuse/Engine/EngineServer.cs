using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Fuse.Protocol;
using Fuse.Repo;

namespace Fuse.Engine;

/// <summary>
///     The engine process: one per repository root, guarded by a named mutex, serving one request per pipe
///     connection. It exits after <see cref="IdleTimeout"/> without requests, when a client of a different version
///     connects, or when the repository disappears.
/// </summary>
internal static class EngineServer
{
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);

    public static async Task<int> RunAsync(string rootPath)
    {
        var root = RepoRoot.Find(rootPath);
        if (root is null)
            return 1;
        if (!OperatingSystem.IsWindows())
            _ = setsid();
        Directory.SetCurrentDirectory(root.Path);

        using var mutex = new Mutex(false, "fuse-engine-" + root.PipeName);
        try
        {
            if (!mutex.WaitOne(0))
                return 0;
        }
        catch (AbandonedMutexException)
        {
            // An engine that exited without releasing the mutex hands it to this process.
        }

        var log = new EngineLog(root.StateDirectory);
        log.Write($"engine {EngineVersion.Build} started for {root.Path} (pid {Environment.ProcessId})");
        using var shutdown = new CancellationTokenSource();
        using var host = new EngineHost(root, log);
        _ = host.InitializeAsync(shutdown.Token);

        var state = new ServerState();
        var watchdog = WatchIdleAsync(root, state, shutdown, log);
        try
        {
            while (!shutdown.IsCancellationRequested)
            {
                var pipe = new NamedPipeServerStream(
                    root.PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try
                {
                    await pipe.WaitForConnectionAsync(shutdown.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                    break;
                }
                catch (IOException e)
                {
                    log.Write($"accept failed: {e.Message}");
                    await pipe.DisposeAsync().ConfigureAwait(false);
                    continue;
                }

                _ = ServeAsync(pipe, host, state, shutdown, log);
            }
        }
        finally
        {
            await shutdown.CancelAsync().ConfigureAwait(false);
            await watchdog.ConfigureAwait(false);
            log.Write("engine stopped");
        }

        return 0;
    }

    private static async Task ServeAsync(NamedPipeServerStream pipe, EngineHost host, ServerState state, CancellationTokenSource shutdown, EngineLog log)
    {
        await using (pipe.ConfigureAwait(false))
        {
            try
            {
                var line = await ReadLineAsync(pipe, shutdown.Token).ConfigureAwait(false);
                var request = line is null ? null : ProtocolJson.ReadRequest(line);
                if (request is null)
                    return;
                state.Touch();
                if (request.Version != EngineVersion.Build)
                {
                    log.Write($"client version {request.Version} differs; exiting so the client can start a matching engine");
                    await WriteAsync(pipe, new EngineResponse(ResponseStatus.Restart), CancellationToken.None).ConfigureAwait(false);
                    await shutdown.CancelAsync().ConfigureAwait(false);
                    return;
                }

                Interlocked.Increment(ref state.Active);
                try
                {
                    using var request_ = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
                    // The client sends nothing after its request, so a completed read means it disconnected: stop the work.
                    var disconnect = WatchDisconnectAsync(pipe, request_);
                    EngineResponse response;
                    try
                    {
                        response = await host.HandleAsync(request, request_.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        response = EngineResponse.Fail(ErrorCode.Timeout, "the request is cancelled");
                    }

                    if (!request_.IsCancellationRequested)
                        await WriteAsync(pipe, response, request_.Token).ConfigureAwait(false);
                    if (request.Kind == RequestKind.Shutdown)
                        await shutdown.CancelAsync().ConfigureAwait(false);
                    await request_.CancelAsync().ConfigureAwait(false);
                    await disconnect.ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Decrement(ref state.Active);
                    state.Touch();
                }
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // The client went away; nothing to answer.
            }
            catch (Exception e)
            {
                log.Write($"request failed: {e}");
            }
        }
    }

    private static async Task WatchDisconnectAsync(Stream pipe, CancellationTokenSource request)
    {
        var buffer = new byte[1];
        try
        {
            var read = await pipe.ReadAsync(buffer, request.Token).ConfigureAwait(false);
            if (read == 0)
                await request.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
        {
            if (!request.IsCancellationRequested)
                await request.CancelAsync().ConfigureAwait(false);
        }
    }

    private static async Task WatchIdleAsync(RepoRoot root, ServerState state, CancellationTokenSource shutdown, EngineLog log)
    {
        try
        {
            while (!shutdown.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(15), shutdown.Token).ConfigureAwait(false);
                if (!Directory.Exists(root.Path))
                {
                    log.Write("repository removed; exiting");
                    await shutdown.CancelAsync().ConfigureAwait(false);
                }
                else if (Volatile.Read(ref state.Active) == 0 && state.IdleFor > IdleTimeout)
                {
                    log.Write("idle; exiting");
                    await shutdown.CancelAsync().ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    ///     Reads one newline-terminated UTF-8 line in 4 KB chunks. Each connection carries exactly one message in each
    ///     direction, so anything after the newline cannot exist and nothing is lost by reading ahead.
    /// </summary>
    internal static async Task<string?> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var line = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return line.Length == 0 ? null : Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length);
            var newline = Array.IndexOf(buffer, (byte)'\n', 0, read);
            if (newline >= 0)
            {
                line.Write(buffer, 0, newline);
                return Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length);
            }

            line.Write(buffer, 0, read);
        }
    }

    private static async Task WriteAsync(Stream stream, EngineResponse response, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(ProtocolJson.Serialize(response) + "\n");
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    [DllImport("libc")]
    private static extern int setsid();

    private sealed class ServerState
    {
        public int Active;
        private long _lastRequest = Environment.TickCount64;

        public TimeSpan IdleFor => TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref _lastRequest));

        public void Touch() => Interlocked.Exchange(ref _lastRequest, Environment.TickCount64);
    }
}
