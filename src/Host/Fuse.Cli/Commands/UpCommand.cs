using DotMake.CommandLine;
using Fuse.Cli.Services;
using Fuse.Semantics;
using Fuse.Semantics.Remediation;

namespace Fuse.Cli.Commands;

/// <summary>
///     Reports the environment-remediation plan for a workspace (C1): runs the same load diagnosis as
///     <c>fuse doctor</c>, classifies each downgraded project's failure against the knowledge base, and prints
///     which remedy (if any) would address it plus the workable-subset line. This first cut is report-only: it
///     applies no remedy and never touches the repository (the C1 hard rule). Applying the remedies (the overlay
///     NuGet config for NU1507, and the consent-gated SDK and workload installs) and re-attempting tier-1 is the
///     next step; until then this names what <c>fuse up</c> would do so a user or agent can act.
/// </summary>
/// <remarks>
///     The remedy classification and the workable-subset summary come from <see cref="EnvironmentRemediationPlanner" />
///     over the knowledge base; the report is rendered by <see cref="RemediationReport" />. Repository-code
///     failures (for example an ambiguous type) are named as not fixable by <c>fuse up</c>, never edited.
/// </remarks>
[CliCommand(
    Name = "up",
    Description = "Report the environment-remediation plan: per-project failures, the remedy that would fix each, and the workable subset. Report-only for now; applies nothing and never edits the repository.",
    ShortFormAutoGenerate = CliNameAutoGenerate.None,
    Parent = typeof(FuseCliCommand))]
public sealed class UpCommand
{
    private readonly SemanticIndexer _indexer;
    private readonly IConsoleUI _consoleUI;

    /// <summary>
    ///     Initializes a new instance of the <see cref="UpCommand" /> class for CLI option binding only.
    /// </summary>
    /// <remarks>Used by DotMake.CommandLine to bind options; the dependencies are null, so this instance must not run.</remarks>
    public UpCommand() : this(null!, null!)
    {
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="UpCommand" /> class.
    /// </summary>
    /// <param name="indexer">The semantic indexer, used to diagnose the load.</param>
    /// <param name="consoleUI">The console UI for output.</param>
    public UpCommand(SemanticIndexer indexer, IConsoleUI consoleUI)
    {
        _indexer = indexer;
        _consoleUI = consoleUI;
    }

    /// <summary>The workspace directory. Defaults to the current directory.</summary>
    [CliArgument(Description = "The workspace directory. Defaults to the current directory.")]
    public string Path { get; set; } = ".";

    /// <summary>Apply the install-free environment remedies (the NU1507 overlay restore) and re-attempt the load.</summary>
    [CliOption(Name = "--apply", Description = "Apply the install-free remedies (the NU1507 overlay restore) and re-attempt the load. Consent-gated installs still require --allow-install and are only reported. Never edits the repository.")]
    public bool Apply { get; set; }

    /// <summary>Consent to machine-changing installs (SDK, workload). Off by default; the installs are reported, not run, without it.</summary>
    [CliOption(Name = "--allow-install", Description = "Consent to the machine-changing install remedies (SDK, workload). Off by default; without it those remedies are reported, never run.")]
    public bool AllowInstall { get; set; }

    /// <summary>Emit the machine-readable JSON report (the same shape workspace status reads) instead of the text report.</summary>
    [CliOption(Name = "--json", Description = "Emit the machine-readable JSON report (per-project tier, remedy, workable-subset) instead of the human-readable text.")]
    public bool Json { get; set; }

    /// <summary>Run a real dotnet build to probe tier-1 (build-capture) reachability and classify the blocker from the build output.</summary>
    [CliOption(Name = "--probe", Description = "Run a real dotnet build to probe tier-1 (build-capture) reachability. Surfaces restore/build failures (NU1507, NETSDK1045, MSB4018) the design-time load cannot, and classifies the blocker. Slower (a full build); off by default.")]
    public bool Probe { get; set; }

    /// <summary>
    ///     Runs the up command.
    /// </summary>
    /// <param name="context">The CLI invocation context supplying the cancellation token.</param>
    /// <returns>A task that completes when the report has been written.</returns>
    public async Task RunAsync(CliContext context)
    {
        var root = System.IO.Path.GetFullPath(Path);
        if (!Directory.Exists(root))
        {
            _consoleUI.WriteError($"Directory not found: {root}");
            return;
        }

        if (!Json)
            _consoleUI.WriteStep($"Planning environment remediation for {root}");
        var diagnosis = await _indexer.DiagnoseLoadAsync(root, context.CancellationToken);
        var plan = new EnvironmentRemediationPlanner().Plan(diagnosis);
        if (!Json)
            _consoleUI.WriteResult(RemediationReport.Render(plan));

        // Track the after-apply plan so the machine-readable report (and the text "after applying" block) reflects
        // the re-attempted load; null until an install-free remedy is actually applied.
        RemediationPlan? afterPlan = null;
        var applied = false;
        UpBuildProbe? probeBefore = null;
        UpBuildProbe? probeAfter = null;
        var probe = new TierOneBuildProbe(TimeSpan.FromMinutes(10));

        // The tier-1 build probe (C1): the design-time load can succeed while a real dotnet build fails on a
        // restore-only failure (NU1507) or an SDK/workload gap, so tier-1 (build-capture oracle grade) reachability
        // is answered by running the build and classifying its output against the knowledge base. Off unless
        // --probe, because it costs a full build.
        BuildProbeResult? probeResult = null;
        if (Probe)
        {
            if (!Json)
                _consoleUI.WriteStep("Probing tier-1 (dotnet build) reachability");
            probeResult = await probe.ProbeAsync(root, null, context.CancellationToken);
            probeBefore = UpBuildProbe.From(probeResult);
            if (!Json)
                _consoleUI.WriteResult(RenderProbe("tier-1 build probe", probeResult));
        }

        // For the NU1507 remedy (Central Package Management with no source mapping) the fix is an overlay
        // NuGet.config, which installs nothing and is never written into the repository. The blocker is detected by
        // the tier-1 build probe (the design-time load does not surface NU1507) or, as a fallback, by the load-plan
        // classification. Generate the overlay to a temp file (passed via --configfile), never into the repo.
        var probeSaysOverlay = probeResult is { Succeeded: false, Blocker.Remedy: "overlay-nuget-source-mapping" };
        var hasOverlayRemedy = probeSaysOverlay
            || plan.Remediable.Any(i => i.Signature?.Remedy == "overlay-nuget-source-mapping");
        if (hasOverlayRemedy)
        {
            var overlay = NuGetOverlayConfig.Build(NuGetOverlayConfig.ReadSources(root));
            var overlayPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"fuse-nuget-overlay-{Guid.NewGuid():N}.config");
            await File.WriteAllTextAsync(overlayPath, overlay, context.CancellationToken);

            if (!Apply)
            {
                if (!Json)
                    _consoleUI.WriteResult(
                        $"\nNU1507 overlay written to {overlayPath}\n" +
                        $"apply it (installs nothing, never edits the repo):\n" +
                        $"  dotnet restore --configfile \"{overlayPath}\"\n" +
                        $"or re-run with --apply to apply it and re-attempt the load.");
            }
            else
            {
                // Apply the install-free overlay remedy (Hard rule: the overlay is a temp file, never written into
                // the repo) and re-attempt the load once (Do-not: no unbounded retry; a single remediation round).
                if (!Json)
                    _consoleUI.WriteStep("Applying the NU1507 overlay (dotnet restore --configfile) and re-attempting the load");
                var applier = new EnvironmentRemediationApplier(TimeSpan.FromMinutes(5));
                var result = await applier.ApplyOverlayRestoreAsync(root, overlayPath, context.CancellationToken);
                if (result.TimedOut)
                {
                    if (!Json)
                        _consoleUI.WriteError("overlay restore timed out; leaving the plan unchanged.");
                }
                else if (!result.Success)
                {
                    if (!Json)
                        _consoleUI.WriteError($"overlay restore did not succeed; the blocker may be more than NU1507. Restore tail:\n{Tail(result.Output)}");
                }
                else
                {
                    var after = await _indexer.DiagnoseLoadAsync(root, context.CancellationToken);
                    afterPlan = new EnvironmentRemediationPlanner().Plan(after);
                    applied = true;
                    if (!Json)
                    {
                        _consoleUI.WriteResult("\nafter applying the overlay:");
                        _consoleUI.WriteResult(RemediationReport.Render(afterPlan));
                    }

                    // Re-probe tier-1 with the overlay config so the report shows whether the build now reaches
                    // build-capture grade (the overlay supplies the source mapping the build's restore needs).
                    if (Probe)
                    {
                        var reprobe = await probe.ProbeAsync(root, overlayPath, context.CancellationToken);
                        probeAfter = UpBuildProbe.From(reprobe);
                        if (!Json)
                            _consoleUI.WriteResult(RenderProbe("tier-1 build probe (after overlay)", reprobe));
                    }
                }
            }
        }

        // Consent-gated install remedies are reported, never run without --allow-install (Do-not: no auto-install).
        var installRemedies = plan.Remediable
            .Where(i => i.Signature is { RequiresConsent: true })
            .Select(i => i.Signature!.Id)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (installRemedies.Count > 0 && !AllowInstall && !Json)
        {
            _consoleUI.WriteResult(
                $"\nconsent-gated remedies present ({string.Join(", ", installRemedies)}): not run. " +
                "Re-run with --allow-install to permit machine-changing installs.");
        }

        // The machine-readable report: the same shape workspace status reads and the up-report harness (C1 gate)
        // consolidates. Written raw to stdout (not through the decorating console UI) so it pipes as valid JSON;
        // every human line above was guarded by !Json, so stdout carries the JSON document alone.
        if (Json)
        {
            var report = new UpResult(
                root,
                applied,
                UpRepoReport.From(plan),
                afterPlan is null ? null : UpRepoReport.From(afterPlan),
                probeBefore,
                probeAfter);
            Console.Out.WriteLine(UpReportJson.Serialize(report));
        }
    }

    // Renders a tier-1 build-probe result for the human-readable report: whether the build reached tier-1, and the
    // classified blocker (or that the failure is unrecognized) when it did not.
    private static string RenderProbe(string label, BuildProbeResult result)
    {
        if (!result.Attempted)
            return $"{label}: not attempted (no build target, or the toolchain is unavailable).";
        if (result.TimedOut)
            return $"{label}: timed out.";
        if (result.Succeeded)
            return $"{label}: tier-1 reachable (dotnet build succeeded).";
        if (result.Blocker is null)
            return $"{label}: build failed, unrecognized blocker (classify-only). Tail:\n{Tail(result.Output)}";
        var consent = result.Blocker.RequiresConsent ? " (needs --allow-install)" : string.Empty;
        return $"{label}: build failed -> {result.Blocker.Id} {result.Blocker.Title} -> remedy: {result.Blocker.Remedy}{consent}";
    }

    // The last few lines of a tool output, so an error report shows the actionable tail without flooding the console.
    private static string Tail(string output, int lines = 8)
    {
        var all = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join("\n", all.TakeLast(lines));
    }
}
