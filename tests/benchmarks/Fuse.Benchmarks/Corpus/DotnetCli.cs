using System.Diagnostics;
using System.Text;

namespace Fuse.Benchmarks;

/// <summary>
///     A thin wrapper over the <c>dotnet</c> command-line, used by <see cref="CorpusManager" /> to restore
///     a corpus repository's NuGet packages before semantic indexing. Like <see cref="GitCli" />, every
///     invocation passes a bounded, fixed argument list (a verb and at most one target path, never a
///     variable-length project list), so it cannot overflow the OS command-line limit.
/// </summary>
public static class DotnetCli
{
    /// <summary>
    ///     Runs <c>dotnet</c> with the given arguments in the given working directory.
    /// </summary>
    /// <param name="workingDirectory">The working directory, or null to use the process default.</param>
    /// <param name="cancellationToken">A token to cancel the invocation.</param>
    /// <param name="arguments">The fixed dotnet arguments.</param>
    /// <returns>The invocation result, reusing <see cref="GitCli.GitResult" /> for exit code and captured output.</returns>
    public static async Task<GitCli.GitResult> RunAsync(
        string? workingDirectory,
        CancellationToken cancellationToken,
        params string[] arguments)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // A per-repo hard timeout (or an outer cancel) must actually stop the work: kill the whole
            // dotnet process tree so a stalling restore does not leak an orphaned child that keeps holding
            // files and CPU through the rest of a multi-hour sweep (D20).
            TryKillTree(process);
            throw;
        }

        return new GitCli.GitResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    // Best-effort termination of the process and its descendants; a race where it already exited is benign.
    private static void TryKillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Already exited or not killable; nothing to reclaim.
        }
    }
}
