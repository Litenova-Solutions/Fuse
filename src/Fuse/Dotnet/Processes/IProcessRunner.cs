namespace Fuse.Dotnet.Processes;

/// <summary>
///     The port Fuse runs child processes through: a file name and an argument list, never a shell string, with the
///     whole process tree killed on cancellation or inactivity. <see cref="ProcessRunner.Default"/> runs real processes;
///     a test passes its own runner to a type that takes one.
/// </summary>
internal interface IProcessRunner
{
    /// <summary>Runs a process to completion and captures its output.</summary>
    /// <param name="fileName">The program to run.</param>
    /// <param name="arguments">Arguments, passed without shell interpretation.</param>
    /// <param name="workingDirectory">The directory the process runs in.</param>
    /// <param name="inactivityTimeout">How long the process may write no output before its tree is killed, or null to wait without a timeout.</param>
    /// <param name="cancellationToken">Kills the process tree when cancelled.</param>
    Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, string workingDirectory, TimeSpan? inactivityTimeout, CancellationToken cancellationToken);
}
