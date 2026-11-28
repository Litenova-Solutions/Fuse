using System.Diagnostics;
using System.Text;
namespace Fuse.Dotnet.Processes;

/// <summary>Runs child processes with argument lists (never a shell string) and kills the whole tree on cancellation.</summary>
internal sealed class ProcessRunner : IProcessRunner
{
    private ProcessRunner()
    {
    }

    /// <summary>The runner that starts real processes, for a type that takes an <see cref="IProcessRunner"/>.</summary>
    public static IProcessRunner Default { get; } = new ProcessRunner();

    /// <summary>The variables MSBuild Locator sets to point a process at one SDK's MSBuild.</summary>
    internal static readonly string[] MsBuildLocatorVariables = ["MSBUILD_EXE_PATH", "MSBuildExtensionsPath", "MSBuildSDKsPath"];

    /// <summary>How long a run waits for a process tree it killed on inactivity to exit before answering without it.</summary>
    private static readonly TimeSpan KillGrace = TimeSpan.FromSeconds(10);

    /// <inheritdoc/>
    Task<ProcessResult> IProcessRunner.RunAsync(string fileName, IEnumerable<string> arguments, string workingDirectory, TimeSpan? inactivityTimeout, CancellationToken cancellationToken) =>
        RunAsync(fileName, arguments, workingDirectory, cancellationToken, inactivityTimeout);

    /// <summary>Runs a process to completion and captures its output.</summary>
    /// <param name="arguments">Arguments, passed without shell interpretation.</param>
    /// <param name="cancellationToken">Kills the process tree when cancelled.</param>
    /// <param name="inactivityTimeout">
    ///     How long the process may write no output before its tree is killed and the result reads timed out, or null
    ///     to wait without a timeout. Every output line renews it.
    /// </param>
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken,
        TimeSpan? inactivityTimeout = null)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);
        // English output, so BuildOutputParser and TrxReader can parse it, and no telemetry notice or logo in the
        // captured output.
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";
        psi.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        // MSBuild Locator points these at the SDK the engine loaded. A child dotnet must resolve its own SDK from the
        // global.json of the directory it runs in, which can name another SDK, for example in another worktree.
        foreach (var variable in MsBuildLocatorVariables)
            psi.Environment.Remove(variable);

        var output = new StringBuilder();
        // When the last output line arrived, in Environment.TickCount64 milliseconds; every line renews the timeout.
        var lastOutput = Environment.TickCount64;
        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => Append(output, e.Data, ref lastOutput);
        process.ErrorDataReceived += (_, e) => Append(output, e.Data, ref lastOutput);
        try
        {
            if (!process.Start())
                return new ProcessResult(-1, $"could not start {fileName}");
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            return new ProcessResult(-1, $"could not start {fileName}: {e.Message}");
        }

        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        var timedOut = false;
        while (true)
        {
            if (inactivityTimeout is null)
            {
                try
                {
                    await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    KillProcessTree(process);
                    throw;
                }

                break;
            }

            var remaining = inactivityTimeout.Value - TimeSpan.FromMilliseconds(Environment.TickCount64 - Volatile.Read(ref lastOutput));
            if (remaining <= TimeSpan.Zero)
            {
                // The process may have exited between its last line and now; only one that is still running is
                // killed and reported as timed out.
                if (process.HasExited)
                    break;
                KillProcessTree(process);
                timedOut = true;
                break;
            }

            using var slice = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            slice.CancelAfter(remaining);
            try
            {
                await process.WaitForExitAsync(slice.Token).ConfigureAwait(false);
                break;
            }
            catch (OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    KillProcessTree(process);
                    throw;
                }
                // The slice fired; output may have arrived since, so the loop recomputes instead of killing.
            }
        }

        if (timedOut)
        {
            try
            {
                using var grace = new CancellationTokenSource(KillGrace);
                await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The tree is killed; a process that still lingers is answered without its exit code.
            }
        }

        // WaitForExitAsync returns after the output streams reach end of file, so the buffer is complete.
        lock (output)
            return new ProcessResult(process.HasExited ? process.ExitCode : -1, output.ToString()) { TimedOut = timedOut };
    }

    /// <summary>
    ///     Kills <paramref name="process"/> with its tree, best effort. A process that already exited needs nothing; a
    ///     tree kill the platform does not cover falls back to the process alone; a kill the operating system refuses
    ///     is left as it is. It never throws.
    /// </summary>
    internal static void KillProcessTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (NotSupportedException)
        {
            try
            {
                process.Kill();
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already exited, or killing is refused; the caller answers without it.
        }
    }

    private static void Append(StringBuilder output, string? line, ref long lastOutput)
    {
        if (line is null)
            return;
        Volatile.Write(ref lastOutput, Environment.TickCount64);
        lock (output)
            output.Append(line).Append('\n');
    }
}
