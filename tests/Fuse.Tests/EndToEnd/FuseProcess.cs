using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Fuse.Engine;
using Fuse.Protocol;
using Fuse.Repo;

namespace Fuse.Tests.EndToEnd;

/// <summary>Runs the real <c>fuse</c> executable and talks to its engine over the pipe, as a harness would.</summary>
internal static class FuseProcess
{
    private static string Executable => Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "fuse.exe" : "fuse");

    public static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(string workingDirectory, string? stdin, params string[] arguments)
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
        using var process = System.Diagnostics.Process.Start(psi)!;
        if (stdin is not null)
            await process.StandardInput.WriteAsync(stdin);
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        // The client must exit without waiting for the engine it started: a detached engine holding the client's
        // stdout would keep these reads open and fail the test by timeout.
        await Task.WhenAll(stdout, stderr).WaitAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, stdout.Result, stderr.Result);
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
}
