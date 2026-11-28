namespace Fuse.Dotnet.Processes;

/// <summary>Result of a finished child process.</summary>
/// <param name="ExitCode">The process exit code, or -1 when it could not start.</param>
/// <param name="Output">Standard output and standard error, interleaved in arrival order.</param>
internal sealed record ProcessResult(int ExitCode, string Output)
{
    /// <summary>True when the process was killed after writing no output for the caller's inactivity timeout.</summary>
    public bool TimedOut { get; init; }
}
