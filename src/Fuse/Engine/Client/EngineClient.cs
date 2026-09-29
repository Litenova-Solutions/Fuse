using System.IO.Pipes;
using System.Text;
using Fuse.Failures;
using Fuse.Paths;
using Fuse.Protocol;
using Fuse.Repo;

namespace Fuse.Engine.Client;

/// <summary>Sends one request to the repository's engine, starting the engine when none is running.</summary>
internal static class EngineClient
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(15);

    private static int _counter;

    /// <summary>
    ///     Names the request for the engine's log. The process id keeps two clients apart, the counter keeps two requests
    ///     from one client apart, so a line in <c>engine.log</c> can be matched to the call that produced it.
    /// </summary>
    public static string NextRequestId() => $"{Environment.ProcessId}-{Interlocked.Increment(ref _counter)}";

    /// <summary>Sends <paramref name="request"/> and waits for the answer.</summary>
    /// <param name="root">The repository whose engine to use.</param>
    /// <param name="request">The request. Its build id and request id are replaced with this build's and a fresh one.</param>
    /// <param name="timeout">How long to wait for the answer.</param>
    /// <param name="cancellationToken">Cancels the request; the engine stops the work when the connection closes.</param>
    public static async Task<EngineResponse> SendAsync(RepoRoot root, EngineRequest request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        request = request with { BuildId = EngineVersion.Build, RequestId = NextRequestId() };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                string? line;
                try
                {
                    await using var pipe = await ConnectAsync(root, deadline.Token).ConfigureAwait(false);
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
                    continue;
                }

                EngineResponse? response = null;
                if (line is not null && Read(line, out response) is { } reason)
                    return new EngineResponse.Unanswered(ErrorCode.Internal, $"the Fuse engine sent an answer this client cannot read ({reason}; see {Path.Combine(root.StateDirectory, "engine.log")})");
                if (response is null)
                {
                    if (attempt < 1)
                        continue;
                    return new EngineResponse.Unanswered(ErrorCode.Internal, $"the Fuse engine closed the connection (see {Path.Combine(root.StateDirectory, "engine.log")})");
                }

                if (response is EngineResponse.Restart && attempt < 2)
                {
                    // An engine from another build is exiting; give it a moment to release the pipe and mutex.
                    await Task.Delay(300, deadline.Token).ConfigureAwait(false);
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
            EngineResponse.Unanswered unanswered => unanswered.Message is not null,
            _ => true,
        };
        return complete ? null : "the answer leaves out part of the result";
    }

    /// <summary>True when an engine's pipe exists, checked without connecting (a connection attempt waits for a timeout).</summary>
    private static bool PipeExists(string name)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return Directory.EnumerateFiles(@"\\.\pipe\", name).Any();
            // .NET implements named pipes on Unix as domain sockets in the temp directory.
            return File.Exists(Path.Combine(Path.GetTempPath(), "CoreFxPipe_" + name));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return true; // Cannot tell; try to connect.
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
}
