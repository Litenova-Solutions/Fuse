using DotMake.CommandLine;
using Fuse.Cli;
using Fuse.Cli.Commands;
using Fuse.Cli.Services;
using Fuse.Fusion.Extensions;
using Microsoft.Extensions.DependencyInjection;

var embeddingsChoice = EmbeddingsModeDetector.ExplicitFlag(args);

Cli.Ext.ConfigureServices(services =>
{
    services.AddSingleton<IConsoleUI, ConsoleUI>();
    services.AddFuse();
    Fuse.Plugins.Languages.CSharp.Roslyn.Extensions.RoslynServiceCollectionExtensions.AddCSharpRoslyn(services);
    Fuse.Fusion.Embeddings.Onnx.Extensions.OnnxEmbeddingsServiceCollectionExtensions.AddFuseOnnxEmbeddings(services, embeddingsChoice);

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
