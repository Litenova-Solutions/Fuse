using System.Diagnostics;
using System.Text;

namespace Fuse.Semantics.Remediation;

/// <summary>
///     The outcome of a tier-1 build probe (C1): whether a real <c>dotnet build</c> of the workspace succeeded, and,
///     when it failed, the knowledge-base signature that classifies the failure (matched against the build output).
/// </summary>
/// <remarks>
///     Tier-1 (build-capture) oracle grade requires a successful <c>dotnet build</c>, because the binary log a
///     successful build emits is what rehydrates exact compilations. The design-time MSBuildWorkspace load
///     (<c>fuse doctor</c>) can succeed while the build fails (a restore-only failure like NU1507, an SDK-band or
///     workload gap), so the load tier alone does not tell an agent whether the oracle is reachable. This probe
///     answers that question directly and names the blocker.
/// </remarks>
/// <param name="Attempted">Whether the probe ran a build (false when no build target was found).</param>
/// <param name="Succeeded">Whether <c>dotnet build</c> exited zero (tier-1 achievable).</param>
/// <param name="TimedOut">Whether the build exceeded the timeout and was killed.</param>
/// <param name="Blocker">The classified failure signature when the build failed, or null (succeeded, or unrecognized failure).</param>
/// <param name="Output">The combined build output (for the report tail and for classifying).</param>
public sealed record BuildProbeResult(
    bool Attempted,
    bool Succeeded,
    bool TimedOut,
    RemediationSignature? Blocker,
    string Output);

/// <summary>
///     Probes whether a workspace reaches tier-1 (build-capture) oracle grade by running a real <c>dotnet build</c>
///     and, on failure, classifying the build output against the remediation knowledge base (C1). This is the
///     restore/build-output signal the knowledge-base regexes are written for; it surfaces the failure classes
///     (NU1507 source mapping, NETSDK1045 SDK band, MSB4018 workload) that the design-time load does not.
/// </summary>
/// <remarks>
///     The build argument list is fixed and bounded (target plus fixed flags, and optionally one overlay config
///     path), never a variable-length command line, per the external-process change-safety invariant. The probe
///     never writes into the repository: an overlay NuGet config, when supplied, is a temp file passed by
///     <c>--configfile</c>.
/// </remarks>
public sealed class TierOneBuildProbe
{
    private readonly TimeSpan _timeout;
    private readonly RemediationKnowledgeBase _knowledgeBase;

    /// <summary>
    ///     Initializes a new instance of the <see cref="TierOneBuildProbe" /> class.
    /// </summary>
    /// <param name="timeout">The maximum time the build may run before it is killed.</param>
    /// <param name="knowledgeBase">The knowledge base to classify a build failure against; defaults to the shipped default.</param>
    public TierOneBuildProbe(TimeSpan timeout, RemediationKnowledgeBase? knowledgeBase = null)
    {
        _timeout = timeout;
        _knowledgeBase = knowledgeBase ?? RemediationKnowledgeBase.LoadDefault();
    }

    /// <summary>
    ///     Runs a tier-1 build probe of the workspace: discovers the build target (solution, else the first
    ///     project), runs <c>dotnet build -c Release</c>, and classifies the output on failure.
    /// </summary>
    /// <param name="rootDirectory">The workspace root to probe.</param>
    /// <param name="overlayConfigPath">An optional overlay <c>NuGet.config</c> to pass by <c>--configfile</c> (the NU1507 remedy for the re-probe), or null.</param>
    /// <param name="cancellationToken">A token to cancel the probe.</param>
    /// <returns>The probe result: whether the build succeeded and, on failure, the classified blocker.</returns>
    public async Task<BuildProbeResult> ProbeAsync(
        string rootDirectory, string? overlayConfigPath, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(rootDirectory);
        var discovery = await new DotNetWorkspaceDiscoverer().DiscoverAsync(root, cancellationToken);
        var target = discovery.SolutionPath ?? discovery.ProjectPaths.FirstOrDefault();
        if (target is null)
            return new BuildProbeResult(Attempted: false, Succeeded: false, TimedOut: false, Blocker: null, Output: string.Empty);

        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Directory.Exists(target) ? target : Path.GetDirectoryName(target)!,
        };
        psi.ArgumentList.Add("build");
        psi.ArgumentList.Add(target);
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("Release");
        psi.ArgumentList.Add("-nologo");
        // The overlay NuGet config is applied to the build's implicit restore via the RestoreConfigFile MSBuild
        // property (dotnet build has no --configfile; that flag is dotnet restore's). This supplies the NU1507
        // source mapping for the re-probe without editing the repo.
        if (overlayConfigPath is not null)
            psi.ArgumentList.Add($"-p:RestoreConfigFile={overlayConfigPath}");

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
            // The toolchain is not on PATH: the probe could not run, reported as not attempted rather than a failure.
            return new BuildProbeResult(Attempted: false, Succeeded: false, TimedOut: false, Blocker: null, Output: string.Empty);
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
            return new BuildProbeResult(Attempted: true, Succeeded: false, TimedOut: true, Blocker: null, Output: output.ToString());
        }

        var text = output.ToString();
        if (process.ExitCode == 0)
            return new BuildProbeResult(Attempted: true, Succeeded: true, TimedOut: false, Blocker: null, Output: text);

        // A failed build: classify the output against the knowledge base to name the blocker (NU1507, NETSDK1045,
        // MSB4018, or a classify-only compile error); a null blocker means the failure is unrecognized.
        var blocker = _knowledgeBase.Match(text);
        return new BuildProbeResult(Attempted: true, Succeeded: false, TimedOut: false, Blocker: blocker, Output: text);
    }
}
