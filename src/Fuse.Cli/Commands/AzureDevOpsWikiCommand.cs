using DotMake.CommandLine;
using Fuse.Collection.Models;
using Fuse.Fusion;

namespace Fuse.Cli.Commands;

[CliCommand(Name = "wiki", Description = "Fuse an Azure DevOps wiki repository (includes only .md files).", Parent = typeof(FuseCliCommand))]
public sealed class AzureDevOpsWikiCommand : CommandBase
{
    public AzureDevOpsWikiCommand() : base(null!, null!, null!)
    {
    }

    public AzureDevOpsWikiCommand(
        FusionOrchestrator orchestrator,
        Fuse.Collection.Templates.ProjectTemplateRegistry templateRegistry,
        Fuse.Cli.Services.IConsoleUI consoleUI) : base(orchestrator, templateRegistry, consoleUI)
    {
    }

    public async Task RunAsync(CliContext context)
    {
        var request = CreateRequestBuilder(ProjectTemplate.AzureDevOpsWiki).Build();
        await ExecuteFusionAsync(request, context.CancellationToken);
    }
}
