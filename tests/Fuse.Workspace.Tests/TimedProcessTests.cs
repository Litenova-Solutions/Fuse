using System.Diagnostics;
using Fuse.Workspace;
using Xunit;

namespace Fuse.Workspace.Tests;

// T1: the child-process isolation primitive the test micro-host stands on. A quick process runs to completion and
// its output is captured; a process that exceeds the hard timeout is killed near the deadline (not run to
// completion), so a hanging test host cannot wedge the caller.
public sealed class TimedProcessTests
{
    [Fact]
    public async Task Runs_a_quick_process_and_captures_output()
    {
        var result = await TimedProcess.RunAsync(
            "dotnet", ["--version"], workingDirectory: null, environment: null,
            TimeSpan.FromSeconds(60), CancellationToken.None);

        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(result.StandardOutput));
    }

    [Fact]
    public async Task Kills_a_process_that_exceeds_the_timeout()
    {
        // A ~30s sleeper under a 2s timeout must be killed near the deadline, proving the tree-kill fires rather
        // than the process running to completion.
        var (fileName, arguments) = OperatingSystem.IsWindows()
            ? ("ping", new[] { "-n", "30", "127.0.0.1" })
            : ("sleep", new[] { "30" });

        var stopwatch = Stopwatch.StartNew();
        var result = await TimedProcess.RunAsync(
            fileName, arguments, workingDirectory: null, environment: null,
            TimeSpan.FromSeconds(2), CancellationToken.None);
        stopwatch.Stop();

        Assert.True(result.TimedOut);
        Assert.Null(result.ExitCode);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20),
            $"expected a kill near the 2s timeout, but the call took {stopwatch.Elapsed.TotalSeconds:F1}s");
    }
}
