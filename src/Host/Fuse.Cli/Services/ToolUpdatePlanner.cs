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
              // R33: capture the previous version first, health-check the new binary (it starts and reports a
              // version), and roll back to the previous version on failure, so an unattended upgrade never bricks
              // the tool. A healthy switch logs a reindex-scheduled signal (R23 rebuilds on the next open).
              $$"""
              $ErrorActionPreference = 'Continue'
              $target = {{waitForProcessId}}
              while (Get-Process -Id $target -ErrorAction SilentlyContinue) { Start-Sleep -Milliseconds 300 }
              $prev = (dotnet tool list --global | Select-String -Pattern '(?i)^{{PackageId}}\s+(\S+)' | ForEach-Object { $_.Matches[0].Groups[1].Value } | Select-Object -First 1)
              & dotnet {{dotnetArguments}} *> "{{logPath}}"
              $healthy = $false
              try { $v = & fuse --version 2>&1; if ($LASTEXITCODE -eq 0 -and $v) { $healthy = $true } } catch { $healthy = $false }
              if ($healthy) { "fuse updated to $v; reindex scheduled on next open" | Out-File -Append -FilePath "{{logPath}}" }
              elseif ($prev) { "health check failed; rolling back to $prev" | Out-File -Append -FilePath "{{logPath}}"; & dotnet tool update --global {{PackageId}} --version $prev *>> "{{logPath}}" }
              else { "health check failed and no previous version to roll back to" | Out-File -Append -FilePath "{{logPath}}" }
              """
            : $$"""
              #!/bin/sh
              while kill -0 {{waitForProcessId}} 2>/dev/null; do sleep 0.3; done
              prev=$(dotnet tool list --global | grep -i '^{{PackageId}} ' | awk '{ print $2 }' | head -1)
              dotnet {{dotnetArguments}} > "{{logPath}}" 2>&1
              if fuse --version >> "{{logPath}}" 2>&1; then
                echo "fuse updated; reindex scheduled on next open" >> "{{logPath}}"
              elif [ -n "$prev" ]; then
                echo "health check failed; rolling back to $prev" >> "{{logPath}}"
                dotnet tool update --global {{PackageId}} --version "$prev" >> "{{logPath}}" 2>&1
              else
                echo "health check failed and no previous version to roll back to" >> "{{logPath}}"
              fi
              """;
}
