using System.Diagnostics;

namespace Fuse.Cli.Services;

/// <summary>
///     The outcome of launching the detached tool updater.
/// </summary>
/// <param name="Launched">Whether the detached updater process started.</param>
/// <param name="DotnetArguments">The <c>dotnet</c> arguments the updater runs, for messaging or a manual fallback.</param>
/// <param name="LogPath">The path the updater writes its combined output to.</param>
/// <param name="Error">The launch failure message when <paramref name="Launched" /> is false; otherwise null.</param>
public sealed record ToolUpdateLaunch(bool Launched, string DotnetArguments, string LogPath, string? Error);

/// <summary>
///     Launches the detached updater that upgrades the Fuse global tool once the current process exits, working
///     around the fact that a running .NET tool locks its own files on Windows. Shared by the explicit
///     <c>fuse update</c> command and the opt-in background auto-update.
/// </summary>
public sealed class ToolUpdateLauncher
{
    /// <summary>
    ///     Writes and starts the detached updater.
    /// </summary>
    /// <param name="version">The exact version to install, or null for the latest stable.</param>
    /// <param name="stopOtherHosts">
    ///     When true, other running Fuse processes are killed first so they release their file locks (the explicit
    ///     <c>fuse update</c> path). When false, siblings are left running (the background auto-update path, which
    ///     must not disrupt other sessions); the update then succeeds only once those processes exit on their own.
    /// </param>
    /// <param name="onHostStopped">An optional callback invoked with a message for each host stopped.</param>
    /// <returns>The launch outcome, including the log path and the command for a manual fallback.</returns>
    public ToolUpdateLaunch Launch(string? version, bool stopOtherHosts, Action<string>? onHostStopped = null)
    {
        if (stopOtherHosts)
            StopOtherFuseProcesses(onHostStopped);

        var arguments = ToolUpdatePlanner.BuildDotnetArguments(version);
        var isWindows = OperatingSystem.IsWindows();
        var workDirectory = Path.Combine(Path.GetTempPath(), "fuse-update");
        var logPath = Path.Combine(workDirectory, "update.log");
        try
        {
            Directory.CreateDirectory(workDirectory);
            var scriptPath = Path.Combine(workDirectory, isWindows ? "update.ps1" : "update.sh");
            File.WriteAllText(scriptPath, ToolUpdatePlanner.BuildUpdaterScript(isWindows, Environment.ProcessId, arguments, logPath));
            LaunchDetached(scriptPath, isWindows);
        }
        catch (Exception ex)
        {
            return new ToolUpdateLaunch(false, arguments, logPath, ex.Message);
        }

        return new ToolUpdateLaunch(true, arguments, logPath, null);
    }

    // Stop the other running Fuse processes so they release their file locks before the update. This process is
    // excluded: the updater waits for it to exit on its own.
    private static void StopOtherFuseProcesses(Action<string>? onHostStopped)
    {
        var selfId = Environment.ProcessId;
        Process[] others;
        try
        {
            others = Process.GetProcessesByName("fuse").Where(p => p.Id != selfId).ToArray();
        }
        catch (Exception)
        {
            // Process enumeration can fail under restricted environments; the update can still proceed.
            return;
        }

        foreach (var process in others)
        {
            try
            {
                process.Kill(entireProcessTree: true);
                onHostStopped?.Invoke($"Stopped running Fuse host (pid {process.Id}).");
            }
            catch (Exception)
            {
                // A host that already exited or that we cannot stop is not fatal; the updater still tries.
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    // Launch the updater so it outlives this process. On Windows, PowerShell runs the .ps1 hidden; on POSIX,
    // /bin/sh runs the script and the child continues after the parent exits.
    private static void LaunchDetached(string scriptPath, bool isWindows)
    {
        var startInfo = isWindows
            ? new ProcessStartInfo("powershell", $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"")
            {
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            }
            : new ProcessStartInfo("/bin/sh", $"\"{scriptPath}\"")
            {
                UseShellExecute = false,
            };

        Process.Start(startInfo);
    }
}
