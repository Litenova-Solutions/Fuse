using System.Text;
using DotMake.CommandLine;
using Fuse.Cli.Mcp;
using Fuse.Cli.Services;
using Fuse.Indexing;
using Fuse.Reduction.Caching;

namespace Fuse.Cli.Commands;

/// <summary>
///     Exact lookup over the index: symbols by name, files by path, and chunks by full-text. The precise
///     counterpart to <c>localize</c> for when the target name or path is already known. Reads the persistent
///     index; run <c>index</c> first.
/// </summary>
[CliCommand(
    Name = "find",
    Description = "Find symbols, files, or text in the index by exact name/path/full-text.",
    ShortFormAutoGenerate = CliNameAutoGenerate.None,
    Parent = typeof(FuseCliCommand))]
public sealed class FindCommand
{
    private const int Limit = 50;

    private readonly IConsoleUI _consoleUI;

    /// <summary>
    ///     Initializes a new instance of the <see cref="FindCommand" /> class for CLI option binding only.
    /// </summary>
    /// <remarks>Used by DotMake.CommandLine to bind options; the console UI is null, so this instance must not run.</remarks>
    public FindCommand() : this(null!)
    {
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="FindCommand" /> class.
    /// </summary>
    /// <param name="consoleUI">The console UI for output.</param>
    public FindCommand(IConsoleUI consoleUI) => _consoleUI = consoleUI;

    /// <summary>The query to look up.</summary>
    [CliArgument(Description = "The name, path fragment, or text to find.")]
    public string Query { get; set; } = string.Empty;

    /// <summary>The workspace directory.</summary>
    [CliOption(Required = false, Description = "The workspace directory. Defaults to the current directory.")]
    public string Path { get; set; } = ".";

    /// <summary>Restrict the lookup to one kind: symbol, path, or text. Default searches all.</summary>
    [CliOption(Description = "Restrict to one kind: symbol, path, or text. Default: all.")]
    public string Kind { get; set; } = "all";

    /// <summary>
    ///     Runs the find command.
    /// </summary>
    /// <param name="context">The CLI invocation context supplying the cancellation token.</param>
    /// <returns>A task that completes when the matches have been written.</returns>
    public async Task RunAsync(CliContext context)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(Query))
            {
                FuseOperationalErrors.WriteCliError(
                    _consoleUI,
                    FuseOperationalErrors.Format(FuseOperationalErrors.ValidationErrorPrefix, "specify a query to find."));
                return;
            }

            var root = System.IO.Path.GetFullPath(Path);
            var databasePath = FuseStorePaths.ResolveDatabasePath(root);
            if (!File.Exists(databasePath))
            {
                FuseOperationalErrors.WriteCliError(_consoleUI, FuseOperationalErrors.FormatIndexNotBuilt(databasePath));
                return;
            }

            await using var store = await IndexCoordinator.Default.OpenForReadOnlyAsync(root, context.CancellationToken);

            var kind = Kind.Trim().ToLowerInvariant();
            var builder = new StringBuilder();

            if (kind is "all" or "symbol")
            {
                var symbols = await store.FindSymbolsByNameAsync(Query, Limit, context.CancellationToken);
                builder.AppendLine($"symbols ({symbols.Count}):");
                foreach (var symbol in symbols)
                    builder.AppendLine($"  {symbol.Kind,-11} {symbol.FullyQualifiedName}  ({symbol.FilePath}:{symbol.StartLine})");
            }

            if (kind is "all" or "path")
            {
                var files = await store.FindFilesByPathAsync(Query, Limit, context.CancellationToken);
                builder.AppendLine($"paths ({files.Count}):");
                foreach (var file in files)
                    builder.AppendLine($"  {file.NormalizedPath}");
            }

            if (kind is "all" or "text")
            {
                var hits = await store.SearchAsync(new SearchQuery(Query, Limit), context.CancellationToken);
                builder.AppendLine($"text ({hits.Count}):");
                foreach (var hit in hits)
                    builder.AppendLine($"  {hit.Score:F2}  {hit.Name ?? hit.Kind}  ({hit.FilePath}:{hit.StartLine})");
            }

            _consoleUI.WriteResult(builder.ToString());
        }
        catch (Exception ex)
        {
            FuseOperationalErrors.ReportCli(_consoleUI, ex);
        }
    }
}
