using System.IO.Pipes;
using System.Text;
using Fuse.Protocol;
using Fuse.Repo;
using Fuse.Workspace;

namespace Fuse.Engine;

/// <summary>Sends one request to the repository's engine, starting the engine when none is running.</summary>
internal static class EngineClient
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Sends <paramref name="request"/> and waits for the answer.</summary>
    /// <param name="root">The repository whose engine to use.</param>
    /// <param name="request">The request. Its version is replaced with this build's.</param>
    /// <param name="timeout">How long to wait for the answer.</param>
    /// <param name="cancellationToken">Cancels the request; the engine stops the work when the connection closes.</param>
    public static async Task<EngineResponse> SendAsync(RepoRoot root, EngineRequest request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        request = request with { Version = EngineVersion.Build };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                await using var pipe = await ConnectAsync(root, deadline.Token).ConfigureAwait(false);
                var bytes = Encoding.UTF8.GetBytes(ProtocolJson.Serialize(request) + "\n");
                await pipe.WriteAsync(bytes, deadline.Token).ConfigureAwait(false);
                await pipe.FlushAsync(deadline.Token).ConfigureAwait(false);
                var line = await EngineServer.ReadLineAsync(pipe, deadline.Token).ConfigureAwait(false);
                var response = line is null ? null : ProtocolJson.ReadResponse(line);
                if (response is null)
                {
                    if (attempt < 1)
                        continue;
                    return EngineResponse.Fail(ErrorCode.Internal, $"the fuse engine closed the connection (see {Path.Combine(root.StateDirectory, "engine.log")})");
                }

                if (response.Status == ResponseStatus.Restart && attempt < 2)
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
            return EngineResponse.Fail(ErrorCode.Timeout, $"the fuse engine did not answer within {timeout.TotalSeconds:0} s");
        }
        catch (FuseException e)
        {
            return EngineResponse.Fail(e.Code, e.Message);
        }
        catch (IOException e)
        {
            return EngineResponse.Fail(ErrorCode.Internal, $"could not talk to the fuse engine: {e.Message}");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        {
            return EngineResponse.Fail(ErrorCode.Internal, $"could not start the fuse engine: {e.Message}");
        }
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
                throw new FuseException(ErrorCode.NoProjects, "no C# projects (.csproj) in this repository, so there is nothing to check");
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
                throw new IOException($"the fuse engine did not start within {StartTimeout.TotalSeconds:0} s (see {Path.Combine(root.StateDirectory, "engine.log")})");
            await Task.Delay(40, cancellationToken).ConfigureAwait(false);
        }
    }
}
