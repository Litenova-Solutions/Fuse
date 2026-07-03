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

        _consoleUI.WriteStep($"Diagnosing semantic load for {root}");
        var diagnosis = await _indexer.DiagnoseLoadAsync(root, context.CancellationToken);

        var builder = new StringBuilder();
        builder.AppendLine($"workspace: {root}");
        builder.AppendLine($"load tier: {diagnosis.Tier}");
        builder.AppendLine($"projects loaded: {diagnosis.ProjectsLoaded}/{diagnosis.ProjectsTotal}");
        builder.AppendLine();
        if (diagnosis.Projects.Count == 0)
        {
            builder.AppendLine("no projects: the workspace has no solution or project, or none opened; indexing is syntax-only.");
        }
        else
        {
            builder.AppendLine("per project:");
            foreach (var project in diagnosis.Projects)
            {
                var mark = project.Loaded ? "ok" : "downgraded";
                builder.AppendLine($"  [{mark}] {project.Name}: {project.Reason}");
            }
        }

        var errors = diagnosis.Diagnostics
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
        builder.AppendLine(diagnosis.Tier.StartsWith("oracle", StringComparison.Ordinal)
            ? "The compiler-backed oracle tools can answer for this workspace."
            : "The oracle tools abstain here (not oracle-grade); retrieval still works at the available tier.");

        _consoleUI.WriteResult(builder.ToString());
    }
}
