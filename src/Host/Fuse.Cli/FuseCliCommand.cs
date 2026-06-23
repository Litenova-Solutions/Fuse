using DotMake.CommandLine;
using Fuse.Cli.Commands;
using Fuse.Collection.Models;
using Fuse.Fusion;
using Fuse.Plugins.Abstractions.Options;

namespace Fuse.Cli;

/// <summary>
///     Root Fuse CLI command. Runs a generic template fusion and parents the <c>dotnet</c>, <c>wiki</c>,
///     <c>init</c>, and <c>mcp</c> subcommands (the latter grouping <c>mcp install</c> and <c>mcp serve</c>).
/// </summary>
[CliCommand(Description = "A flexible file combining tool for developers.")]
public class FuseCliCommand : CommandBase
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="FuseCliCommand" /> class for CLI option binding only.
    /// </summary>
    /// <remarks>
    ///     Used by DotMake.CommandLine to bind options; the base services are <see langword="null" />, so this
    ///     instance must not run fusion. The DI container resolves the service-injecting constructor for execution.
    /// </remarks>
    public FuseCliCommand() : base(null!, null!, null!, null!)
    {
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="FuseCliCommand" /> class.
    /// </summary>
    /// <param name="orchestrator">The fusion orchestrator.</param>
    /// <param name="templateRegistry">The project template registry.</param>
    /// <param name="consoleUI">The console UI for status output.</param>
    /// <param name="skeletonExtractors">Skeleton extractors resolved by file extension.</param>
    public FuseCliCommand(
        FusionOrchestrator orchestrator,
        Fuse.Collection.Templates.ProjectTemplateRegistry templateRegistry,
        Services.IConsoleUI consoleUI,
        Fuse.Plugins.Abstractions.CapabilityRegistry<Fuse.Plugins.Abstractions.Skeleton.ISkeletonExtractor> skeletonExtractors)
        : base(orchestrator, templateRegistry, skeletonExtractors, consoleUI)
    {
    }

    /// <summary>
    ///     Runs a generic fusion using template defaults and the shared CLI options.
    /// </summary>
    /// <param name="context">The CLI invocation context supplying the cancellation token.</param>
    /// <returns>A task that completes when fusion finishes and results have been written and reported.</returns>
    public async Task RunAsync(CliContext context)
    {
        var request = CreateRequestBuilder().Build();
        await ExecuteFusionAsync(request, context.CancellationToken);
    }
}
