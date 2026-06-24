using DotMake.CommandLine;
using Fuse.Cli;
using Fuse.Cli.Commands;
using Fuse.Cli.Extensions;
using Fuse.Cli.Services;
using Microsoft.Extensions.DependencyInjection;

Cli.Ext.ConfigureServices(services =>
{
    services.AddSingleton<IConsoleUI, ConsoleUI>();
    services.AddFuse();

    services.AddTransient<FuseCliCommand>();
    services.AddTransient<DotNetCommand>();
    services.AddTransient<AzureDevOpsWikiCommand>();
    services.AddTransient<InitCommand>();
    services.AddTransient<McpCommand>();
    services.AddTransient<InstallCommand>();
    services.AddSingleton<McpInstallService>();
    services.AddTransient<McpServeCommand>();
    services.AddTransient<ExplainCommand>();
    services.AddTransient<VerifyCommand>();
    services.AddTransient<ReduceCommand>();
});

await Cli.RunAsync<FuseCliCommand>(args);
