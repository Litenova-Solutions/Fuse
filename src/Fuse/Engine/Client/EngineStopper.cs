using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Fuse.Paths;
using Fuse.Protocol;

namespace Fuse.Engine.Client;

/// <summary>
///     Stops a repository's engine on demand: it asks the engine to shut down, as an engine of another build is asked to,
///     and kills the engine's process when the engine has not gone within <see cref="Grace"/>. It never starts an engine.
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
    ///     Stops the engine of <paramref name="root"/>. Returns whether one was running, and the id of the process that was
    ///     killed because it did not exit when asked, or null when it exited on its own or none ran.
    /// </summary>
    public static async Task<(bool WasRunning, int? Killed)> StopAsync(RepoRoot root, CancellationToken cancellationToken)
    {
        if (!EngineClient.PipeExists(root.PipeName))
            return (false, null);

        await AskAsync(root, cancellationToken).ConfigureAwait(false);
        var deadline = Environment.TickCount64 + (long)Grace.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if (!EngineClient.PipeExists(root.PipeName))
                return (true, null);
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }

        return (true, Kill(LocalState.LastEnginePid(root.StateDirectory)));
    }

    /// <summary>Sends the shutdown request; an engine too busy or broken to answer is killed after the grace period instead.</summary>
    private static async Task AskAsync(RepoRoot root, CancellationToken cancellationToken)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(".", root.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(2000, cancellationToken).ConfigureAwait(false);
            var request = new EngineRequest.ShutDown { BuildId = EngineVersion.Build };
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
