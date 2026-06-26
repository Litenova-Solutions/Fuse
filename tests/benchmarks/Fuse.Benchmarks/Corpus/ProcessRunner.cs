using System.Diagnostics;
using System.Text;

namespace Fuse.Benchmarks;

/// <summary>
///     A thin wrapper over an arbitrary external command, used by the task-resolution harness to run a
///     repository's test oracle (for example <c>dotnet test</c>). Like <see cref="GitCli" /> and
///     <see cref="DotnetCli" />, it passes a bounded, fixed argument list.
/// </summary>
public static class ProcessRunner
{
    /// <summary>
    ///     Runs an executable with the given arguments in the given working directory.
    /// </summary>
    /// <param name="fileName">The executable to run.</param>
    /// <param name="workingDirectory">The working directory, or null to use the process default.</param>
    /// <param name="cancellationToken">A token to cancel the invocation.</param>
    /// <param name="arguments">The fixed arguments.</param>
    /// <returns>The invocation result, reusing <see cref="GitCli.GitResult" /> for exit code and output.</returns>
    public static async Task<GitCli.GitResult> RunAsync(
        string fileName,
        string? workingDirectory,
        CancellationToken cancellationToken,
        params string[] arguments)
    {
        var psi = new ProcessStartInfo(fileName)
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
        await process.WaitForExitAsync(cancellationToken);
        return new GitCli.GitResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }
}
