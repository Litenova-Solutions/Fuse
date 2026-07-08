using System.Diagnostics;

namespace Fuse.Workspace;

/// <summary>
///     The outcome of running a child process under a hard timeout (T1): its exit code and captured output, or a
///     timeout flag when the process outran its budget and was killed.
/// </summary>
/// <param name="ExitCode">The process exit code, or null when it timed out and was killed.</param>
/// <param name="StandardOutput">The captured standard output.</param>
/// <param name="StandardError">The captured standard error.</param>
/// <param name="TimedOut">Whether the process exceeded the timeout and its process tree was killed.</param>
public sealed record ProcessRunResult(int? ExitCode, string StandardOutput, string StandardError, bool TimedOut);

/// <summary>
///     Runs a child process to completion under a hard timeout and kills its entire process tree if it overruns
///     (T1): the isolation primitive the out-of-process test micro-host is built on, so a hanging or runaway test
///     host kills its own child and nothing else, and never wedges the caller. Output is captured with concurrent
///     reads to avoid the classic pipe-buffer deadlock.
/// </summary>
public static class TimedProcess
{
    /// <summary>
    ///     Runs a process and returns its exit code and output, killing its process tree on timeout.
    /// </summary>
    /// <param name="fileName">The executable to run.</param>
    /// <param name="arguments">The arguments, passed as discrete tokens (never a concatenated command line).</param>
    /// <param name="workingDirectory">The working directory, or null for the current one.</param>
    /// <param name="environment">
    ///     Environment variables for the child. When non-null the child starts from a stripped environment holding
    ///     only these entries (the isolation the micro-host wants); when null it inherits the parent environment.
    /// </param>
    /// <param name="timeout">The hard timeout; on overrun the process tree is killed and <see cref="ProcessRunResult.TimedOut" /> is true.</param>
    /// <param name="cancellationToken">A token to cancel the run (also kills the tree).</param>
    /// <returns>The run result.</returns>
    /// <exception cref="InvalidOperationException">The process could not be started.</exception>
    public static async Task<ProcessRunResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        IReadOnlyDictionary<string, string>? environment,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        if (environment is not null)
        {
            // A stripped environment: drop the inherited variables and set only the supplied ones, so the test
            // host cannot read ambient state (connection strings, tokens) that would make a run non-hermetic.
            startInfo.Environment.Clear();
            foreach (var (key, value) in environment)
                startInfo.Environment[key] = value;
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException($"Failed to start process '{fileName}'.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The process outran the timeout: kill the whole tree so a child it spawned cannot outlive it.
            timedOut = true;
            KillTree(process);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            throw;
        }

        // The output reads complete once the streams close (the process has exited or been killed).
        var stdout = await SafeReadAsync(stdoutTask);
        var stderr = await SafeReadAsync(stderrTask);

        return new ProcessRunResult(timedOut ? null : process.ExitCode, stdout, stderr, timedOut);
    }

    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The process already exited between the check and the kill: nothing to do.
        }
    }

    private static async Task<string> SafeReadAsync(Task<string> readTask)
    {
        try
        {
            return await readTask;
        }
        catch (OperationCanceledException)
        {
            return string.Empty;
        }
    }
}
