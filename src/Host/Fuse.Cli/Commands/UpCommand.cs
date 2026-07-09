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

        // For the NU1507 remedy (Central Package Management with no source mapping) the fix is an overlay
        // NuGet.config, which installs nothing and is never written into the repository. Generate it now to a
        // temp file so the concrete remedy is in hand: pass it to restore/build with --configfile. (Auto-applying
        // it through the index/build pipeline and re-attempting tier-1 is the C1 apply step; this hands back the
        // ready-to-use overlay without touching the repo.)
        var hasOverlayRemedy = plan.Remediable.Any(i => i.Signature?.Remedy == "overlay-nuget-source-mapping");
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
        // consolidates. Emitted last so it is the sole output on the result channel when --json is set.
        if (Json)
        {
            var report = new UpResult(
                root,
                applied,
                UpRepoReport.From(plan),
                afterPlan is null ? null : UpRepoReport.From(afterPlan));
            _consoleUI.WriteResult(UpReportJson.Serialize(report));
        }
    }

    // The last few lines of a tool output, so an error report shows the actionable tail without flooding the console.
    private static string Tail(string output, int lines = 8)
    {
        var all = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join("\n", all.TakeLast(lines));
    }
}
