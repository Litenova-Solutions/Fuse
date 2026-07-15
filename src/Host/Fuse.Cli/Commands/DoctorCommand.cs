using System.Text;
using DotMake.CommandLine;
using Fuse.Cli.Services;
using Fuse.Semantics;

namespace Fuse.Cli.Commands;

/// <summary>
///     Diagnoses a workspace's semantic load and reports the achieved tier and the concrete per-project reason
///     for any downgrade (unrestored, SDK mismatch, or build error). Where <c>fuse diagnostics</c> reports the
///     state of an already-written index, <c>fuse doctor</c> actively loads the workspace and explains why the
///     oracle tier was or was not reached, so a user knows whether the compiler-backed tools can answer.
/// </summary>
/// <remarks>
///     The oracle tools (speculative check, impact, refactor) answer only at the oracle-grade tier (every project
///     loaded clean); a project loaded with compile errors is graph-grade (retrieval only), and a project that
///     did not load at all drops the workspace toward syntax. This command names that per project so a coverage
///     gap is visible rather than hidden.
/// </remarks>
[CliCommand(
    Name = "doctor",
    Description = "Diagnose the semantic load: the achieved tier and the per-project reason for any downgrade.",
    ShortFormAutoGenerate = CliNameAutoGenerate.None,
    Parent = typeof(FuseCliCommand))]
public sealed class DoctorCommand
{
    private readonly SemanticIndexer _indexer;
    private readonly IConsoleUI _consoleUI;

    /// <summary>
    ///     Initializes a new instance of the <see cref="DoctorCommand" /> class for CLI option binding only.
    /// </summary>
    /// <remarks>Used by DotMake.CommandLine to bind options; the dependencies are null, so this instance must not run.</remarks>
    public DoctorCommand() : this(null!, null!)
    {
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="DoctorCommand" /> class.
    /// </summary>
    /// <param name="indexer">The semantic indexer, used to diagnose the load.</param>
    /// <param name="consoleUI">The console UI for output.</param>
    public DoctorCommand(SemanticIndexer indexer, IConsoleUI consoleUI)
    {
        _indexer = indexer;
        _consoleUI = consoleUI;
    }

    /// <summary>The workspace directory. Defaults to the current directory.</summary>
    [CliArgument(Description = "The workspace directory. Defaults to the current directory.")]
    public string Path { get; set; } = ".";

    /// <summary>Force a live MSBuild load instead of reporting the diagnosis stamped in the warm index (R43).</summary>
    [CliOption(Description = "Force a live MSBuild load diagnosis instead of reporting the warm index's stamped diagnosis (R43).")]
    public bool Refresh { get; set; }

    /// <summary>
    ///     Runs the doctor command.
    /// </summary>
    /// <param name="context">The CLI invocation context supplying the cancellation token.</param>
    /// <returns>A task that completes when the diagnosis has been written.</returns>
    public async Task RunAsync(CliContext context)
    {
        var root = System.IO.Path.GetFullPath(Path);
        if (!Directory.Exists(root))
        {
            _consoleUI.WriteError($"Directory not found: {root}");
            return;
        }

        // R43: report the diagnosis stamped in the warm index (sub-second, no MSBuild load) unless a live load is
        // forced with --refresh or no stamp is present yet.
        var persisted = Refresh ? null : await PersistedDiagnosisReader.TryReadAsync(root, context.CancellationToken);
        string tier;
        string? selectedSolution;
        string? selectionNote;
        int projectsLoaded;
        int projectsTotal;
        IReadOnlyList<(string Name, bool Loaded, string Reason)> projects;
        IReadOnlyList<DiagnosticRecord> loadDiagnostics;

        if (persisted is not null)
        {
            _consoleUI.WriteStep($"Reading the semantic-load diagnosis stamped in the index for {root}");
            tier = persisted.Tier;
            selectedSolution = persisted.SelectedSolution;
            selectionNote = persisted.SelectionNote;
            projectsLoaded = persisted.ProjectsLoaded;
            projectsTotal = persisted.ProjectsTotal;
            projects = persisted.Projects.Select(p => (p.Name, p.Loaded, p.Reason)).ToList();
            loadDiagnostics = [];
        }
        else
        {
            _consoleUI.WriteStep($"Diagnosing semantic load for {root}");
            var diagnosis = await _indexer.DiagnoseLoadAsync(root, context.CancellationToken);
            tier = diagnosis.Tier;
            selectedSolution = diagnosis.SelectedSolution;
            selectionNote = diagnosis.SelectionNote;
            projectsLoaded = diagnosis.ProjectsLoaded;
            projectsTotal = diagnosis.ProjectsTotal;
            projects = diagnosis.Projects.Select(p => (p.Name, p.Loaded, p.Reason)).ToList();
            loadDiagnostics = diagnosis.Diagnostics;
        }

        var builder = new StringBuilder();
        builder.AppendLine($"workspace: {root}");
        builder.AppendLine(persisted is not null
            ? "diagnosis source: warm index (stamped at index time; --refresh forces a live load)"
            : $"diagnosis source: live MSBuild load{(Refresh ? " (--refresh)" : " (no index stamp yet)")}");
        builder.AppendLine($"load tier: {tier}");
        builder.AppendLine($"selected solution: {selectedSolution ?? "none (syntax-only)"}");
        if (selectionNote is not null)
            builder.AppendLine($"WARNING: {selectionNote}");
        builder.AppendLine($"projects loaded: {projectsLoaded}/{projectsTotal}");
        builder.AppendLine();
        if (projects.Count == 0)
        {
            builder.AppendLine("no projects: the workspace has no solution or project, or none opened; indexing is syntax-only.");
        }
        else
        {
            builder.AppendLine("per project:");
            foreach (var project in projects)
            {
                var mark = project.Loaded ? "ok" : "downgraded";
                builder.AppendLine($"  [{mark}] {project.Name}: {project.Reason}");
            }
        }

        var errors = loadDiagnostics
            .Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
            .ToList();
        if (errors.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("load diagnostics:");
            foreach (var diagnostic in errors.Take(20))
                builder.AppendLine($"  {diagnostic.Code}: {diagnostic.Message}");
        }

        builder.AppendLine();
        builder.AppendLine(tier.StartsWith("oracle", StringComparison.Ordinal)
            ? "The compiler-backed oracle tools can answer for this workspace."
            : "The oracle tools abstain here (not oracle-grade); retrieval still works at the available tier.");

        _consoleUI.WriteResult(builder.ToString());
    }
}
