using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Fuse.Semantics.Remediation;

/// <summary>
///     The outcome of a consent-gated install remediation (C1): whether the install succeeded, and, for an SDK-band
///     install, the path to the <c>dotnet</c> executable of the freshly installed SDK so the tier-1 re-probe can use
///     it instead of the system default.
/// </summary>
/// <param name="Attempted">Whether the install ran (false when the prerequisite was missing, for example no pinned band).</param>
/// <param name="Success">Whether the install command exited zero.</param>
/// <param name="TimedOut">Whether the install exceeded the timeout and was killed.</param>
/// <param name="DotnetPath">For an SDK-band install: the installed <c>dotnet</c> executable to re-probe with, or null.</param>
/// <param name="Output">The combined install output, for the report.</param>
public sealed record InstallApplyResult(
    bool Attempted,
    bool Success,
    bool TimedOut,
    string? DotnetPath,
    string Output);

/// <summary>
///     Applies the consent-gated SDK-band install remediation (C1): installing the SDK band a repository pins in
///     <c>global.json</c> (the NETSDK1045 remedy) via the official dotnet-install script into an isolated directory.
///     This changes the machine, so it runs only behind <c>--allow-install</c> (Decision D17); the caller gates on
///     that flag and records the install. The MSB4018 workload class is not auto-installed: the workload id is not
///     reliably derivable from the diagnostic (a repository-custom task looks the same), so <c>fuse up</c> reports
///     it with the safe <c>dotnet workload restore</c> step rather than guessing a workload to install.
/// </summary>
/// <remarks>
///     The SDK install targets an isolated directory (never the machine-wide SDK) and returns that install's
///     <c>dotnet</c> executable, so the tier-1 re-probe builds with the installed band without a global side effect.
///     Argument lists are fixed and bounded per the external-process change-safety invariant. Each command is
///     time-bounded and killed on timeout.
/// </remarks>
public sealed class InstallRemediationApplier
{
    private const string InstallScriptUrlWindows = "https://dot.net/v1/dotnet-install.ps1";
    private const string InstallScriptUrlPosix = "https://dot.net/v1/dotnet-install.sh";

    private readonly TimeSpan _timeout;

    /// <summary>
    ///     Initializes a new instance of the <see cref="InstallRemediationApplier" /> class.
    /// </summary>
    /// <param name="timeout">The maximum time a single install command may run before it is killed.</param>
    public InstallRemediationApplier(TimeSpan timeout) => _timeout = timeout;

    /// <summary>
    ///     Reads the SDK band a repository pins in the nearest <c>global.json</c> (the <c>sdk.version</c> value).
    /// </summary>
    /// <param name="rootDirectory">The workspace root to search from.</param>
    /// <returns>The pinned band (for example <c>7.0.100</c>), or null when no <c>global.json</c> pins one.</returns>
    public static string? TryReadPinnedSdkBand(string rootDirectory)
    {
        var configPath = FindNearestGlobalJson(Path.GetFullPath(rootDirectory));
        if (configPath is null)
            return null;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(configPath));
            if (document.RootElement.TryGetProperty("sdk", out var sdk)
                && sdk.TryGetProperty("version", out var version)
                && version.ValueKind == JsonValueKind.String)
            {
                var value = version.GetString();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }

        return null;
    }

    /// <summary>
    ///     Installs an SDK band via the official dotnet-install script into an isolated directory (the NETSDK1045
    ///     remedy), returning the installed <c>dotnet</c> executable for the tier-1 re-probe. Machine-changing:
    ///     the caller must have the user's consent (<c>--allow-install</c>).
    /// </summary>
    /// <param name="band">The SDK band to install (the <c>global.json</c> <c>sdk.version</c>).</param>
    /// <param name="installDirectory">The isolated directory to install into (never the machine-wide SDK location).</param>
    /// <param name="cancellationToken">A token to cancel the install.</param>
    /// <returns>The install result, including the installed <c>dotnet</c> path on success.</returns>
    public async Task<InstallApplyResult> InstallSdkAsync(
        string band, string installDirectory, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(band))
            return new InstallApplyResult(Attempted: false, Success: false, TimedOut: false, DotnetPath: null, Output: string.Empty);

        Directory.CreateDirectory(installDirectory);

        // Fetch the official install script to a temp file, then run it to place the SDK in the isolated directory.
        // A specific band uses -Version; the script resolves the exact patch. Windows uses the PowerShell script,
        // other OSes the shell script.
        var scriptResult = await DownloadInstallScriptAsync(cancellationToken);
        if (scriptResult is not { } scriptPath)
            return new InstallApplyResult(Attempted: false, Success: false, TimedOut: false, DotnetPath: null, Output: "could not download the dotnet-install script (offline).");

        var (fileName, argumentList) = BuildInstallInvocation(scriptPath, band, installDirectory);
        var run = await RunAsync(fileName, argumentList, installDirectory, cancellationToken);
        if (run.TimedOut)
            return new InstallApplyResult(Attempted: true, Success: false, TimedOut: true, DotnetPath: null, Output: run.Output);

        var dotnetPath = Path.Combine(installDirectory, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        var success = run.ExitCode == 0 && File.Exists(dotnetPath);
        return new InstallApplyResult(Attempted: true, Success: success, TimedOut: false, DotnetPath: success ? dotnetPath : null, Output: run.Output);
    }

    // Builds the dotnet-install invocation for the current OS: PowerShell for the .ps1 on Windows, sh for the .sh
    // elsewhere. The install directory and band are passed as bounded arguments.
    private static (string FileName, IReadOnlyList<string> Arguments) BuildInstallInvocation(
        string scriptPath, string band, string installDirectory)
    {
        if (OperatingSystem.IsWindows())
            return ("pwsh", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath, "-Version", band, "-InstallDir", installDirectory]);
        return ("bash", [scriptPath, "--version", band, "--install-dir", installDirectory]);
    }

    private static async Task<string?> DownloadInstallScriptAsync(CancellationToken cancellationToken)
    {
        var url = OperatingSystem.IsWindows() ? InstallScriptUrlWindows : InstallScriptUrlPosix;
        var extension = OperatingSystem.IsWindows() ? ".ps1" : ".sh";
        var path = Path.Combine(Path.GetTempPath(), $"fuse-dotnet-install-{Guid.NewGuid():N}{extension}");
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(1) };
            var content = await client.GetStringAsync(url, cancellationToken);
            await File.WriteAllTextAsync(path, content, cancellationToken);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return path;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return null;
        }
    }

    private async Task<(int ExitCode, bool TimedOut, string Output)> RunAsync(
        string fileName, IReadOnlyList<string> arguments, string? workingDirectory, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (workingDirectory is not null)
            psi.WorkingDirectory = workingDirectory;
        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };

        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (-1, false, $"could not start '{fileName}' (not on PATH).");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return (-1, true, output.ToString());
        }

        return (process.ExitCode, false, output.ToString());
    }

    private static string? FindNearestGlobalJson(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "global.json");
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        return null;
    }
}
