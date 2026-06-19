using DotMake.CommandLine;
using Fuse.Cli.Commands;
using Fuse.Collection.Models;
using Fuse.Fusion;
using Fuse.Reduction.Options;

namespace Fuse.Cli;

[CliCommand(Description = "A flexible file combining tool for developers.")]
public class FuseCliCommand : CommandBase
{
    public FuseCliCommand() : base(null!, null!, null!)
    {
    }

    public FuseCliCommand(
        FusionOrchestrator orchestrator,
        Fuse.Collection.Templates.ProjectTemplateRegistry templateRegistry,
        Services.IConsoleUI consoleUI) : base(orchestrator, templateRegistry, consoleUI)
    {
    }

    public async Task RunAsync(CliContext context)
    {
        var request = CreateRequestBuilder().Build();
        await ExecuteFusionAsync(request, context.CancellationToken);
    }
}
