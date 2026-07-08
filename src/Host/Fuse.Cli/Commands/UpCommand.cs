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

        _consoleUI.WriteStep($"Planning environment remediation for {root}");
        var diagnosis = await _indexer.DiagnoseLoadAsync(root, context.CancellationToken);
        var plan = new EnvironmentRemediationPlanner().Plan(diagnosis);
        _consoleUI.WriteResult(RemediationReport.Render(plan));
    }
}
