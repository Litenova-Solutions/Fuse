using System.Diagnostics;
using DotMake.CommandLine;
using Fuse.Cli.Services;
using Fuse.Indexing;

namespace Fuse.Cli.Commands;

/// <summary>
///     Updates the Fuse global tool, owning the process-lock choreography a bare <c>dotnet tool update</c>
///     cannot: it stops the other running Fuse hosts, then hands the update to a detached script that waits
///     for this process to exit before replacing the tool files.
/// </summary>
/// <remarks>
///     A running .NET global tool locks its own files (on Windows), so it cannot update itself in-process.
///     This command stops the long-lived Fuse hosts (for example the one an editor extension spawns) that
///     would hold the lock, writes a small platform-native updater via <see cref="ToolUpdatePlanner" />, and
///     launches it detached so the update runs once this short-lived CLI process exits. Reload the editor
///     window afterward so its MCP client re-handshakes with the new tool surface.
/// </remarks>
[CliCommand(
    Name = "update",
    Description = "Update the Fuse global tool (stops running hosts, then updates once this process exits).",
    ShortFormAutoGenerate = CliNameAutoGenerate.None,
    Parent = typeof(FuseCliCommand))]
public sealed class UpdateCommand
{
    private readonly IConsoleUI _consoleUI;

    /// <summary>
    ///     Initializes a new instance of the <see cref="UpdateCommand" /> class for CLI option binding only.
    /// </summary>
    /// <remarks>Used by DotMake.CommandLine to bind options; the console UI is null, so this instance must not run.</remarks>
    public UpdateCommand() : this(null!)
    {
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="UpdateCommand" /> class.
    /// </summary>
    /// <param name="consoleUI">The console UI for status output.</param>
    public UpdateCommand(IConsoleUI consoleUI) => _consoleUI = consoleUI;

    /// <summary>The exact version to install. Defaults to the latest stable on NuGet.</summary>
    /// <remarks>Named <c>to-version</c> rather than <c>version</c> so it does not shadow the global <c>--version</c>.</remarks>
    [CliOption(Name = "to-version", Required = false, Description = "The exact version to install (default: latest stable).")]
    public string? TargetVersion { get; set; }

    /// <summary>
    ///     Runs the update command: stops other Fuse hosts and launches the detached updater.
    /// </summary>
    /// <param name="context">The CLI invocation context supplying the cancellation token.</param>
    /// <returns>A task that completes once the updater has been launched.</returns>
    public Task RunAsync(CliContext context)
    {
        _consoleUI.WriteStep($"Current Fuse version: {FuseBuildInfo.Current}");

        StopOtherFuseProcesses();

        var arguments = ToolUpdatePlanner.BuildDotnetArguments(TargetVersion);
        var isWindows = OperatingSystem.IsWindows();
        var workDirectory = Path.Combine(Path.GetTempPath(), "fuse-update");
        Directory.CreateDirectory(workDirectory);
        var scriptPath = Path.Combine(workDirectory, isWindows ? "update.ps1" : "update.sh");
        var logPath = Path.Combine(workDirectory, "update.log");
        var script = ToolUpdatePlanner.BuildUpdaterScript(isWindows, Environment.ProcessId, arguments, logPath);
        File.WriteAllText(scriptPath, script);

        try
        {
            LaunchDetached(scriptPath, isWindows);
        }
        catch (Exception ex)
        {
            // Falling back to a manual instruction is better than a stack trace: the user can still update by hand.
            _consoleUI.WriteError($"Could not launch the updater ({ex.Message}). Run 'dotnet {arguments}' from a plain terminal with no Fuse process running.");
            return Task.CompletedTask;
        }

        _consoleUI.WriteSuccess("Update launched. It runs once this process exits.");
        _consoleUI.WriteStep($"Command: dotnet {arguments}");
        _consoleUI.WriteStep($"Log: {logPath}");
        _consoleUI.WriteStep("Reload your editor window afterward so its MCP client picks up the new tool surface.");
        return Task.CompletedTask;
    }

    // Stop the other running Fuse processes (the long-lived hosts an editor spawns) so they release their file
    // locks before the update. This process is excluded: the updater waits for it to exit on its own.
    private void StopOtherFuseProcesses()
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
                _consoleUI.WriteStep($"Stopped running Fuse host (pid {process.Id}).");
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
