using Fuse.Collection.Options;
using Fuse.Emission.Models;
using Fuse.Fusion.Extensions;
using Fuse.Plugins.Abstractions.Options;
using Microsoft.Extensions.DependencyInjection;

namespace Fuse.Fusion.Tests;

public sealed class FusionOrchestratorSessionTests : IDisposable
{
    private readonly string _sourceDirectory;
    private readonly ServiceProvider _serviceProvider;

    public FusionOrchestratorSessionTests()
    {
        _sourceDirectory = Path.Combine(Path.GetTempPath(), "fuse-session-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sourceDirectory);
        File.WriteAllText(Path.Combine(_sourceDirectory, "A.cs"), "public class A { }");
        File.WriteAllText(Path.Combine(_sourceDirectory, "B.cs"), "public class B { }");

        var services = new ServiceCollection();
        services.AddFuseForTests();
        _serviceProvider = services.BuildServiceProvider();
    }

    private FusionRequest Request(string sessionId) => new(
        new CollectionOptions(_sourceDirectory, extensions: [".cs"]),
        new ReductionOptions(),
        new EmissionOptions { SessionId = sessionId, IncludeManifest = false },
        inMemory: true);

    [Fact]
    public async Task FuseAsync_SecondCallSameSession_OmitsUnchangedFiles()
    {
        var orchestrator = _serviceProvider.GetRequiredService<FusionOrchestrator>();

        var first = await orchestrator.FuseAsync(Request("s1"));
        Assert.Contains("class A", first.InMemoryContent);
        Assert.Contains("class B", first.InMemoryContent);

        var second = await orchestrator.FuseAsync(Request("s1"));

        Assert.Equal(0, second.ProcessedFileCount);
        Assert.Contains("fuse:session-delta", second.InMemoryContent);
        Assert.Contains("A.cs", second.InMemoryContent);
        Assert.DoesNotContain("class A", second.InMemoryContent);
    }

    [Fact]
    public async Task FuseAsync_ChangedFile_IsResentInSession()
    {
        var orchestrator = _serviceProvider.GetRequiredService<FusionOrchestrator>();

        await orchestrator.FuseAsync(Request("s2"));

        // Change A.cs; B.cs is unchanged.
        File.WriteAllText(Path.Combine(_sourceDirectory, "A.cs"), "public class A { public int X; }");

        var second = await orchestrator.FuseAsync(Request("s2"));

        Assert.Contains("class A", second.InMemoryContent); // changed file resent
        Assert.DoesNotContain("class B", second.InMemoryContent); // unchanged file omitted
        Assert.Equal(1, second.ProcessedFileCount);
    }

    [Fact]
    public async Task FuseAsync_ChangedMultiLineFile_IsSentAsDiff()
    {
        var orchestrator = _serviceProvider.GetRequiredService<FusionOrchestrator>();
        var path = Path.Combine(_sourceDirectory, "Multi.cs");
        File.WriteAllText(path,
            "public class Multi\n{\n    public int One() => 1;\n    public int Two() => 2;\n    public int Three() => 3;\n    public int Four() => 4;\n}\n");

        await orchestrator.FuseAsync(Request("sd"));

        // Change one line in the middle; the rest is unchanged.
        File.WriteAllText(path,
            "public class Multi\n{\n    public int One() => 1;\n    public int Two() => 22;\n    public int Three() => 3;\n    public int Four() => 4;\n}\n");

        var second = await orchestrator.FuseAsync(Request("sd"));

        // Multi.cs is re-sent as a unified diff, not the whole file: the changed line shows as a delete and an
        // insert (whitespace-tolerant, since reduction normalizes indentation), and the note records the diff.
        var content = second.InMemoryContent!;
        Assert.Contains("fuse:diff", content);
        Assert.Contains("unified diff", content);
        Assert.Contains("@@ ", content); // a hunk header, so it is a diff
        Assert.Matches(@"\+\s*public int Two\(\) => 22;", content);
        Assert.Matches(@"-\s*public int Two\(\) => 2;", content);
    }

    [Fact]
    public async Task FuseAsync_DifferentSession_SendsEverything()
    {
        var orchestrator = _serviceProvider.GetRequiredService<FusionOrchestrator>();

        await orchestrator.FuseAsync(Request("s3"));
        var other = await orchestrator.FuseAsync(Request("s4"));

        Assert.Contains("class A", other.InMemoryContent);
        Assert.Contains("class B", other.InMemoryContent);
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        if (Directory.Exists(_sourceDirectory))
            Directory.Delete(_sourceDirectory, recursive: true);
    }
}
