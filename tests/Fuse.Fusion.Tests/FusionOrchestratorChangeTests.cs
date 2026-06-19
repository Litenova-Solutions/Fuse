using Fuse.Analysis.Changes;
using Fuse.Collection.Options;
using Fuse.Emission.Models;
using Fuse.Fusion;
using Fuse.Fusion.Extensions;
using Fuse.Languages.Abstractions.Options;
using Microsoft.Extensions.DependencyInjection;

namespace Fuse.Fusion.Tests;

public sealed class FusionOrchestratorChangeTests : IDisposable
{
    private readonly string _repoDirectory;
    private readonly ServiceProvider _serviceProvider;

    public FusionOrchestratorChangeTests()
    {
        _repoDirectory = Path.Combine(Path.GetTempPath(), "fuse-git-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_repoDirectory);

        File.WriteAllText(Path.Combine(_repoDirectory, "Tracked.cs"), "public class Tracked { }");
        File.WriteAllText(Path.Combine(_repoDirectory, "Other.cs"), "public class Other { }");

        RunGit("init");
        RunGit("config user.email test@test.com");
        RunGit("config user.name Test");
        RunGit("add .");
        RunGit("commit -m baseline");

        File.WriteAllText(Path.Combine(_repoDirectory, "Tracked.cs"), "public class Tracked { public int Id; }");

        var services = new ServiceCollection();
        services.AddFuse();
        _serviceProvider = services.BuildServiceProvider();
    }

    [Fact]
    public async Task FuseAsync_WithChangedSince_OnlyEmitsChangedFiles()
    {
        var orchestrator = _serviceProvider.GetRequiredService<FusionOrchestrator>();
        var request = new FusionRequest(
            new CollectionOptions(_repoDirectory, extensions: [".cs"]),
            new ReductionOptions(),
            new EmissionOptions(),
            inMemory: true,
            changes: new ChangeOptions("HEAD", false));

        var result = await orchestrator.FuseAsync(request);

        Assert.NotNull(result.InMemoryContent);
        Assert.Contains("Tracked.cs", result.InMemoryContent);
        Assert.DoesNotContain("Other.cs", result.InMemoryContent);
    }

    private void RunGit(string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git", args)
        {
            WorkingDirectory = _repoDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = System.Diagnostics.Process.Start(psi)!;
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(process.StandardError.ReadToEnd());
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        try
        {
            if (Directory.Exists(_repoDirectory))
                Directory.Delete(_repoDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
