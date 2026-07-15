using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Fuse.Cli.Services;

/// <summary>
///     The result of health-checking an upgraded Fuse binary (R33): whether it starts and reports a version, so
///     an in-session upgrade only switches to a binary that works and rolls back otherwise.
/// </summary>
/// <param name="Healthy">Whether the binary started and reported a version.</param>
/// <param name="Detail">A short human-readable reason.</param>
/// <param name="ReportedVersion">The version the binary reported, when healthy.</param>
public sealed record UpgradeHealthResult(bool Healthy, string Detail, string? ReportedVersion);

/// <summary>
///     Health-checks an upgraded Fuse binary before the client is pointed at it (R33). A staged switch runs this
///     gate: on a pass the new version serves; on a fail the previous version is kept and the failure is warned.
///     The detached updater script embeds the same check (start + <c>--version</c>) in shell; this is the
///     in-process gate used to decide rollback and to make the gate unit-testable.
/// </summary>
public static partial class UpgradeHealthCheck
{
    [GeneratedRegex(@"\d+\.\d+\.\d+")]
    private static partial Regex VersionPattern();

    /// <summary>
    ///     Runs <c>dotnet {fuseDllPath} --version</c> and reports whether the binary is healthy: it exists, starts,
    ///     exits zero, and prints a version-like token. Bounded by a short timeout so a hung binary is unhealthy,
    ///     not a hang.
    /// </summary>
    /// <param name="fuseDllPath">The absolute path to the built <c>fuse.dll</c> (or apphost) to check.</param>
    /// <param name="cancellationToken">A token to cancel the check.</param>
    /// <returns>The health result.</returns>
    public static async Task<UpgradeHealthResult> CheckAsync(string fuseDllPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(fuseDllPath) || !File.Exists(fuseDllPath))
            return new UpgradeHealthResult(false, $"binary not found: {fuseDllPath}", null);

        var isDll = fuseDllPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        var startInfo = new ProcessStartInfo
        {
            FileName = isDll ? "dotnet" : fuseDllPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (isDll)
            startInfo.ArgumentList.Add(fuseDllPath);
        startInfo.ArgumentList.Add("--version");

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
                return new UpgradeHealthResult(false, "could not start the binary", null);

            var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token);

            var match = VersionPattern().Match(stdout);
            if (process.ExitCode == 0 && match.Success)
                return new UpgradeHealthResult(true, "started and reported a version", match.Value);

            return new UpgradeHealthResult(false, $"exit {process.ExitCode}, no version reported", null);
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new UpgradeHealthResult(false, $"health check failed: {ex.Message}", null);
        }
    }

    /// <summary>Whether an upgrade should roll back to the previous version, given the new binary's health result.</summary>
    /// <param name="result">The health result of the upgraded binary.</param>
    /// <returns><see langword="true" /> when the upgrade should roll back (the new binary is unhealthy).</returns>
    public static bool ShouldRollBack(UpgradeHealthResult result) => !result.Healthy;
}
