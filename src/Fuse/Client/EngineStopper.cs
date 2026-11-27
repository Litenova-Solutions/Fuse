using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Fuse.Paths;
using Fuse.Protocol.Analysis;
using Fuse.Protocol.Requests;
using Fuse.Protocol.Transport;
namespace Fuse.Client;

/// <summary>
///     Stops a repository's engine on demand, or releases the caller's secondary worktree while one engine serves the
///     repository's worktrees: it asks the engine to shut down, as an engine of another build is asked to, and kills the
///     engine's process when the engine has not gone within <see cref="Grace"/>. It never starts an engine.
/// </summary>
/// <remarks>
///     The process to kill is the one the engine log's last start line names, and only while it is a <c>dotnet</c> or
///     <c>fuse</c> process, so a process that took over a recycled id is left alone.
/// </remarks>
internal static class EngineStopper
{
    /// <summary>How long an engine gets to finish its requests and exit after it is asked to.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(10);

    /// <summary>
    ///     How long after the shutdown answer a still-answering engine is taken as released rather than stopping: a
    ///     stopping engine's pipe disappears at once, so one that answers past this keeps serving.
    /// </summary>
    private static readonly TimeSpan ReleaseSettle = TimeSpan.FromSeconds(1);

    /// <summary>
    ///     Stops the engine of <paramref name="root"/>, or releases <paramref name="root"/> when it is a secondary
    ///     worktree of a shared engine. Returns whether one was running, and the id of the process that was killed
    ///     because it did not exit when asked, or null when it exited on its own, was released, or none ran.
    /// </summary>
    public static async Task<(bool WasRunning, int? Killed)> StopAsync(RepoRoot root, CancellationToken cancellationToken)
    {
        var (wasRunning, killed, _) = await StopOrReleaseAsync(root, cancellationToken).ConfigureAwait(false);
        return (wasRunning, killed);
    }

    /// <summary>
    ///     Stops the engine of <paramref name="root"/>, or releases <paramref name="root"/> when it is a secondary
    ///     worktree of a shared engine, which keeps serving. Returns whether one was running, the id of the process
    ///     that was killed because it did not exit when asked (or null), and whether the worktree was released.
    /// </summary>
    public static async Task<(bool WasRunning, int? Killed, bool Released)> StopOrReleaseAsync(RepoRoot root, CancellationToken cancellationToken)
    {
        if (!EngineClient.PipeExists(root.PipeName))
            return (false, null, false);

        await AskAsync(root, cancellationToken).ConfigureAwait(false);
        var start = Environment.TickCount64;
        var deadline = start + (long)Grace.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if (!EngineClient.PipeExists(root.PipeName))
                return (true, null, false);
            if (Environment.TickCount64 - start >= (long)ReleaseSettle.TotalMilliseconds && await PingAsync(root, cancellationToken).ConfigureAwait(false))
                return (true, null, true);
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }

        if (!EngineClient.PipeExists(root.PipeName))
            return (true, null, false);
        return (true, Kill(LocalState.LastEnginePid(root.StateDirectory) ?? LocalState.LastEnginePid(root.SharedStateDirectory)), false);
    }

    /// <summary>True when the engine answers a liveness probe: it is still serving requests.</summary>
    private static async Task<bool> PingAsync(RepoRoot root, CancellationToken cancellationToken)
    {
        try
        {
            var pipe = new NamedPipeClientStream(".", root.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await using var pipeLifetime = pipe.ConfigureAwait(false);
            await pipe.ConnectAsync(2000, cancellationToken).ConfigureAwait(false);
            var request = new EngineRequest.Ping { BuildId = EngineVersion.Build, WorktreeRoot = root.Path };
            var bytes = Encoding.UTF8.GetBytes(ProtocolJson.Serialize(request) + "\n");
            await pipe.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
            using var answered = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            answered.CancelAfter(TimeSpan.FromSeconds(2));
            var line = await PipeFraming.ReadLineAsync(pipe, answered.Token).ConfigureAwait(false);
            return line is not null && ProtocolJson.ReadResponse(line) is EngineResponse.Acknowledged;
        }
        catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException or System.Text.Json.JsonException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Sends the shutdown request; an engine too busy or broken to answer is killed after the grace period instead.</summary>
    private static async Task AskAsync(RepoRoot root, CancellationToken cancellationToken)
    {
        try
        {
            var pipe = new NamedPipeClientStream(".", root.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await using var pipeLifetime = pipe.ConfigureAwait(false);
            await pipe.ConnectAsync(2000, cancellationToken).ConfigureAwait(false);
            var request = new EngineRequest.ShutDown { BuildId = EngineVersion.Build, WorktreeRoot = root.Path };
            var bytes = Encoding.UTF8.GetBytes(ProtocolJson.Serialize(request) + "\n");
            await pipe.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
            using var answered = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            answered.CancelAfter(Grace);
            await PipeFraming.ReadLineAsync(pipe, answered.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException)
        {
        }
    }

    private static int? Kill(int? pid)
    {
        if (pid is not { } id)
            return null;
        try
        {
            using var process = Process.GetProcessById(id);
            if (!process.ProcessName.Equals("dotnet", StringComparison.OrdinalIgnoreCase) && !process.ProcessName.StartsWith("fuse", StringComparison.OrdinalIgnoreCase))
                return null;
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
            return id;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The process has exited, or this user may not end it.
            return null;
        }
    }
}
