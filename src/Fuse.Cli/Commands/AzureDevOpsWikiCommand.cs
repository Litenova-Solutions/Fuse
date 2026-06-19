using DotMake.CommandLine;
using Fuse.Collection.Models;
using Fuse.Fusion;

namespace Fuse.Cli.Commands;

/// <summary>
///     Fuses an Azure DevOps wiki repository (markdown files only).
/// </summary>
[CliCommand(Name = "wiki", Description = "Fuse an Azure DevOps wiki repository (includes only .md files).", Parent = typeof(FuseCliCommand))]
public sealed class AzureDevOpsWikiCommand : CommandBase
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="AzureDevOpsWikiCommand" /> class for CLI binding.
    /// </summary>
    public AzureDevOpsWikiCommand() : base(null!, null!, null!, null!)
    {
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="AzureDevOpsWikiCommand" /> class.
    /// </summary>
    /// <param name="orchestrator">The fusion orchestrator.</param>
    /// <param name="templateRegistry">The project template registry.</param>
    /// <param name="consoleUI">The console UI for status output.</param>
    /// <param name="skeletonExtractors">Skeleton extractors resolved by file extension.</param>
    public AzureDevOpsWikiCommand(
        FusionOrchestrator orchestrator,
        Fuse.Collection.Templates.ProjectTemplateRegistry templateRegistry,
        Fuse.Cli.Services.IConsoleUI consoleUI,
        Fuse.Languages.Abstractions.CapabilityRegistry<Fuse.Languages.Abstractions.Skeleton.ISkeletonExtractor> skeletonExtractors)
        : base(orchestrator, templateRegistry, skeletonExtractors, consoleUI)
    {
    }

    /// <summary>
    ///     Runs the <c>wiki</c> fusion command.
    /// </summary>
    /// <param name="context">The CLI invocation context.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task RunAsync(CliContext context)
    {
        var request = CreateRequestBuilder(ProjectTemplate.AzureDevOpsWiki).Build();
        await ExecuteFusionAsync(request, context.CancellationToken);
    }
}
