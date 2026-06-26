using System.Text;
using DotMake.CommandLine;
using Fuse.Cli.Services;
using Fuse.Indexing;
using Fuse.Reduction.Caching;
using Fuse.Retrieval;

namespace Fuse.Cli.Commands;

/// <summary>
///     Localizes a task to ranked candidate files and symbols (no source bodies). The cheap first step of an
///     iterative workflow; follow with <c>context</c> to read the selected seeds. Reads the persistent index;
///     run <c>index</c> first.
/// </summary>
[CliCommand(
    Name = "localize",
    Description = "Localize a task to ranked candidate files and symbols (no bodies).",
    ShortFormAutoGenerate = CliNameAutoGenerate.None,
    Parent = typeof(FuseCliCommand))]
public sealed class LocalizeCommand
{
    private readonly IConsoleUI _consoleUI;
    private readonly IChangeSource _changeSource;
    private readonly Fuse.Plugins.Abstractions.Scoping.ITextEmbedder? _embedder;

    /// <summary>
    ///     Initializes a new instance of the <see cref="LocalizeCommand" /> class for CLI option binding only.
    /// </summary>
    /// <remarks>Used by DotMake.CommandLine to bind options; the dependencies are null, so this instance must not run.</remarks>
    public LocalizeCommand() : this(null!, null!)
    {
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="LocalizeCommand" /> class.
    /// </summary>
    /// <param name="consoleUI">The console UI for output.</param>
    /// <param name="changeSource">The change source for resolving a git base ref.</param>
    /// <param name="embedder">An optional text embedder; when present, the dense retrieval channel is added.</param>
    public LocalizeCommand(
        IConsoleUI consoleUI,
        IChangeSource changeSource,
        Fuse.Plugins.Abstractions.Scoping.ITextEmbedder? embedder = null)
    {
        _consoleUI = consoleUI;
        _changeSource = changeSource;
        _embedder = embedder;
    }

    /// <summary>The workspace directory. Defaults to the current directory.</summary>
    [CliArgument(Description = "The workspace directory. Defaults to the current directory.")]
    public string Path { get; set; } = ".";

    /// <summary>The free-text task or query.</summary>
    [CliOption(Required = false, Description = "The task or query to localize.")]
    public string? Task { get; set; }

    /// <summary>A route to resolve ("METHOD /pattern").</summary>
    [CliOption(Required = false, Description = "A route to resolve.")]
    public string? Route { get; set; }

    /// <summary>A symbol to focus on.</summary>
    [CliOption(Required = false, Description = "A symbol to focus on.")]
    public string? Symbol { get; set; }

    /// <summary>A service to resolve.</summary>
    [CliOption(Required = false, Description = "A service to resolve.")]
    public string? Service { get; set; }

    /// <summary>A request or command to resolve.</summary>
    [CliOption(Required = false, Description = "A request/command to resolve.")]
    public string? Request { get; set; }

    /// <summary>A config section to resolve.</summary>
    [CliOption(Required = false, Description = "A config section to resolve.")]
    public string? Config { get; set; }

    /// <summary>A git base ref whose changed files seed the candidates.</summary>
    [CliOption(Name = "--changed-since", Required = false, Description = "A git base ref whose changed files seed the candidates.")]
    public string? ChangedSince { get; set; }

    /// <summary>The maximum number of candidates to return.</summary>
    [CliOption(Name = "--max-candidates", Description = "Maximum candidates to return.")]
    public int MaxCandidates { get; set; } = 50;

    /// <summary>
    ///     Runs the localize command.
    /// </summary>
    /// <param name="context">The CLI invocation context supplying the cancellation token.</param>
    /// <returns>A task that completes when the candidates have been written.</returns>
    public async Task RunAsync(CliContext context)
    {
        var root = System.IO.Path.GetFullPath(Path);
        var databasePath = FuseStorePaths.ResolveDatabasePath(root);
        if (!File.Exists(databasePath))
        {
            _consoleUI.WriteError($"No index found at {databasePath}. Run 'fuse index' first.");
            return;
        }

        await using var store = new WorkspaceIndexStore(databasePath);
        await store.InitializeAsync(context.CancellationToken);

        var request = new LocalizationRequest(
            root, Query: Task, ChangedSince: ChangedSince, Route: Route, Focus: Symbol, Service: Service,
            Request: Request, ConfigSection: Config, MaxCandidates: MaxCandidates);
        var result = await new SemanticRetrievalEngine(store, _changeSource, _embedder).LocalizeAsync(request, context.CancellationToken);

        _consoleUI.WriteResult(Format(result));
    }

    private static string Format(LocalizationResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"localize: {result.Candidates.Count} candidates");
        foreach (var candidate in result.Candidates)
        {
            builder.AppendLine($"  {candidate.Score:F3}  {candidate.Path}  (~{candidate.EstimatedTokens} tokens)");
            foreach (var reason in candidate.Reasons)
                builder.AppendLine($"        {reason}");
        }

        foreach (var warning in result.Warnings)
            builder.AppendLine($"  ! {warning}");

        return builder.ToString();
    }
}
