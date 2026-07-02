using System.Text;
using DotMake.CommandLine;
using Fuse.Cli.Services;
using Fuse.Indexing;
using Fuse.Reduction.Caching;

namespace Fuse.Cli.Commands;

/// <summary>
///     Reports the state of a workspace's index: schema version, mode, status, record counts, full-text search
///     availability, and the database location. Useful for diagnosing a missing or stale index.
/// </summary>
[CliCommand(
    Name = "diagnostics",
    Description = "Report the index state for a workspace (schema, mode, counts, store location).",
    ShortFormAutoGenerate = CliNameAutoGenerate.None,
    Parent = typeof(FuseCliCommand))]
public sealed class DiagnosticsCommand
{
    private readonly IConsoleUI _consoleUI;

    /// <summary>
    ///     Initializes a new instance of the <see cref="DiagnosticsCommand" /> class for CLI option binding only.
    /// </summary>
    /// <remarks>Used by DotMake.CommandLine to bind options; the console UI is null, so this instance must not run.</remarks>
    public DiagnosticsCommand() : this(null!)
    {
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="DiagnosticsCommand" /> class.
    /// </summary>
    /// <param name="consoleUI">The console UI for output.</param>
    public DiagnosticsCommand(IConsoleUI consoleUI) => _consoleUI = consoleUI;

    /// <summary>The workspace directory. Defaults to the current directory.</summary>
    [CliArgument(Description = "The workspace directory. Defaults to the current directory.")]
    public string Path { get; set; } = ".";

    /// <summary>
    ///     Runs the diagnostics command.
    /// </summary>
    /// <param name="context">The CLI invocation context supplying the cancellation token.</param>
    /// <returns>A task that completes when the diagnostics have been written.</returns>
    public async Task RunAsync(CliContext context)
    {
        var root = System.IO.Path.GetFullPath(Path);
        var databasePath = FuseStorePaths.ResolveDatabasePath(root);

        var builder = new StringBuilder();
        builder.AppendLine($"workspace: {root}");
        builder.AppendLine($"store: {databasePath}");
        if (!File.Exists(databasePath))
        {
            builder.AppendLine("index: not built (run 'fuse index')");
            _consoleUI.WriteResult(builder.ToString());
            return;
        }

        await using var store = new WorkspaceIndexStore(databasePath);
        await store.InitializeAsync(context.CancellationToken);
        var state = await store.GetStateAsync(context.CancellationToken);
        var indexedBy = await store.GetMetaAsync(WorkspaceIndexStore.FuseVersionMetaKey, context.CancellationToken);

        builder.AppendLine($"fuse version: {FuseBuildInfo.Current}");
        builder.AppendLine($"indexed by: {indexedBy ?? "(unknown; pre-stamp or rebuilt)"}");
        builder.AppendLine($"schema version: {state.SchemaVersion}");
        builder.AppendLine($"status: {state.Status}");
        builder.AppendLine($"index mode: {state.Mode ?? "(never indexed)"}");
        builder.AppendLine($"files: {state.FileCount}");
        builder.AppendLine($"symbols: {state.SymbolCount}");
        builder.AppendLine($"full-text search: {(store.FullTextSearchAvailable ? "available" : "unavailable")}");

        _consoleUI.WriteResult(builder.ToString());
    }
}
