using System.Diagnostics;
using System.Text;

namespace Fuse.Benchmarks;

/// <summary>
///     A thin wrapper over the <c>git</c> command-line, used by <see cref="CorpusManager" /> to pin
///     repositories and reconstruct pull-request change sets. All invocations pass a bounded, fixed
///     argument list (refs and flags, never a variable-length path list), so they cannot overflow the
///     OS command-line limit.
/// </summary>
public static class GitCli
{
    /// <summary>The result of a git invocation.</summary>
    /// <param name="ExitCode">The process exit code.</param>
    /// <param name="StdOut">Captured standard output.</param>
    /// <param name="StdErr">Captured standard error.</param>
    public readonly record struct GitResult(int ExitCode, string StdOut, string StdErr)
    {
        /// <summary>Whether the invocation succeeded (exit code 0).</summary>
        public bool Ok => ExitCode == 0;

        /// <summary>The standard output split into non-empty trimmed lines.</summary>
        public IReadOnlyList<string> Lines => StdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Length > 0)
            .ToList();
    }

    /// <summary>
    ///     Runs git with the given arguments in the given working directory.
    /// </summary>
    /// <param name="workingDirectory">The working directory, or null to use the process default.</param>
    /// <param name="cancellationToken">A token to cancel the invocation.</param>
    /// <param name="arguments">The fixed git arguments.</param>
    /// <returns>The invocation result.</returns>
    public static async Task<GitResult> RunAsync(
        string? workingDirectory,
        CancellationToken cancellationToken,
        params string[] arguments)
    {
        var psi = new ProcessStartInfo("git")
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
        return new GitResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }
}
