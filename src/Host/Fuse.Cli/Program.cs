using DotMake.CommandLine;
using Fuse.Cli;
using Fuse.Cli.Commands;
using Fuse.Cli.Services;
using Fuse.Fusion.Extensions;
using Microsoft.Extensions.DependencyInjection;

#if FUSE_ROSLYN
var semanticRequested = SemanticModeDetector.IsRequested(args);
#endif

Cli.Ext.ConfigureServices(services =>
{
    services.AddSingleton<IConsoleUI, ConsoleUI>();
    services.AddFuse();

#if FUSE_ROSLYN
    // Opt-in Roslyn precision tier. Registered after AddFuse so it wins capability resolution for .cs. Not
    // referenced in the Native AOT build, so this block is compiled out there and the regex tier is always used.
    if (semanticRequested)
        Fuse.Plugins.Languages.CSharp.Roslyn.Extensions.RoslynServiceCollectionExtensions.AddCSharpRoslyn(services);
#endif

    services.AddTransient<FuseCliCommand>();
    services.AddTransient<DotNetCommand>();
    services.AddTransient<AzureDevOpsWikiCommand>();
    services.AddTransient<InitCommand>();
    services.AddTransient<McpServeCommand>();
    services.AddTransient<ExplainCommand>();
    services.AddTransient<VerifyCommand>();
});

await Cli.RunAsync<FuseCliCommand>(args);
