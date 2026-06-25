using DotMake.CommandLine;
using Fuse.Cli.Services;
using Fuse.Collection.Models;
using Fuse.Collection.Templates;
using Fuse.Fusion;
using Fuse.Fusion.Scoping;
using Fuse.Plugins.Abstractions;
using Fuse.Plugins.Abstractions.Options;
using Fuse.Plugins.Abstractions.Skeleton;

namespace Fuse.Cli.Commands;

/// <summary>
///     Dry-run command that reports which files a .NET fusion would include and exclude, with a token
///     estimate, without writing any output.
/// </summary>
/// <remarks>
///     Runs collection and scoping exactly as a real run would and reduces in memory to count tokens, but
///     writes nothing to disk. Useful for previewing the effect of focus, query, change, and reduction
///     options before committing to a full run.
/// </remarks>
[CliCommand(Name = "explain", Description = "Preview which files a .NET fusion would include and exclude, with a token estimate. Writes nothing.", Parent = typeof(FuseCliCommand))]
public sealed class ExplainCommand : CommandBase
{
    private readonly IExplainService _explainService;

    /// <summary>
    ///     Initializes a new instance of the <see cref="ExplainCommand" /> class for CLI option binding only.
    /// </summary>
    public ExplainCommand() : base(null!, null!, null!, null!)
    {
        _explainService = null!;
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="ExplainCommand" /> class.
    /// </summary>
    /// <param name="orchestrator">The fusion orchestrator.</param>
    /// <param name="templateRegistry">The project template registry.</param>
    /// <param name="consoleUI">The console UI for status output.</param>
    /// <param name="skeletonExtractors">Skeleton extractors resolved by file extension.</param>
    /// <param name="explainService">The unified explain service.</param>
    public ExplainCommand(
        FusionOrchestrator orchestrator,
        ProjectTemplateRegistry templateRegistry,
        IConsoleUI consoleUI,
        CapabilityRegistry<ISkeletonExtractor> skeletonExtractors,
        IExplainService explainService)
        : base(orchestrator, templateRegistry, skeletonExtractors, consoleUI)
    {
        _explainService = explainService;
    }

    /// <summary>
    ///     Runs the dry-run explanation: collects candidates, applies scoping and in-memory reduction, then
    ///     reports included and excluded files and an estimated token total.
    /// </summary>
    /// <param name="context">The CLI invocation context supplying the cancellation token.</param>
    /// <returns>A task that completes when the explanation has been printed.</returns>
    public async Task RunAsync(CliContext context)
    {
        var builder = CreateRequestBuilder(ProjectTemplate.DotNet)
            .WithReductionOptions(new ReductionOptions(
                level: Level,
                trimContent: true,
                useCondensing: true,
                enableRedaction: !NoRedact))
            .WithInMemory(true);

        if (!string.IsNullOrWhiteSpace(Focus))
            builder.WithFocusOptions(new FocusOptions(Focus, Depth));

        if (!string.IsNullOrWhiteSpace(Query))
            builder.WithQueryOptions(new QueryOptions(Query, QueryTop, Depth));

        var request = builder.Build();

        try
        {
            var preview = await _explainService.PreviewAsync(request, context.CancellationToken);

            var lines = Verification.ExplanationBuilder.Build(
                preview.ScopeDescription,
                request.Emission.TokenizerModel,
                preview.FusionResult.EmittedFileTokens ?? [],
                preview.CollectedPaths);

            foreach (var line in lines)
                _consoleUI.WriteResult(line);
        }
        catch (FusionValidationException ex)
        {
            foreach (var error in ex.Errors)
                _consoleUI.WriteError(error);
        }
        catch (FusionException ex)
        {
            _consoleUI.WriteError($"Error: {ex.Message}");
        }
    }

    /// <summary>
    ///     Type name, filename, or relative directory used as a focus seed.
    /// </summary>
    [CliOption(Required = false, Description = "Type name, filename, or relative directory to scope the preview around.")]
    public string? Focus { get; set; }

    /// <summary>
    ///     Dependency traversal depth for focus or query scoping.
    /// </summary>
    [CliOption(Description = "Dependency traversal depth for focus or query scoping.")]
    public int Depth { get; set; } = 1;

    /// <summary>
    ///     BM25 query used to scope the preview to the most relevant files.
    /// </summary>
    [CliOption(Required = false, Description = "BM25 query to scope the preview to the most relevant files.")]
    public string? Query { get; set; }

    /// <summary>
    ///     Number of top-ranked files to seed query scoping.
    /// </summary>
    [CliOption(Description = "Number of top-ranked files to seed query scoping.")]
    public int QueryTop { get; set; } = 10;

    /// <summary>
    ///     The C# reduction level to estimate: <c>none</c>, <c>standard</c>, <c>aggressive</c>,
    ///     <c>skeleton</c>, or <c>publicApi</c>.
    /// </summary>
    [CliOption(Description = "C# reduction level to estimate: none, standard, aggressive, skeleton, publicApi.")]
    public ReductionLevel Level { get; set; } = ReductionLevel.None;
}
