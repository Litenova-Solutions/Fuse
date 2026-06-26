using DotMake.CommandLine;
using Fuse.Cli.Services;
using Fuse.Collection.FileSystem;
using Fuse.Context;
using Fuse.Indexing;
using Fuse.Reduction;
using Fuse.Reduction.Caching;
using Fuse.Retrieval;

namespace Fuse.Cli.Commands;

/// <summary>
///     Plans the context for a set of seeds: the files to read, their role and render tier, and why. Reads the
///     persistent index; run <c>index</c> first. Source rendering is added in a later phase; this prints the
///     plan.
/// </summary>
[CliCommand(
    Name = "context",
    Description = "Plan the context (files, roles, tiers, provenance) for a set of seeds.",
    ShortFormAutoGenerate = CliNameAutoGenerate.None,
    Parent = typeof(FuseCliCommand))]
public sealed class ContextCommand
{
    private readonly IConsoleUI _consoleUI;
    private readonly ContentReductionPipeline _reductionPipeline;

    /// <summary>
    ///     Initializes a new instance of the <see cref="ContextCommand" /> class for CLI option binding only.
    /// </summary>
    /// <remarks>Used by DotMake.CommandLine to bind options; the dependencies are null, so this instance must not run.</remarks>
    public ContextCommand() : this(null!, null!)
    {
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="ContextCommand" /> class.
    /// </summary>
    /// <param name="consoleUI">The console UI for output.</param>
    /// <param name="reductionPipeline">The reduction pipeline used to render file bodies.</param>
    public ContextCommand(IConsoleUI consoleUI, ContentReductionPipeline reductionPipeline)
    {
        _consoleUI = consoleUI;
        _reductionPipeline = reductionPipeline;
    }

    /// <summary>The workspace directory. Defaults to the current directory.</summary>
    [CliArgument(Description = "The workspace directory. Defaults to the current directory.")]
    public string Path { get; set; } = ".";

    /// <summary>Named seeds (symbol, service, request, or config) to build context around.</summary>
    [CliOption(Description = "A named seed (symbol/service/request/config). Repeatable.")]
    public string[] Seed { get; set; } = [];

    /// <summary>Route seeds ("METHOD /pattern"). Repeatable.</summary>
    [CliOption(Required = false, Description = "A route seed (\"POST /api/orders/{id}\"). Repeatable.")]
    public string[] Route { get; set; } = [];

    /// <summary>The graph expansion depth.</summary>
    [CliOption(Description = "Graph expansion depth.")]
    public int Depth { get; set; } = 2;

    /// <summary>The token budget.</summary>
    [CliOption(Name = "--max-tokens", Required = false, Description = "Token budget.")]
    public int? MaxTokens { get; set; }

    /// <summary>The output format: xml (default), markdown, or json.</summary>
    [CliOption(Description = "Output format: xml (default), markdown, or json.")]
    public string Format { get; set; } = "xml";

    /// <summary>Print only the plan (files, roles, tiers) without rendering bodies.</summary>
    [CliOption(Name = "--plan-only", Description = "Print the plan without rendering source bodies.")]
    public bool PlanOnly { get; set; }

    /// <summary>
    ///     Runs the context command.
    /// </summary>
    /// <param name="context">The CLI invocation context supplying the cancellation token.</param>
    /// <returns>A task that completes when the context has been written.</returns>
    public async Task RunAsync(CliContext context)
    {
        var root = System.IO.Path.GetFullPath(Path);
        var databasePath = FuseStorePaths.ResolveDatabasePath(root);
        if (!File.Exists(databasePath))
        {
            _consoleUI.WriteError($"No index found at {databasePath}. Run 'fuse index' first.");
            return;
        }

        var seeds = Seed.Select(s => new ContextSeed(ContextSeedKind.Symbol, s))
            .Concat(Route.Select(r => new ContextSeed(ContextSeedKind.Route, r)))
            .ToList();
        if (seeds.Count == 0)
        {
            _consoleUI.WriteError("Specify at least one --seed or --route.");
            return;
        }

        await using var store = new WorkspaceIndexStore(databasePath);
        await store.InitializeAsync(context.CancellationToken);

        var request = new ContextRequest(root, seeds, Depth, MaxTokens);
        var plan = await new SemanticRetrievalEngine(store).PlanContextAsync(request, context.CancellationToken);

        if (PlanOnly)
        {
            _consoleUI.WriteResult(PlanFormatter.Format(plan));
            return;
        }

        var renderer = new SemanticContextRenderer(_reductionPipeline, new SourceContentProvider(new PhysicalFileSystem()));
        var rendered = await renderer.RenderAsync(plan, root, context.CancellationToken);
        var output = SemanticContextEmitter.Emit(plan, rendered, PlanFormatter.ParseFormat(Format), root);
        _consoleUI.WriteResult(output);
    }
}
