using DotMake.CommandLine;
using Fuse.Cli.Services;
using Fuse.Cli.Verification;
using Fuse.Collection;
using Fuse.Collection.Models;
using Fuse.Collection.Templates;
using Fuse.Emission.Serialization;
using Fuse.Fusion;
using Fuse.Fusion.Scoping;
using Fuse.Plugins.Abstractions;
using Fuse.Plugins.Abstractions.Options;
using Fuse.Plugins.Abstractions.Skeleton;

namespace Fuse.Cli.Commands;

/// <summary>
///     Opt-in command that reports how much of a project's public API surface (public and protected types
///     and methods, plus ASP.NET routes) survives a .NET fusion.
/// </summary>
/// <remarks>
///     Runs a fusion in memory, then compares the public API declared in the included source files against
///     the fused output. The source side is parsed by Roslyn in the framework-dependent tool and by an
///     AOT-clean regex analyzer in the Native AOT build; the fused side is matched by text presence so that
///     skeleton output (signatures without bodies) is handled. A drop in type or method preservation under
///     the default or <c>--all</c> reduction signals lost API; skeleton is signatures only by design.
/// </remarks>
[CliCommand(Name = "verify", Description = "Report the preserved percent of public types, methods, and routes after a .NET fusion.", Parent = typeof(FuseCliCommand))]
public sealed class VerifyCommand : CommandBase
{
    private readonly FileCollectionPipeline _collectionPipeline;

    /// <summary>
    ///     Initializes a new instance of the <see cref="VerifyCommand" /> class for CLI option binding only.
    /// </summary>
    public VerifyCommand() : base(null!, null!, null!, null!)
    {
        _collectionPipeline = null!;
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="VerifyCommand" /> class.
    /// </summary>
    /// <param name="orchestrator">The fusion orchestrator.</param>
    /// <param name="templateRegistry">The project template registry.</param>
    /// <param name="consoleUI">The console UI for status output.</param>
    /// <param name="skeletonExtractors">Skeleton extractors resolved by file extension.</param>
    /// <param name="collectionPipeline">The collection pipeline used to enumerate candidate files.</param>
    public VerifyCommand(
        FusionOrchestrator orchestrator,
        ProjectTemplateRegistry templateRegistry,
        IConsoleUI consoleUI,
        CapabilityRegistry<ISkeletonExtractor> skeletonExtractors,
        FileCollectionPipeline collectionPipeline)
        : base(orchestrator, templateRegistry, skeletonExtractors, consoleUI)
    {
        _collectionPipeline = collectionPipeline;
    }

    /// <summary>
    ///     Runs the verification: fuses in memory, then measures preservation of the public API surface.
    /// </summary>
    /// <param name="context">The CLI invocation context supplying the cancellation token.</param>
    /// <returns>A task that completes when the report has been printed.</returns>
    public async Task RunAsync(CliContext context)
    {
        var builder = CreateRequestBuilder(ProjectTemplate.DotNet)
            .WithReductionOptions(new ReductionOptions(
                trimContent: true,
                useCondensing: true,
                removeCSharpComments: All,
                removeCSharpUsings: All,
                removeCSharpNamespaces: All,
                removeCSharpRegions: All,
                aggressiveCSharpReduction: All,
                skeletonMode: Skeleton,
                enableRedaction: !NoRedact))
            .WithInMemory(true);

        if (!string.IsNullOrWhiteSpace(Focus))
            builder.WithFocusOptions(new FocusOptions(Focus, Depth));

        if (!string.IsNullOrWhiteSpace(Query))
            builder.WithQueryOptions(new QueryOptions(Query, QueryTop, Depth));

        var request = builder.Build();

        try
        {
            var collection = await _collectionPipeline.CollectAsync(request.Collection, request.Parallelism, context.CancellationToken);
            var result = await _orchestrator.FuseAsync(request, context.CancellationToken);

            var fused = result.InMemoryContent ?? string.Empty;
            var includedPaths = result.EmittedFileTokens
                .Select(f => f.Path)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var sources = collection.Files
                .Where(f => includedPaths.Contains(f.NormalizedRelativePath))
                .Select(f => SafeRead(f.FullPath))
                .Where(s => s.Length > 0);

            var analyzer = ApiSurfaceAnalyzerFactory.Create();
            var report = new ApiSurfaceVerifier(analyzer).Verify(sources, fused);

            if (Json)
            {
                Console.Out.WriteLine(VerifyReportSerializer.ToJson(
                    ApiSurfaceAnalyzerFactory.BackendName,
                    report.FileCount,
                    report.Types.Total, report.Types.Preserved,
                    report.Methods.Total, report.Methods.Preserved,
                    report.Routes.Total, report.Routes.Preserved));
                return;
            }

            PrintReport(report);
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

    private void PrintReport(ApiSurfaceReport report)
    {
        _consoleUI.WriteResult($"Verify ({ApiSurfaceAnalyzerFactory.BackendName}): {report.FileCount} files");
        _consoleUI.WriteResult(FormatCategory("Types", report.Types));
        _consoleUI.WriteResult(FormatCategory("Methods", report.Methods));
        _consoleUI.WriteResult(FormatCategory("Routes", report.Routes));
    }

    private static string FormatCategory(string name, ApiCategoryResult category) =>
        $"  {name}: {category.Preserved}/{category.Total} preserved ({category.Ratio * 100:F1}%)";

    private static string SafeRead(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (IOException)
        {
            return string.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    ///     Type name, filename, or relative directory used as a focus seed.
    /// </summary>
    [CliOption(Required = false, Description = "Type name, filename, or relative directory to scope verification around.")]
    public string? Focus { get; set; }

    /// <summary>
    ///     Dependency traversal depth for focus or query scoping.
    /// </summary>
    [CliOption(Description = "Dependency traversal depth for focus or query scoping.")]
    public int Depth { get; set; } = 1;

    /// <summary>
    ///     BM25 query used to scope verification to the most relevant files.
    /// </summary>
    [CliOption(Required = false, Description = "BM25 query to scope verification to the most relevant files.")]
    public string? Query { get; set; }

    /// <summary>
    ///     Number of top-ranked files to seed query scoping.
    /// </summary>
    [CliOption(Description = "Number of top-ranked files to seed query scoping.")]
    public int QueryTop { get; set; } = 10;

    /// <summary>
    ///     Apply all .NET reduction flags before verifying.
    /// </summary>
    [CliOption(Description = "Apply all reduction flags before verifying.")]
    public bool All { get; set; } = false;

    /// <summary>
    ///     Verify skeleton (signatures only) reduction.
    /// </summary>
    [CliOption(Description = "Verify skeleton (signatures only) reduction.")]
    public bool Skeleton { get; set; } = false;

    /// <summary>
    ///     Emit the verification result as JSON to stdout.
    /// </summary>
    [CliOption(Description = "Emit the verification result as JSON to stdout.")]
    public bool Json { get; set; } = false;
}
