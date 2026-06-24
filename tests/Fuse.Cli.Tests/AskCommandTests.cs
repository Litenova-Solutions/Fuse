using Fuse.Cli.Commands;
using Fuse.Cli.Services;
using Fuse.Collection.Templates;
using Fuse.Fusion;
using Fuse.Plugins.Abstractions;
using Fuse.Plugins.Abstractions.Skeleton;
using Microsoft.Extensions.DependencyInjection;

namespace Fuse.Cli.Tests;

// Item 31: the CLI `ask` command mirrors fuse_ask, routing a task to skeleton/focus/search and falling back
// from focus to search when the named type does not resolve. Exercised end to end through the orchestrator.
public sealed class AskCommandTests : IDisposable
{
    private readonly string _sourceDirectory;
    private readonly string _outputDirectory;
    private readonly ServiceProvider _provider;

    public AskCommandTests()
    {
        _sourceDirectory = Path.Combine(Path.GetTempPath(), "fuse-ask-cmd", Guid.NewGuid().ToString("N"));
        _outputDirectory = Path.Combine(_sourceDirectory, ".out");
        Directory.CreateDirectory(_sourceDirectory);

        File.WriteAllText(Path.Combine(_sourceDirectory, "PaymentService.cs"), """
            public class PaymentService
            {
                public void ProcessPayment() { var token = "pay-token"; }
            }
            """);
        File.WriteAllText(Path.Combine(_sourceDirectory, "CatalogService.cs"), """
            public class CatalogService
            {
                public void ListProducts() { }
            }
            """);

        _provider = new ServiceCollection().AddFuseForTests().BuildServiceProvider();
    }

    [Fact]
    public async Task RunAsync_SearchTask_EmitsScopedOutput()
    {
        var command = CreateCommand();
        command.Task = "where is payment processed";

        await command.RunAsync(CancellationToken.None);

        Assert.Contains("PaymentService", ReadEmitted());
    }

    [Fact]
    public async Task RunAsync_NamedType_FocusesOnThatType()
    {
        var command = CreateCommand();
        command.Task = "explain PaymentService";

        await command.RunAsync(CancellationToken.None);

        var output = ReadEmitted();
        Assert.Contains("PaymentService", output);
    }

    [Fact]
    public async Task RunAsync_UnresolvableNamedType_FallsBackToSearchInsteadOfFailing()
    {
        var command = CreateCommand();
        // A PascalCase type that does not exist routes to focus first; the run must fall back to search and
        // still emit, rather than failing with "matched no collected files".
        command.Task = "explain NonExistentWidgetService";

        await command.RunAsync(CancellationToken.None);

        Assert.NotEmpty(EmittedFiles());
    }

    private AskCommand CreateCommand()
    {
        var command = new AskCommand(
            _provider.GetRequiredService<FusionOrchestrator>(),
            _provider.GetRequiredService<ProjectTemplateRegistry>(),
            new StubConsoleUI(),
            _provider.GetRequiredService<CapabilityRegistry<ISkeletonExtractor>>())
        {
            Directory = _sourceDirectory,
            Output = _outputDirectory,
            Overwrite = true,
            NoManifest = true,
            MaxTokens = 15000,
        };
        return command;
    }

    private string[] EmittedFiles() =>
        Directory.Exists(_outputDirectory)
            ? Directory.GetFiles(_outputDirectory).Select(f => Path.GetFileName(f)).ToArray()
            : [];

    private string ReadEmitted()
    {
        var files = Directory.GetFiles(_outputDirectory);
        return files.Length == 0 ? string.Empty : string.Concat(files.Select(File.ReadAllText));
    }

    public void Dispose()
    {
        _provider.Dispose();
        if (Directory.Exists(_sourceDirectory))
            Directory.Delete(_sourceDirectory, recursive: true);
    }

    private sealed class StubConsoleUI : IConsoleUI
    {
        public void WriteSuccess(string message) { }
        public void WriteStep(string message) { }
        public void WriteResult(string message) { }
        public void WriteError(string message) { }
    }
}
