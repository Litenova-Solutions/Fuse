using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using Fuse.Failures;
using Fuse.Paths;
using Fuse.Protocol.Analysis;
using Fuse.Protocol.Requests;
using Fuse.Protocol.Transport;
using Fuse.Repo.Tracking;
namespace Fuse.Client;

/// <summary>Sends one request to the repository's engine, starting the engine when none is running.</summary>
internal static class EngineClient
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(15);

    /// <summary>How long a pipe existence answer is kept, so a round trip that asks twice in quick succession enumerates the pipes once.</summary>
    private static readonly long PipeExistsCacheMs = 100;

    private static readonly ConcurrentDictionary<string, (bool Exists, long ExpiresAt)> PipeExistence = new(StringComparer.Ordinal);

    private static int _counter;

    /// <summary>
    ///     Names the request for the engine's log. The process id keeps two clients apart, the counter keeps two requests
    ///     from one client apart, so a line in <c>engine.log</c> can be matched to the call that produced it.
    /// </summary>
    public static string NextRequestId() => $"{Environment.ProcessId}-{Interlocked.Increment(ref _counter)}";

    /// <summary>Sends <paramref name="request"/> and waits for the answer.</summary>
    /// <param name="root">The repository whose engine to use.</param>
    /// <param name="request">The request. Its build id and request id are replaced with this build's and a fresh one, and its wait with <paramref name="timeout"/>.</param>
    /// <param name="timeout">How long to wait for the answer.</param>
    /// <param name="cancellationToken">Cancels the request; the engine stops the work when the connection closes.</param>
    public static async Task<EngineResponse> SendAsync(RepoRoot root, EngineRequest request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        request = Stamp(root, request, timeout);
        var sendStarted = Environment.TickCount64;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            var restarts = 0;
            for (var attempt = 0; ;)
            {
                string? line;
                try
                {
                    var pipe = await ConnectAsync(root, deadline.Token).ConfigureAwait(false);
                    await using var pipeLifetime = pipe.ConfigureAwait(false);
                    var bytes = Encoding.UTF8.GetBytes(ProtocolJson.Serialize(request) + "\n");
                    await pipe.WriteAsync(bytes, deadline.Token).ConfigureAwait(false);
                    await pipe.FlushAsync(deadline.Token).ConfigureAwait(false);
                    line = await PipeFraming.ReadLineAsync(pipe, deadline.Token).ConfigureAwait(false);
                }
                catch (IOException) when (attempt < 4)
                {
                    // On Linux and macOS each pipe server instance accepts one connection and closes its socket, so a
                    // connection that arrives in between is reset. Every request is safe to send again.
                    await Task.Delay(50 * (attempt + 1), deadline.Token).ConfigureAwait(false);
                    attempt++;
                    continue;
                }

                EngineResponse? response = null;
                if (line is not null && Read(line, out response) is { } reason)
                    return new EngineResponse.Unanswered(ErrorCode.Internal, $"the Fuse engine sent an answer this client cannot read ({reason}; see {Path.Combine(root.StateDirectory, "engine.log")})");
                if (response is null)
                {
                    if (attempt < 1)
                    {
                        attempt++;
                        continue;
                    }

                    return new EngineResponse.Unanswered(ErrorCode.Internal, $"the Fuse engine closed the connection (see {Path.Combine(root.StateDirectory, "engine.log")})");
                }

                if (response is EngineResponse.Busy busy)
                {
                    // The engine refused the request at once instead of queueing it past the wait. Send it again
                    // after the estimate, within this request's own wait; past the wait the answer is Busy, which
                    // the command line prints and a hook leaves silent, as it leaves every answer but a missing
                    // restore and introduced errors.
                    var remainingMs = timeout.TotalMilliseconds - (Environment.TickCount64 - sendStarted);
                    if (remainingMs <= 0)
                        return new EngineResponse.Unanswered(ErrorCode.Busy, ErrorMessages.Busy(busy.Position, busy.EstimatedWaitMs));
                    var delayMs = Math.Clamp(busy.EstimatedWaitMs, 50, 5000);
                    if (delayMs >= remainingMs)
                        return new EngineResponse.Unanswered(ErrorCode.Busy, ErrorMessages.Busy(busy.Position, busy.EstimatedWaitMs));
                    await Task.Delay(TimeSpan.FromMilliseconds(delayMs), deadline.Token).ConfigureAwait(false);
                    continue;
                }

                if (response is EngineResponse.Restart)
                {
                    // An engine from another build is draining; wait with backoff for the matching engine to take its
                    // place, within this request's own wait.
                    restarts++;
                    await Task.Delay(RestartDelay(restarts), deadline.Token).ConfigureAwait(false);
                    continue;
                }

                return response;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new EngineResponse.Unanswered(ErrorCode.Timeout, $"the Fuse engine did not answer within {timeout.TotalSeconds:0} s");
        }
        catch (FuseException e)
        {
            return new EngineResponse.Unanswered(e.Code, e.Message);
        }
        catch (IOException e)
        {
            return new EngineResponse.Unanswered(ErrorCode.Internal, $"could not connect to the Fuse engine: {e.Message}");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        {
            return new EngineResponse.Unanswered(ErrorCode.Internal, $"could not start the Fuse engine: {e.Message}");
        }
    }

    /// <summary>
    ///     How long to wait after the <paramref name="nth"/> Restart in a row before sending the request again. The drained
    ///     engine finishes its requests before it exits, so the wait grows from 300 ms to 5 s.
    /// </summary>
    private static TimeSpan RestartDelay(int nth) => nth switch
    {
        1 => TimeSpan.FromMilliseconds(300),
        2 => TimeSpan.FromMilliseconds(600),
        3 => TimeSpan.FromMilliseconds(1200),
        4 => TimeSpan.FromMilliseconds(2400),
        _ => TimeSpan.FromMilliseconds(5000),
    };

    /// <summary>
    ///     Reads <paramref name="line"/> into <paramref name="response"/> and returns why it is not a complete answer, or
    ///     null when it is one. A line that is not JSON, that was cut off, that names no case this build knows, or whose
    ///     answer leaves out what the client prints would otherwise end the command with an exception instead of exit
    ///     code 2.
    /// </summary>
    private static string? Read(string line, out EngineResponse? response)
    {
        response = null;
        try
        {
            response = ProtocolJson.ReadResponse(line);
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or NotSupportedException)
        {
            return e is NotSupportedException ? "the answer names no case" : "the answer is not complete JSON";
        }

        var complete = response switch
        {
            null => true,
            EngineResponse.CheckAnswered { Report: var report } => report is { Errors: not null, Projects: not null, DeclarationsChangedIn: not null }
                                                                    && report.Errors.All(e => e?.Error is not null),
            EngineResponse.PlanAnswered { Plan: var plan } => plan is { Runs: not null, Summary: not null }
                                                              && plan.Runs.All(r => r is { Project: not null, Name: not null, Mode: not null }),
            EngineResponse.JudgeAnswered { Report: var judge } => judge is not null,
            EngineResponse.GraphAnswered { Report: var graph } => graph is not null,
            EngineResponse.ImpactAnswered { Report: var impact } => impact is not null,
            EngineResponse.WatchEventPushed { Event: var ev } => ev is not null,
            EngineResponse.Unanswered unanswered => unanswered.Message is not null,
            _ => true,
        };
        return complete ? null : "the answer leaves out part of the result";
    }

    /// <summary>
    ///     True when an engine's pipe exists, checked without connecting (a connection attempt waits for a
    ///     timeout). An answer is kept per pipe name for 100 ms, so a post-edit round trip and a background
    ///     post-edit's confirmation round trip enumerate the pipes at most once between them.
    /// </summary>
    internal static bool PipeExists(string name) => PipeExists(name, Environment.TickCount64);

    /// <summary>True when an engine's pipe exists, as of <paramref name="now"/> in <see cref="Environment.TickCount64"/> milliseconds.</summary>
    internal static bool PipeExists(string name, long now)
    {
        if (PipeExistence.TryGetValue(name, out var kept) && now < kept.ExpiresAt)
            return kept.Exists;
        bool exists;
        try
        {
            if (OperatingSystem.IsWindows())
                exists = Directory.EnumerateFiles(@"\\.\pipe\", name).Any();
            else
                exists = File.Exists(PipeSocketPath(name));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return true; // Cannot tell; try to connect.
        }

        PipeExistence[name] = (exists, now + PipeExistsCacheMs);
        return exists;
    }

    /// <summary>
    ///     The socket file .NET listens on for the pipe <paramref name="name"/> on Linux and macOS: the temp directory
    ///     with <c>CoreFxPipe_</c> before the name, as the runtime's <c>PipeStream.Unix.GetPipePath</c> builds it. It
    ///     names no file on Windows, where a pipe is not a file.
    /// </summary>
    internal static string PipeSocketPath(string name) => Path.Combine(Path.GetTempPath(), "CoreFxPipe_" + name);

    /// <summary>
    ///     True when a process listens on the pipe's socket: the probe connects a raw socket and closes it at once,
    ///     which a live engine accepts and drops. False when the socket file is missing or the connect is refused,
    ///     because the engine was killed without closing its pipe. True on Windows, where a pipe is not a file, and
    ///     whenever the probe cannot tell, so the caller keeps its existing behavior.
    /// </summary>
    internal static bool PipeHasListener(string name)
    {
        if (OperatingSystem.IsWindows())
            return true;
        var path = PipeSocketPath(name);
        if (!File.Exists(path))
            return false;
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(path));
            return true;
        }
        catch (SocketException e)
        {
            // Only a refused connect proves no process listens; anything else keeps the existing behavior.
            return e.SocketErrorCode != SocketError.ConnectionRefused;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or ObjectDisposedException)
        {
            return true;
        }
    }

    private static async Task<NamedPipeClientStream> ConnectAsync(RepoRoot root, CancellationToken cancellationToken)
    {
        var begin = Environment.TickCount64;
        long lastStart = 0;
        if (!PipeExists(root.PipeName))
        {
            // No engine yet. Start one only where there is C# to compile, so hooks cost almost nothing elsewhere.
            if (!await RepoProbe.HasCSharpProjectsAsync(root, cancellationToken).ConfigureAwait(false))
                throw new FuseException(ErrorCode.NoProjects, ErrorMessages.NoProjects);
            EngineLauncher.Start(root.Path);
            lastStart = Environment.TickCount64;
        }

        while (true)
        {
            if (PipeExists(root.PipeName))
            {
                var pipe = new NamedPipeClientStream(".", root.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try
                {
                    await pipe.ConnectAsync(250, cancellationToken).ConfigureAwait(false);
                    return pipe;
                }
                catch (Exception e) when (e is TimeoutException or IOException)
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                    if (!PipeHasListener(root.PipeName) && Environment.TickCount64 - lastStart > 1500)
                    {
                        // No process listens on the pipe's socket: the engine was killed without closing its
                        // pipe. Start one as when the pipe is missing; it removes the file once it holds the
                        // root's mutex. On Windows the probe always answers true, so this changes nothing there.
                        // A redundant start is harmless: the second engine exits at once.
                        EngineLauncher.Start(root.Path);
                        lastStart = Environment.TickCount64;
                    }
                }
            }
            else if (Environment.TickCount64 - lastStart > 1500)
            {
                // No pipe: the engine is not running, or a starting engine lost the root's mutex to one that was
                // exiting. Starting again is harmless: a redundant engine exits at once.
                EngineLauncher.Start(root.Path);
                lastStart = Environment.TickCount64;
            }

            if (Environment.TickCount64 - begin > StartTimeout.TotalMilliseconds)
                throw new IOException($"it did not start within {StartTimeout.TotalSeconds:0} s (see {Path.Combine(root.StateDirectory, "engine.log")})");
            await Task.Delay(40, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Sends a <see cref="EngineRequest.YieldMemory"/> signal if an engine is already running for
    ///     <paramref name="root"/>, without ever starting one. Returns null when no engine runs or it cannot answer
    ///     within the short yield timeout.
    /// </summary>
    public static async Task<EngineResponse?> TryYieldMemoryAsync(RepoRoot root, CancellationToken cancellationToken)
    {
        if (!PipeExists(root.PipeName))
            return null;
        var timeout = TimeSpan.FromSeconds(2);
        var request = Stamp(root, new EngineRequest.YieldMemory(), timeout);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            var pipe = new NamedPipeClientStream(".", root.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await using var pipeLifetime = pipe.ConfigureAwait(false);
            await pipe.ConnectAsync(500, deadline.Token).ConfigureAwait(false);
            var bytes = Encoding.UTF8.GetBytes(ProtocolJson.Serialize(request) + "\n");
            await pipe.WriteAsync(bytes, deadline.Token).ConfigureAwait(false);
            await pipe.FlushAsync(deadline.Token).ConfigureAwait(false);
            var line = await PipeFraming.ReadLineAsync(pipe, deadline.Token).ConfigureAwait(false);
            if (line is null || Read(line, out var response) is not null)
                return null;
            return response;
        }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException or OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    ///     Runs a sequence of requests over one persistent pipe session (reconnecting automatically if the server closes
    ///     after a single response), eliminating per-request process spawns for <c>fuse batch</c>.
    /// </summary>
    public static async Task RunBatchSessionAsync(
        RepoRoot root,
        Func<Func<EngineRequest, TimeSpan, CancellationToken, Task<EngineResponse>>, Task> run,
        CancellationToken cancellationToken)
    {
        var session = new PipeSession(root);
        await using var sessionLifetime = session.ConfigureAwait(false);
        await run((req, timeout, ct) => session.SendAsync(req, timeout, ct)).ConfigureAwait(false);
    }

    /// <summary>
    ///     Streams server-push <see cref="WatchEvent"/> messages from a running engine's pipe until
    ///     <paramref name="cancellationToken"/> is cancelled, subscribing again after a drop, for example when the
    ///     engine restarts. True when cancellation ended the stream, so the caller exits; false when no engine runs
    ///     or the engine does not support push streams, so the caller falls back to polling.
    /// </summary>
    public static async Task<bool> WatchStreamAsync(
        RepoRoot root,
        EngineRequest.WatchStream request,
        Func<WatchEvent, Task> onEvent,
        CancellationToken cancellationToken)
    {
        if (!PipeExists(root.PipeName))
            return false;
        var stamped = (EngineRequest.WatchStream)Stamp(root, request, TimeSpan.FromHours(24));
        // True once an event arrived: push works, so a later drop subscribes again instead of falling back to polling.
        var supported = false;
        var restarts = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var dropped = false;
            try
            {
                if (!PipeExists(root.PipeName))
                {
                    // The engine is gone. When push never worked there is nothing to rejoin, so the caller polls;
                    // after a drop the engine may still be restarting, so wait for its pipe within the start window.
                    if (!supported)
                        return false;
                    var begin = Environment.TickCount64;
                    while (!PipeExists(root.PipeName))
                    {
                        if (Environment.TickCount64 - begin > StartTimeout.TotalMilliseconds)
                            return false;
                        await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                    }

                    continue;
                }

                var pipe = new NamedPipeClientStream(".", root.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await using var pipeLifetime = pipe.ConfigureAwait(false);
                await pipe.ConnectAsync(1000, cancellationToken).ConfigureAwait(false);
                var bytes = Encoding.UTF8.GetBytes(ProtocolJson.Serialize(stamped) + "\n");
                await pipe.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);

                // The engine writes events back to back, so two can arrive in one read; the reader keeps the second.
                var reader = new PipeLineReader(pipe);
                var restart = false;
                while (!cancellationToken.IsCancellationRequested && !restart)
                {
                    var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (line is null)
                        break;
                    if (Read(line, out var response) is not null || response is null)
                        break;
                    if (response is EngineResponse.WatchEventPushed pushed)
                    {
                        supported = true;
                        restarts = 0;
                        await onEvent(pushed.Event).ConfigureAwait(false);
                        continue;
                    }

                    if (response is EngineResponse.Restart)
                    {
                        // An engine from another build is draining while a matching engine starts, so this
                        // subscription waits and subscribes again whatever it has read so far.
                        restarts++;
                        await Task.Delay(RestartDelay(restarts), cancellationToken).ConfigureAwait(false);
                        restart = true;
                    }

                    if (response is EngineResponse.Unanswered)
                        return false;
                }

                if (restart)
                    continue;
                if (cancellationToken.IsCancellationRequested)
                    return true;
                if (!supported)
                    return false;
                dropped = true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return true;
            }
            catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException)
            {
                if (cancellationToken.IsCancellationRequested)
                    return true;
                if (!supported)
                    return false;
                dropped = true;
            }

            // The stream dropped after events flowed: pause so a flap does not spin, then subscribe again.
            if (dropped)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return true;
                }
            }
        }

        return true;
    }

    private static EngineRequest Stamp(RepoRoot root, EngineRequest request, TimeSpan timeout) =>
        request with
        {
            BuildId = EngineVersion.Build,
            RequestId = NextRequestId(),
            WaitMs = (long)timeout.TotalMilliseconds,
            WorktreeRoot = request.WorktreeRoot ?? root.Path,
        };

    private sealed class PipeSession : IAsyncDisposable
    {
        private readonly RepoRoot _root;
        private NamedPipeClientStream? _pipe;

        public PipeSession(RepoRoot root)
        {
            _root = root;
        }

        public async Task<EngineResponse> SendAsync(EngineRequest request, TimeSpan timeout, CancellationToken cancellationToken)
        {
            request = Stamp(_root, request, timeout);
            var sendStarted = Environment.TickCount64;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            try
            {
                var restarts = 0;
                for (var attempt = 0; ;)
                {
                    string? line;
                    try
                    {
                        var pipe = await EnsureConnectedAsync(deadline.Token).ConfigureAwait(false);
                        var bytes = Encoding.UTF8.GetBytes(ProtocolJson.Serialize(request) + "\n");
                        await pipe.WriteAsync(bytes, deadline.Token).ConfigureAwait(false);
                        await pipe.FlushAsync(deadline.Token).ConfigureAwait(false);
                        line = await PipeFraming.ReadLineAsync(pipe, deadline.Token).ConfigureAwait(false);
                    }
                    catch (IOException) when (attempt < 4)
                    {
                        await ResetPipeAsync().ConfigureAwait(false);
                        await Task.Delay(50 * (attempt + 1), deadline.Token).ConfigureAwait(false);
                        attempt++;
                        continue;
                    }

                    EngineResponse? response = null;
                    if (line is not null && Read(line, out response) is { } reason)
                        return new EngineResponse.Unanswered(ErrorCode.Internal, $"the Fuse engine sent an answer this client cannot read ({reason}; see {Path.Combine(_root.StateDirectory, "engine.log")})");
                    if (response is null)
                    {
                        await ResetPipeAsync().ConfigureAwait(false);
                        if (attempt < 2)
                        {
                            attempt++;
                            continue;
                        }

                        return new EngineResponse.Unanswered(ErrorCode.Internal, $"the Fuse engine closed the connection (see {Path.Combine(_root.StateDirectory, "engine.log")})");
                    }

                    if (response is EngineResponse.Busy busy)
                    {
                        var remainingMs = timeout.TotalMilliseconds - (Environment.TickCount64 - sendStarted);
                        if (remainingMs <= 0)
                            return new EngineResponse.Unanswered(ErrorCode.Busy, ErrorMessages.Busy(busy.Position, busy.EstimatedWaitMs));
                        var delayMs = Math.Clamp(busy.EstimatedWaitMs, 50, 5000);
                        if (delayMs >= remainingMs)
                            return new EngineResponse.Unanswered(ErrorCode.Busy, ErrorMessages.Busy(busy.Position, busy.EstimatedWaitMs));
                        await Task.Delay(TimeSpan.FromMilliseconds(delayMs), deadline.Token).ConfigureAwait(false);
                        continue;
                    }

                    if (response is EngineResponse.Restart)
                    {
                        await ResetPipeAsync().ConfigureAwait(false);
                        restarts++;
                        await Task.Delay(RestartDelay(restarts), deadline.Token).ConfigureAwait(false);
                        continue;
                    }

                    return response;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new EngineResponse.Unanswered(ErrorCode.Timeout, $"the Fuse engine did not answer within {timeout.TotalSeconds:0} s");
            }
            catch (FuseException e)
            {
                return new EngineResponse.Unanswered(e.Code, e.Message);
            }
            catch (IOException e)
            {
                return new EngineResponse.Unanswered(ErrorCode.Internal, $"could not connect to the Fuse engine: {e.Message}");
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or UnauthorizedAccessException)
            {
                return new EngineResponse.Unanswered(ErrorCode.Internal, $"could not start the Fuse engine: {e.Message}");
            }
        }

        private async Task<NamedPipeClientStream> EnsureConnectedAsync(CancellationToken cancellationToken)
        {
            if (_pipe is { IsConnected: true })
                return _pipe;
            await ResetPipeAsync().ConfigureAwait(false);
            _pipe = await ConnectAsync(_root, cancellationToken).ConfigureAwait(false);
            return _pipe;
        }

        private async Task ResetPipeAsync()
        {
            if (_pipe is not null)
            {
                await _pipe.DisposeAsync().ConfigureAwait(false);
                _pipe = null;
            }
        }

        public ValueTask DisposeAsync() => ResetPipeAsync() is var t ? new ValueTask(t) : ValueTask.CompletedTask;
    }
}
