using DotMake.CommandLine;
using Fuse.Cli.Commands;
using Fuse.Collection.Models;
using Fuse.Fusion;
using Fuse.Languages.Abstractions.Options;

namespace Fuse.Cli;

[CliCommand(Description = "A flexible file combining tool for developers.")]
public class FuseCliCommand : CommandBase
{
    public FuseCliCommand() : base(null!, null!, null!, null!)
    {
    }

    public FuseCliCommand(
        FusionOrchestrator orchestrator,
        Fuse.Collection.Templates.ProjectTemplateRegistry templateRegistry,
        Services.IConsoleUI consoleUI, Fuse.Languages.Abstractions.CapabilityRegistry<Fuse.Languages.Abstractions.Skeleton.ISkeletonExtractor> skeletonExtractors) : base(orchestrator, templateRegistry, skeletonExtractors, consoleUI)
    {
    }

    public async Task RunAsync(CliContext context)
    {
        var request = CreateRequestBuilder().Build();
        await ExecuteFusionAsync(request, context.CancellationToken);
    }
}
