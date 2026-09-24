using System.Diagnostics;
using System.Text;

namespace Fuse.Dotnet;

/// <summary>Result of a finished child process.</summary>
/// <param name="ExitCode">The process exit code, or -1 when it could not start.</param>
/// <param name="Output">Standard output and standard error, interleaved in arrival order.</param>
internal sealed record ProcessResult(int ExitCode, string Output);

/// <summary>Runs child processes with argument lists (never a shell string) and kills the whole tree on cancellation.</summary>
internal static class ProcessRunner
{
    /// <summary>Runs a process to completion and captures its output.</summary>
    /// <param name="fileName">Executable to run.</param>
    /// <param name="arguments">Arguments, passed without shell interpretation.</param>
    /// <param name="workingDirectory">Working directory for the process.</param>
    /// <param name="cancellationToken">Kills the process tree when cancelled.</param>
    /// <param name="environment">Extra environment variables.</param>
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null)
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
        // Child dotnet processes must not start their own long-lived build servers inside a hook's process tree,
        // and must never prompt.
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";
        psi.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        if (environment is not null)
            foreach (var (key, value) in environment)
                psi.Environment[key] = value;

        var output = new StringBuilder();
        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => Append(output, e.Data);
        process.ErrorDataReceived += (_, e) => Append(output, e.Data);
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
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }

            throw;
        }

        // WaitForExitAsync returns after the output streams reach end of file, so the buffer is complete.
        lock (output)
            return new ProcessResult(process.ExitCode, output.ToString());
    }

    private static void Append(StringBuilder output, string? line)
    {
        if (line is null)
            return;
        lock (output)
            output.Append(line).Append('\n');
    }
}
