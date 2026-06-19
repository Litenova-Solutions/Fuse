using DotMake.CommandLine;
using Fuse.Cli.Commands;
using Fuse.Collection.Models;
using Fuse.Fusion;
using Fuse.Languages.Abstractions.Options;

namespace Fuse.Cli;

/// <summary>
///     Root Fuse CLI command with shared fusion options.
/// </summary>
[CliCommand(Description = "A flexible file combining tool for developers.")]
public class FuseCliCommand : CommandBase
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="FuseCliCommand" /> class for CLI binding.
    /// </summary>
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
        Fuse.Languages.Abstractions.CapabilityRegistry<Fuse.Languages.Abstractions.Skeleton.ISkeletonExtractor> skeletonExtractors)
        : base(orchestrator, templateRegistry, skeletonExtractors, consoleUI)
    {
    }

    /// <summary>
    ///     Runs a generic fusion using template defaults and shared CLI options.
    /// </summary>
    /// <param name="context">The CLI invocation context.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task RunAsync(CliContext context)
    {
        var request = CreateRequestBuilder().Build();
        await ExecuteFusionAsync(request, context.CancellationToken);
    }
}
