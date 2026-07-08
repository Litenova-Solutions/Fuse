using DotMake.CommandLine;
using Fuse.Cli.Services;
using Fuse.Indexing;
using Fuse.Reduction.Caching;
using Fuse.Semantics;

namespace Fuse.Cli.Commands;

/// <summary>
///     Builds or refreshes the persistent semantic index for a workspace. Loads the workspace through
///     MSBuild/Roslyn when possible and falls back to syntax-level indexing otherwise.
/// </summary>
[CliCommand(
    Name = "index",
    Description = "Build or refresh the persistent semantic index for a workspace.",
    ShortFormAutoGenerate = CliNameAutoGenerate.None,
    Parent = typeof(FuseCliCommand))]
public sealed class IndexCommand
{
    private readonly SemanticIndexer _indexer;
    private readonly IConsoleUI _consoleUI;

    /// <summary>
    ///     Initializes a new instance of the <see cref="IndexCommand" /> class for CLI option binding only.
    /// </summary>
    /// <remarks>Used by DotMake.CommandLine to bind options; the dependencies are null, so this instance must not run.</remarks>
    public IndexCommand() : this(null!, null!)
    {
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="IndexCommand" /> class.
    /// </summary>
    /// <param name="indexer">The semantic indexer.</param>
    /// <param name="consoleUI">The console UI for status output.</param>
    public IndexCommand(SemanticIndexer indexer, IConsoleUI consoleUI)
    {
        _indexer = indexer;
        _consoleUI = consoleUI;
    }

    /// <summary>The workspace directory to index. Defaults to the current directory.</summary>
    [CliArgument(Description = "The workspace directory to index. Defaults to the current directory.")]
    public string Path { get; set; } = ".";

    /// <summary>Whether to force a full re-index rather than reuse unchanged data.</summary>
    [CliOption(Description = "Force a full re-index.")]
    public bool Force { get; set; }

    /// <summary>
    ///     Runs the index command.
    /// </summary>
    /// <param name="context">The CLI invocation context supplying the cancellation token.</param>
    /// <returns>A task that completes when indexing finishes.</returns>
    public async Task RunAsync(CliContext context)
    {
        var root = System.IO.Path.GetFullPath(Path);
        if (!Directory.Exists(root))
        {
            _consoleUI.WriteError($"Directory not found: {root}");
            return;
        }

        // Surface an available update (cache-first, no auto-update on a one-shot CLI command). The background
        // refresh overlaps the indexing pass, so the cache is warm for the next run.
        FuseUpdatePrompt.Emit(_consoleUI.WriteStep, allowAutoUpdate: false);

        var databasePath = FuseStorePaths.ResolveDatabasePath(root);
        _consoleUI.WriteStep($"Indexing {root}");
        _consoleUI.WriteStep($"Store: {databasePath}");

        await using var store = new WorkspaceIndexStore(databasePath);
        await store.InitializeAsync(context.CancellationToken);

        var result = await _indexer.IndexAsync(root, store, context.CancellationToken);

        _consoleUI.WriteSuccess(
            $"Indexed [{result.Mode}] {result.FileCount} files, {result.ProjectCount} projects: " +
            $"{result.SymbolCount} symbols, {result.ChunkCount} chunks, {result.RouteCount} routes.");
        foreach (var diagnostic in result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
            _consoleUI.WriteStep($"  {diagnostic.Code}: {diagnostic.Message}");
    }
}
