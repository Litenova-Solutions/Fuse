using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Fuse.Engine;
using Fuse.Paths;
using Fuse.Protocol;

namespace Fuse.Tests.EndToEnd;

/// <summary>Runs the real <c>fuse</c> executable and talks to its engine over the pipe, as a harness would.</summary>
internal static class FuseProcess
{
    private static string Executable => Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "fuse.exe" : "fuse");

    public static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(string workingDirectory, string? stdin, params string[] arguments)
    {
        await using var client = Start(workingDirectory, arguments);
        client.Send(stdin);
        return await client.WaitAsync(TimeSpan.FromMinutes(3));
    }

    /// <summary>
    ///     Starts a client without waiting for it, so a test can watch what it says while it is still running. A client
    ///     that waits for a lock announces itself before it gets one, and that announcement is only observable this way.
    /// </summary>
    public static Client Start(string workingDirectory, params string[] arguments)
    {
        var psi = new ProcessStartInfo(Executable)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);
        return new Client(System.Diagnostics.Process.Start(psi)!);
    }

    /// <summary>Sends one raw request, bypassing the client's version stamping and engine start.</summary>
    public static async Task<EngineResponse?> SendRawAsync(RepoRoot root, EngineRequest request)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var pipe = new NamedPipeClientStream(".", root.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.ConnectAsync(2000);
                var bytes = Encoding.UTF8.GetBytes(ProtocolJson.Serialize(request) + "\n");
                await pipe.WriteAsync(bytes);
                await pipe.FlushAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var line = await EngineServer.ReadLineAsync(pipe, timeout.Token);
                return line is null ? null : ProtocolJson.ReadResponse(line);
            }
            catch (IOException) when (attempt < 4)
            {
                // A connection that arrives while a Unix pipe server instance is being replaced is reset; retry as the client does.
                await Task.Delay(100);
            }
        }
    }

    public static bool EngineRunning(RepoRoot root)
    {
        using var pipe = new NamedPipeClientStream(".", root.PipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
        try
        {
            pipe.Connect(200);
            return true;
        }
        catch (Exception e) when (e is TimeoutException or IOException)
        {
            return false;
        }
    }

    public static async Task<bool> WaitForExitAsync(RepoRoot root, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!EngineRunning(root))
                return true;
            await Task.Delay(200);
        }

        return false;
    }

    /// <summary>Stops the repository's engine if one is running.</summary>
    public static async Task StopEngineAsync(RepoRoot root)
    {
        try
        {
            if (EngineRunning(root))
                await SendRawAsync(root, new EngineRequest(EngineVersion.Build, RequestKind.Shutdown));
        }
        catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException)
        {
        }

        await WaitForExitAsync(root, TimeSpan.FromSeconds(15));
    }

    /// <summary>A started client, whose output can be read as it arrives and whose lifetime the test controls.</summary>
    internal sealed class Client : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly StringBuilder _stdout = new();
        private readonly StringBuilder _stderr = new();
        private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Client(Process process)
        {
            _process = process;
            // Both pumps are started before anything waits, so neither pipe can fill and stall the client.
            _ = PumpAsync(process.StandardOutput, _stdout);
            _ = PumpAsync(process.StandardError, _stderr);
            // Without this the Exited event is never raised, and waiting on it would wait for the test's own timeout.
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => _exited.TrySetResult();
        }

        /// <summary>What the client has written to standard error so far, which is where a waiting client says so.</summary>
        public string StderrSoFar
        {
            get
            {
                lock (_stderr)
                    return _stderr.ToString();
            }
        }

        public int Id => _process.Id;

        /// <summary>True once the client process has exited.</summary>
        public bool HasExited => _process.HasExited;

        public void Send(string? stdin)
        {
            if (stdin is null)
            {
                _process.StandardInput.Close();
                return;
            }

            // Every harness writes UTF-8 to a hook's standard input, with no byte order mark. The encoding is set on the
            // stream rather than on StandardInput, which encodes with the console output code page instead.
            var bytes = new UTF8Encoding(false).GetBytes(stdin);
            _process.StandardInput.BaseStream.Write(bytes);
            _process.StandardInput.Close();
        }

        /// <summary>Waits for the client to finish, or gives up after <paramref name="timeout"/>.</summary>
        public async Task<(int ExitCode, string Stdout, string Stderr)> WaitAsync(TimeSpan timeout)
        {
            using var cancellation = new CancellationTokenSource(timeout);
            // The client must exit without waiting for the engine it started: a detached engine holding the client's
            // stdout would keep these reads open and fail the test by timeout.
            await _process.WaitForExitAsync(cancellation.Token);
            await _exited.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellation.Token);
            // The output pumps finish just after the process does; a short wait keeps a truncated last line out of the
            // assertion that follows.
            await Task.Delay(100, cancellation.Token);
            return (_process.ExitCode, Stdout, Stderr);
        }

        /// <summary>Kills the client where it stands, the way an agent's shell or a cancelled hook would.</summary>
        public void Kill()
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (Exception e) when (e is InvalidOperationException or NotSupportedException or SystemException)
            {
                // Already gone.
            }
        }

        private string Stdout
        {
            get
            {
                lock (_stdout)
                    return _stdout.ToString().TrimEnd('\n');
            }
        }

        private string Stderr
        {
            get
            {
                lock (_stderr)
                    return _stderr.ToString().TrimEnd('\n');
            }
        }

        private static async Task PumpAsync(StreamReader reader, StringBuilder into)
        {
            string? line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                lock (into)
                    into.Append(line).Append('\n');
            }
        }

        public async ValueTask DisposeAsync()
        {
            // A client that has already finished is left alone: killing its process tree would take the engine it started
            // with it, and the next call in the test would find no engine.
            if (!_exited.Task.IsCompleted)
            {
                Kill();
                try
                {
                    await _exited.Task.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // A client the test abandoned is not a failure of the client's own; the handle is released either way.
                }
            }

            _process.Dispose();
        }
    }
}
