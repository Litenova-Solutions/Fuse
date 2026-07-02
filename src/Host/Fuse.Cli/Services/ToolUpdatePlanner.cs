namespace Fuse.Cli.Services;

/// <summary>
///     Builds the command line and the detached updater script that <c>fuse update</c> uses to update the
///     global tool after the running process exits.
/// </summary>
/// <remarks>
///     A .NET global tool cannot replace its own files while it is running: on Windows the running executable
///     holds a lock, so <c>dotnet tool update</c> fails to uninstall the current version. The fix is to hand
///     the update to a short detached script that first waits for the launching Fuse process to exit, then
///     runs the update. These builders are pure so the exact command line and script are unit-tested without
///     spawning anything.
/// </remarks>
public static class ToolUpdatePlanner
{
    /// <summary>The NuGet package id of the Fuse global tool.</summary>
    public const string PackageId = "Fuse";

    /// <summary>
    ///     Builds the <c>dotnet</c> arguments that update the global tool, pinned to <paramref name="version" />
    ///     when given or to the latest stable otherwise.
    /// </summary>
    /// <param name="version">An explicit version to install, or null/empty for the latest stable.</param>
    /// <returns>The argument string for a <c>dotnet</c> invocation.</returns>
    public static string BuildDotnetArguments(string? version) =>
        string.IsNullOrWhiteSpace(version)
            ? $"tool update --global {PackageId}"
            : $"tool update --global {PackageId} --version {version.Trim()}";

    /// <summary>
    ///     Builds a platform-native updater script that waits for the launching Fuse process to exit (so its
    ///     files unlock), then runs the update and captures output to a log.
    /// </summary>
    /// <param name="isWindows">True to emit a PowerShell script; false to emit a POSIX shell script.</param>
    /// <param name="waitForProcessId">The Fuse process id to wait for before updating.</param>
    /// <param name="dotnetArguments">The <c>dotnet</c> arguments from <see cref="BuildDotnetArguments" />.</param>
    /// <param name="logPath">The absolute path the updater writes its combined output to.</param>
    /// <returns>The script text.</returns>
    public static string BuildUpdaterScript(bool isWindows, int waitForProcessId, string dotnetArguments, string logPath) =>
        isWindows
            ? // $pid is a PowerShell automatic variable (the current process id), so the target pid uses its own
              // name. Uses $$ so literal PowerShell braces stay single and only {{...}} interpolates.
              $$"""
              $ErrorActionPreference = 'Continue'
              $target = {{waitForProcessId}}
              while (Get-Process -Id $target -ErrorAction SilentlyContinue) { Start-Sleep -Milliseconds 300 }
              & dotnet {{dotnetArguments}} *> "{{logPath}}"
              """
            : $$"""
              #!/bin/sh
              while kill -0 {{waitForProcessId}} 2>/dev/null; do sleep 0.3; done
              dotnet {{dotnetArguments}} > "{{logPath}}" 2>&1
              """;
}
