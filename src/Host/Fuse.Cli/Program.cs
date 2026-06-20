using DotMake.CommandLine;
using Fuse.Cli;
using Fuse.Cli.Commands;
using Fuse.Cli.Services;
using Fuse.Fusion.Extensions;
using Microsoft.Extensions.DependencyInjection;

Cli.Ext.ConfigureServices(services =>
{
    services.AddSingleton<IConsoleUI, ConsoleUI>();
    services.AddFuse();

    services.AddTransient<FuseCliCommand>();
    services.AddTransient<DotNetCommand>();
    services.AddTransient<AzureDevOpsWikiCommand>();
    services.AddTransient<InitCommand>();
    services.AddTransient<McpServeCommand>();
    services.AddTransient<ExplainCommand>();
    services.AddTransient<VerifyCommand>();
});

await Cli.RunAsync<FuseCliCommand>(args);
