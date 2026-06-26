using System.Text;
using DotMake.CommandLine;
using Fuse.Cli.Services;
using Fuse.Indexing;
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

    /// <summary>
    ///     Initializes a new instance of the <see cref="ContextCommand" /> class for CLI option binding only.
    /// </summary>
    /// <remarks>Used by DotMake.CommandLine to bind options; the console UI is null, so this instance must not run.</remarks>
    public ContextCommand() : this(null!)
    {
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="ContextCommand" /> class.
    /// </summary>
    /// <param name="consoleUI">The console UI for output.</param>
    public ContextCommand(IConsoleUI consoleUI) => _consoleUI = consoleUI;

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

    /// <summary>
    ///     Runs the context command.
    /// </summary>
    /// <param name="context">The CLI invocation context supplying the cancellation token.</param>
    /// <returns>A task that completes when the plan has been written.</returns>
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

        _consoleUI.WriteResult(Format(plan));
    }

    private static string Format(ContextPlan plan)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"context plan: {plan.Items.Count} files, ~{plan.EstimatedTokens} tokens");
        foreach (var item in plan.Items)
        {
            var keep = item.MustKeep ? "*" : " ";
            builder.AppendLine($"  {keep} {item.Score:F3} [{item.Role}/{item.Tier}] {item.Path}  (~{item.EstimatedTokens} tokens)");
        }

        foreach (var warning in plan.Warnings)
            builder.AppendLine($"  ! {warning}");

        return builder.ToString();
    }
}
