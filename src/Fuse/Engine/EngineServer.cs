using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Fuse.Check;
using Fuse.Failures;
using Fuse.Paths;
using Fuse.Protocol;
using Fuse.Telemetry;

namespace Fuse.Engine;

/// <summary>
///     The engine process: one per repository root, guarded by a named mutex, serving one request per pipe
///     connection. It returns its free memory once idle for <see cref="IdleClock.ReleaseAfter"/>, and exits after
///     <see cref="IdleClock.ExitAfter"/> without requests, when a client of another build connects, or when the
///     repository disappears.
/// </summary>
internal static class EngineServer
{
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
        using var copy = HoldEngineCopy(log);
        _ = Task.Run(() => CleanUpLocalState(root, log));
        using var shutdown = new CancellationTokenSource();
        using var router = new RequestRouter(root, log);
        _ = router.InitializeAsync(shutdown.Token);

        var state = new IdleClock(TimeProvider.System);
        var watchdog = WatchIdleAsync(root, router, state, shutdown, log);
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

                _ = ServeAsync(pipe, router, state, shutdown, log);
            }
        }
        finally
        {
            await shutdown.CancelAsync().ConfigureAwait(false);
            await watchdog.ConfigureAwait(false);
            // The router is disposed when this method returns; a background load must stop before its workspace goes.
            await router.WaitForPreloadAsync().ConfigureAwait(false);
            log.Write("engine stopped");
        }

        return 0;
    }

    /// <summary>
    ///     Holds the engine copy this engine runs from, so another engine's cleanup keeps it. Null when the lock file
    ///     cannot be written, which only lets a cleanup remove the copy once it is old.
    /// </summary>
    private static IDisposable? HoldEngineCopy(EngineLog log)
    {
        try
        {
            return LocalState.HoldEngineCopy(AppContext.BaseDirectory);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.Write($"could not mark the engine copy in use: {e.Message}");
            return null;
        }
    }

    /// <summary>Records this repository's root and removes the engine copies, the stored baselines and the state directories nothing uses.</summary>
    private static void CleanUpLocalState(RepoRoot root, EngineLog log)
    {
        try
        {
            LocalState.RecordRoot(root);
            BaselineStore.Prune(root.StateDirectory);
            LocalState.RemoveUnusedEngineCopies(LocalState.EngineCopies, AppContext.BaseDirectory, DateTime.UtcNow);
            var removed = LocalState.RemoveStateOfMissingRepositories(LocalState.Repositories, DateTime.UtcNow);
            if (removed > 0)
                log.Write($"removed the state of {removed} repositories that no longer exist");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.Write($"cleaning up local state failed: {e.Message}");
        }
    }

    private static async Task ServeAsync(NamedPipeServerStream pipe, RequestRouter router, IdleClock state, CancellationTokenSource shutdown, EngineLog log)
    {
        await using (pipe.ConfigureAwait(false))
        {
            try
            {
                var line = await PipeFraming.ReadLineAsync(pipe, shutdown.Token).ConfigureAwait(false);
                if (line is null)
                    return;
                state.Touch();
                // The build id is read before the request's case, which a client of another build may name differently or
                // not at all, so every such client gets Restart.
                var buildId = ProtocolJson.ReadBuildId(line);
                if (buildId != EngineVersion.Build)
                {
                    // A request of 5.0.0 names its build id otherwise, so none is read from it.
                    log.Write($"client build {buildId ?? "(none)"} differs; exiting so the client can start a matching engine");
                    await WriteAsync(pipe, new EngineResponse.Restart(), CancellationToken.None).ConfigureAwait(false);
                    await shutdown.CancelAsync().ConfigureAwait(false);
                    return;
                }

                var request = ProtocolJson.ReadRequest(line);
                if (request is null)
                    return;

                state.Begin();
                try
                {
                    using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
                    // The client sends nothing after its request, so a completed read means it disconnected: stop the work.
                    var disconnect = WatchDisconnectAsync(pipe, requestCancellation);
                    EngineResponse response;
                    try
                    {
                        response = await router.HandleAsync(request, requestCancellation.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        response = new EngineResponse.Unanswered(ErrorCode.Timeout, "the request is cancelled");
                    }

                    if (!requestCancellation.IsCancellationRequested)
                        await WriteAsync(pipe, response, requestCancellation.Token).ConfigureAwait(false);

                    // Let the client read the answer and close its end first: on Unix, where the pipe is a socket,
                    // cancelling the pending read can reset the connection before the client has read the answer.
                    await Task.WhenAny(disconnect, Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None)).ConfigureAwait(false);
                    if (request is EngineRequest.ShutDown)
                        await shutdown.CancelAsync().ConfigureAwait(false);
                    await requestCancellation.CancelAsync().ConfigureAwait(false);
                    await disconnect.ConfigureAwait(false);
                }
                finally
                {
                    state.End();
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

    private static async Task WatchIdleAsync(RepoRoot root, RequestRouter router, IdleClock state, CancellationTokenSource shutdown, EngineLog log)
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
                else if (state.ShouldExit)
                {
                    log.Write("idle; exiting");
                    await shutdown.CancelAsync().ConfigureAwait(false);
                }
                else if (!router.IsLoading && state.TakeRelease())
                {
                    ReleaseMemory(log);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    ///     Collects and compacts the whole heap and returns the free memory to the operating system. A check on a large
    ///     repository allocates gigabytes that the background collector frees without returning, so an idle engine would
    ///     otherwise keep the size it peaked at.
    /// </summary>
    private static void ReleaseMemory(EngineLog log)
    {
        var before = Environment.WorkingSet;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        log.Write($"released memory: working set {before / (1024 * 1024)} MB before, {Environment.WorkingSet / (1024 * 1024)} MB after");
    }

    private static async Task WriteAsync(Stream stream, EngineResponse response, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(ProtocolJson.Serialize(response) + "\n");
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    [DllImport("libc")]
    private static extern int setsid();
}
