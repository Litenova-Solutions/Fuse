using System.Diagnostics;
using System.Text;

namespace Fuse.Semantics.Remediation;

/// <summary>
///     The outcome of applying an environment remediation (C1): whether the action succeeded and the tool output
///     it produced, so the caller can report and decide whether to re-attempt the oracle tier.
/// </summary>
/// <param name="Success">Whether the remediation command exited zero.</param>
/// <param name="TimedOut">Whether the command exceeded the timeout and was killed.</param>
/// <param name="Output">The combined stdout and stderr of the command, for the report.</param>
public sealed record RemediationApplyResult(bool Success, bool TimedOut, string Output);

/// <summary>
///     Applies the install-free environment remediations (C1): running <c>dotnet restore</c> with an overlay NuGet
///     configuration passed by <c>--configfile</c>, so a Central Package Management source-mapping failure (NU1507)
///     is fixed without editing the repository and without installing anything. Consent-gated remedies (SDK or
///     workload installs) are not applied here; they require the caller's explicit consent flag and a separate path,
///     so this applier is safe to run unattended (it downloads packages restore already would, and writes nothing
///     into the repository).
/// </summary>
/// <remarks>
///     The restore argument list is fixed and bounded (target plus the overlay config path), never a
///     variable-length command line, per the external-process change-safety invariant.
/// </remarks>
public sealed class EnvironmentRemediationApplier
{
    private readonly TimeSpan _timeout;

    /// <summary>
    ///     Initializes a new instance of the <see cref="EnvironmentRemediationApplier" /> class.
    /// </summary>
    /// <param name="timeout">The maximum time a single remediation command may run before it is killed.</param>
    public EnvironmentRemediationApplier(TimeSpan timeout) => _timeout = timeout;

    /// <summary>
    ///     Runs <c>dotnet restore</c> for a target (a directory, solution, or project) with an overlay NuGet
    ///     configuration, the NU1507 remedy. The overlay is passed explicitly and never written into the repository.
    /// </summary>
    /// <param name="target">The absolute path to the directory, solution, or project to restore.</param>
    /// <param name="overlayConfigPath">The absolute path to the overlay <c>NuGet.config</c> (a temp file).</param>
    /// <param name="cancellationToken">A token to cancel the restore.</param>
    /// <returns>The apply result: success, timed-out, and the restore output.</returns>
    public async Task<RemediationApplyResult> ApplyOverlayRestoreAsync(
        string target, string overlayConfigPath, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Directory.Exists(target) ? target : Path.GetDirectoryName(target)!,
        };
        psi.ArgumentList.Add("restore");
        psi.ArgumentList.Add(target);
        psi.ArgumentList.Add("--configfile");
        psi.ArgumentList.Add(overlayConfigPath);
        psi.ArgumentList.Add("-nologo");

        using var process = new Process { StartInfo = psi };
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
        process.Start();
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
            return new RemediationApplyResult(false, true, output.ToString());
        }

        return new RemediationApplyResult(process.ExitCode == 0, false, output.ToString());
    }
}
