using DotMake.CommandLine;
using Fuse.Cli.Mcp;
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
    public LocalizeCommand(
        IConsoleUI consoleUI,
        IChangeSource changeSource)
    {
        _consoleUI = consoleUI;
        _changeSource = changeSource;
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
    ///     Whether to apply the strict signal-sufficiency contract: refuse an insufficient request and return
    ///     only a navigation map, instead of a low-confidence best-effort guess.
    /// </summary>
    [CliOption(Name = "--strict", Description = "Refuse an insufficient request and return only a navigation map (best-effort by default).")]
    public bool Strict { get; set; }

    /// <summary>Whether to enrich the selected candidates with their typed-graph neighbors for discovery.</summary>
    [CliOption(Name = "--expand", Description = "Enrich the selected candidates with their typed-graph neighbors (widens recall, pressures precision).")]
    public bool Expand { get; set; }

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
            Request: Request, ConfigSection: Config, MaxCandidates: MaxCandidates, Strict: Strict, ExpandGraph: Expand);
        var result = await new SemanticRetrievalEngine(store, _changeSource).LocalizeAsync(request, context.CancellationToken);

        _consoleUI.WriteResult(LocalizationFormatter.Format(result));
    }
}
