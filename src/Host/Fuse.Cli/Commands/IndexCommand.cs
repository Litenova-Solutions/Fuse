using DotMake.CommandLine;
using Fuse.Cli.Services;
using Fuse.Indexing;
using Fuse.Reduction.Caching;
using Fuse.Semantics;

namespace Fuse.Cli.Commands;

/// <summary>
///     Builds or refreshes the persistent semantic index for a workspace. This is the syntax-level batch
///     indexer; the MSBuild/Roslyn semantic pass layers on top of it.
/// </summary>
[CliCommand(
    Name = "index",
    Description = "Build or refresh the persistent semantic index for a workspace.",
    ShortFormAutoGenerate = CliNameAutoGenerate.None,
    Parent = typeof(FuseCliCommand))]
public sealed class IndexCommand
{
    private readonly WorkspaceFileScanner _scanner;
    private readonly SyntaxSymbolExtractor _symbolExtractor;
    private readonly SyntaxRouteExtractor _routeExtractor;
    private readonly IConsoleUI _consoleUI;

    /// <summary>
    ///     Initializes a new instance of the <see cref="IndexCommand" /> class for CLI option binding only.
    /// </summary>
    /// <remarks>Used by DotMake.CommandLine to bind options; the dependencies are null, so this instance must not run.</remarks>
    public IndexCommand() : this(null!, null!, null!, null!)
    {
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="IndexCommand" /> class.
    /// </summary>
    /// <param name="scanner">The workspace file scanner.</param>
    /// <param name="symbolExtractor">The syntax symbol and chunk extractor.</param>
    /// <param name="routeExtractor">The syntax route extractor.</param>
    /// <param name="consoleUI">The console UI for status output.</param>
    public IndexCommand(
        WorkspaceFileScanner scanner,
        SyntaxSymbolExtractor symbolExtractor,
        SyntaxRouteExtractor routeExtractor,
        IConsoleUI consoleUI)
    {
        _scanner = scanner;
        _symbolExtractor = symbolExtractor;
        _routeExtractor = routeExtractor;
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

        var databasePath = FuseStorePaths.ResolveDatabasePath(root);
        _consoleUI.WriteStep($"Indexing {root}");
        _consoleUI.WriteStep($"Store: {databasePath}");

        await using var store = new WorkspaceIndexStore(databasePath);
        await store.InitializeAsync(context.CancellationToken);

        var indexer = new SyntaxIndexer(_scanner, store, _symbolExtractor, _routeExtractor);
        var result = await indexer.IndexAsync(root, context.CancellationToken);

        _consoleUI.WriteSuccess(
            $"Indexed {result.FileCount} files: {result.SymbolCount} symbols, {result.ChunkCount} chunks, {result.RouteCount} routes.");
    }
}
