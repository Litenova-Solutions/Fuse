using Fuse.Cli.Services;

namespace Fuse.Cli.Tests.Services;

// The pure command-line and updater-script builders behind `fuse update`.
public sealed class ToolUpdatePlannerTests
{
    [Fact]
    public void BuildDotnetArguments_NoVersion_UpdatesToLatest()
    {
        Assert.Equal("tool update --global Fuse", ToolUpdatePlanner.BuildDotnetArguments(null));
        Assert.Equal("tool update --global Fuse", ToolUpdatePlanner.BuildDotnetArguments("   "));
    }

    [Fact]
    public void BuildDotnetArguments_WithVersion_PinsIt()
    {
        Assert.Equal("tool update --global Fuse --version 3.2.0", ToolUpdatePlanner.BuildDotnetArguments("3.2.0"));
    }

    [Fact]
    public void BuildUpdaterScript_Windows_WaitsForPidThenUpdates()
    {
        var script = ToolUpdatePlanner.BuildUpdaterScript(
            isWindows: true, waitForProcessId: 4321, dotnetArguments: "tool update --global Fuse", logPath: @"C:\tmp\u.log");

        Assert.Contains("$target = 4321", script);
        Assert.Contains("Get-Process -Id $target", script);
        Assert.Contains("dotnet tool update --global Fuse", script);
        Assert.Contains(@"C:\tmp\u.log", script);
        // $pid is a PowerShell reserved automatic variable; the script must not assign to it.
        Assert.DoesNotContain("$pid =", script);
    }

    [Fact]
    public void BuildUpdaterScript_Posix_WaitsForPidThenUpdates()
    {
        var script = ToolUpdatePlanner.BuildUpdaterScript(
            isWindows: false, waitForProcessId: 4321, dotnetArguments: "tool update --global Fuse", logPath: "/tmp/u.log");

        Assert.StartsWith("#!/bin/sh", script);
        Assert.Contains("kill -0 4321", script);
        Assert.Contains("dotnet tool update --global Fuse", script);
        Assert.Contains("/tmp/u.log", script);
    }
}
